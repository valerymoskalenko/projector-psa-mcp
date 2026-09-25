using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Projector.Domain.Auth;

namespace Projector.Application.Auth;

/// <summary>
/// Reads/writes the same DPAPI cache files as PowerShell ProjectorSoap.ps1
/// under %LOCALAPPDATA%\ProjectorMcp\oauth-sessions.
/// </summary>
public sealed class DpapiLocalOAuthSessionStore : ILocalOAuthSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string? _overrideDirectory;
    private readonly Dictionary<string, OAuthCachePayload> _memory = new(StringComparer.Ordinal);

    public DpapiLocalOAuthSessionStore(string? overrideDirectory = null)
    {
        _overrideDirectory = overrideDirectory;
    }

    public string GetCacheDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_overrideDirectory))
        {
            return _overrideDirectory;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "ProjectorMcp", "oauth-sessions");
    }

    public ProjectorConnection? TryLoad(string accountCode, string? requestedScope = null)
    {
        var key = ProjectorScopes.CacheKey(accountCode, requestedScope);
        if (_memory.TryGetValue(key, out var cached))
        {
            return ToConnection(cached, key);
        }

        var path = GetFilePath(key);
        var loaded = TryReadFile(path, key);
        if (loaded is not null)
        {
            return loaded;
        }

        // Fall back to any non-mock cache for this account (legacy tag-scope filenames).
        return TryLoadAnyLiveForAccount(accountCode);
    }

    private ProjectorConnection? TryLoadAnyLiveForAccount(string accountCode)
    {
        var dir = GetCacheDirectory();
        if (!Directory.Exists(dir))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.bin"))
        {
            try
            {
                var protectedBytes = File.ReadAllBytes(file);
                var plain = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(plain);
                var payload = JsonSerializer.Deserialize<OAuthCachePayload>(json, JsonOptions);
                if (payload is null
                    || !string.Equals(payload.AccountCode, accountCode, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(payload.AccessToken)
                    || string.IsNullOrWhiteSpace(payload.SoapAuthority)
                    || payload.SoapAuthority.Contains("mock", StringComparison.OrdinalIgnoreCase)
                    || !payload.SoapAuthority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key = ProjectorScopes.CacheKey(payload.AccountCode, payload.RequestedScope);
                _memory[key] = payload;
                return ToConnection(payload, key);
            }
            catch (CryptographicException)
            {
                // skip
            }
            catch (JsonException)
            {
                // skip
            }
        }

        return null;
    }

    private ProjectorConnection? TryReadFile(string path, string key)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plain);
            var payload = JsonSerializer.Deserialize<OAuthCachePayload>(json, JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                return null;
            }

            // Prefer not to use a mock authority when a live fallback exists.
            if (payload.SoapAuthority.Contains("mock", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _memory[key] = payload;
            return ToConnection(payload, key);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(ProjectorConnection connection, string accountCode, string requestedScope)
    {
        var normalized = ProjectorScopes.Normalize(requestedScope);
        var key = ProjectorScopes.CacheKey(accountCode, normalized);
        var payload = new OAuthCachePayload
        {
            AccountCode = accountCode,
            RequestedScope = normalized,
            GrantedScope = connection.GrantedScope,
            AccessToken = connection.SessionTicket,
            RefreshToken = connection.RefreshToken,
            SoapAuthority = connection.SoapServiceAuthority.TrimEnd('/'),
            RestAuthority = connection.RestServiceAuthority.TrimEnd('/'),
            ExpiresAtUtc = connection.ExpiresAt.UtcDateTime.ToString("o"),
            ObtainedAtUtc = DateTime.UtcNow.ToString("o")
        };

        _memory[key] = payload;

        var dir = GetCacheDirectory();
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var plain = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var path = GetFilePath(key);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, path, overwrite: true);
    }

    public bool Clear(string accountCode, string? requestedScope = null)
    {
        var key = ProjectorScopes.CacheKey(accountCode, requestedScope);
        _memory.Remove(key);
        var path = GetFilePath(key);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private string GetFilePath(string cacheKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey));
        var name = Convert.ToHexString(hash).ToLowerInvariant() + ".bin";
        return Path.Combine(GetCacheDirectory(), name);
    }

    private static ProjectorConnection ToConnection(OAuthCachePayload payload, string cacheKey)
    {
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddDays(7);
        if (!string.IsNullOrWhiteSpace(payload.ExpiresAtUtc)
            && DateTimeOffset.TryParse(payload.ExpiresAtUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            expires = parsed.ToUniversalTime();
        }

        return new ProjectorConnection
        {
            ConnectionId = "local|" + cacheKey,
            SessionTicket = payload.AccessToken,
            RefreshToken = payload.RefreshToken,
            ExpiresAt = expires,
            GrantedScope = string.IsNullOrWhiteSpace(payload.GrantedScope)
                ? payload.RequestedScope
                : payload.GrantedScope,
            SoapServiceAuthority = payload.SoapAuthority.TrimEnd('/'),
            RestServiceAuthority = payload.RestAuthority.TrimEnd('/'),
            DisplayName = null
        };
    }
}
