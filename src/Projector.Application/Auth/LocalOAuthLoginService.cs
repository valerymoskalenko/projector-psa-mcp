using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Projector.ApiClient;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;

namespace Projector.Application.Auth;

/// <summary>
/// Browser PKCE login against Projector, writing the DPAPI local OAuth cache.
/// Starts a short-lived HttpListener on 5180 when HTTP MCP is not already running.
/// </summary>
public sealed class LocalOAuthLoginService
{
    private readonly IOptions<ProjectorOptions> _options;
    private readonly IProjectorTokenClient _tokenClient;
    private readonly ILocalOAuthSessionStore _localStore;
    private readonly IProjectorConnectionStore _connectionStore;
    private readonly ProjectorConnectionService _connections;
    private readonly ILogger<LocalOAuthLoginService> _logger;

    public LocalOAuthLoginService(
        IOptions<ProjectorOptions> options,
        IProjectorTokenClient tokenClient,
        ILocalOAuthSessionStore localStore,
        IProjectorConnectionStore connectionStore,
        ProjectorConnectionService connections,
        ILogger<LocalOAuthLoginService> logger)
    {
        _options = options;
        _tokenClient = tokenClient;
        _localStore = localStore;
        _connectionStore = connectionStore;
        _connections = connections;
        _logger = logger;
    }

    public async Task<ProjectorConnection> LoginAsync(
        bool forceLogin = false,
        bool openBrowser = true,
        CancellationToken cancellationToken = default)
    {
        var opts = _options.Value;
        var requested = ProjectorScopes.Normalize(opts.RequestedScopes);

        if (!forceLogin)
        {
            var existing = _localStore.TryLoad(opts.AccountCode, requested);
            if (existing is not null && !existing.IsExpired(TimeSpan.FromMinutes(5)))
            {
                _logger.LogInformation(
                    "Using cached OAuth session for {Account} scope={Scope} granted={Granted}",
                    opts.AccountCode,
                    requested,
                    existing.GrantedScope);
                _connectionStore.Save(existing);
                return existing;
            }

            if (existing is not null && !string.IsNullOrWhiteSpace(existing.RefreshToken))
            {
                try
                {
                    _connectionStore.Save(existing);
                    var refreshed = await _connections.RefreshConnectionAsync(existing, cancellationToken);
                    _localStore.Save(refreshed, opts.AccountCode, requested);
                    _logger.LogInformation(
                        "Refreshed OAuth session for {Account}; granted={Granted}",
                        opts.AccountCode,
                        refreshed.GrantedScope);
                    return refreshed;
                }
                catch (ProjectorApiException ex)
                {
                    _logger.LogWarning(ex, "Refresh failed; starting interactive login.");
                }
            }
        }

        var verifier = Pkce.GenerateCodeVerifier();
        var challenge = Pkce.GetS256CodeChallenge(verifier);
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var redirectUri = opts.RedirectUri;

        var authorizeBase =
            $"{opts.AuthorizeBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(opts.AccountCode)}";

        var authorizeUrl =
            $"{authorizeBase}" +
            $"?response_type=code" +
            $"&client_id={Uri.EscapeDataString(opts.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&scope={Uri.EscapeDataString(requested)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            $"&code_challenge_method=S256";

        _logger.LogInformation(
            "Starting Projector OAuth login; requestedScope={Requested} redirect={Redirect}",
            requested,
            redirectUri);

        var (code, returnedState) = await CaptureAuthorizationCodeAsync(
            redirectUri,
            authorizeUrl,
            openBrowser,
            cancellationToken);

        if (!string.Equals(returnedState, state, StringComparison.Ordinal))
        {
            throw new ProjectorAuthorizationException("OAuth state mismatch.");
        }

        var token = await _tokenClient.ExchangeAuthorizationCodeAsync(
            code,
            redirectUri,
            verifier,
            cancellationToken);

        var connectionId = Guid.NewGuid().ToString("N");
        var connection = _connections.CreateFromToken(connectionId, token);
        _localStore.Save(connection, opts.AccountCode, requested);

        _logger.LogInformation(
            "OAuth login complete; requested={Requested} granted={Granted}",
            requested,
            connection.GrantedScope);

        return connection;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var opts = _options.Value;
        var requested = ProjectorScopes.Normalize(opts.RequestedScopes);
        var existing = _localStore.TryLoad(opts.AccountCode, requested);
        if (existing is not null)
        {
            try
            {
                await _tokenClient.RevokeAsync(existing, cancellationToken);
            }
            catch (ProjectorApiException)
            {
                // best effort
            }

            _connectionStore.Remove(existing.ConnectionId);
        }

        _localStore.Clear(opts.AccountCode, requested);
    }

    /// <summary>
    /// Ensures a usable connection id for CLI/stdio tool calls (cache or refresh).
    /// </summary>
    public async Task<string> RequireLocalConnectionIdAsync(CancellationToken cancellationToken = default)
    {
        var connection = await LoginAsync(forceLogin: false, openBrowser: false, cancellationToken);
        return connection.ConnectionId;
    }

    private static async Task<(string Code, string State)> CaptureAuthorizationCodeAsync(
        string redirectUri,
        string authorizeUrl,
        bool openBrowser,
        CancellationToken cancellationToken)
    {
        var redirect = new Uri(redirectUri);
        // Bind root of host so /oauth/projector/callback works on 5180.
        var listenPrefix = $"{redirect.Scheme}://{redirect.Authority}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(listenPrefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new ProjectorAuthorizationException(
                $"Cannot bind {listenPrefix} for OAuth callback (is 'serve --http' already running on 5180?). {ex.Message}");
        }

        if (openBrowser)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = authorizeUrl, UseShellExecute = true });
            }
            catch
            {
                Console.Error.WriteLine($"Open this URL in a browser:{Environment.NewLine}{authorizeUrl}");
            }
        }
        else
        {
            Console.Error.WriteLine($"Open this URL in a browser:{Environment.NewLine}{authorizeUrl}");
        }

        using var reg = cancellationToken.Register(() =>
        {
            try { listener.Stop(); } catch { /* ignore */ }
        });

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        // Wait until we get the Projector callback path (ignore other probes).
        while (!string.Equals(
                   context.Request.Url?.AbsolutePath.TrimEnd('/'),
                   redirect.AbsolutePath.TrimEnd('/'),
                   StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        }

        var query = ParseQuery(context.Request.Url?.Query);
        query.TryGetValue("code", out var code);
        query.TryGetValue("state", out var state);
        query.TryGetValue("error", out var error);
        code ??= "";
        state ??= "";

        var html = string.IsNullOrEmpty(error)
            ? "<html><body><h1>Projector OAuth complete</h1><p>You can close this window.</p></body></html>"
            : $"<html><body><h1>OAuth error</h1><p>{WebUtility.HtmlEncode(error)}</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.Close();
        listener.Stop();

        if (!string.IsNullOrEmpty(error))
        {
            throw new ProjectorAuthorizationException($"Projector authorization error: {error}");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ProjectorAuthorizationException("OAuth callback missing code.");
        }

        return (code, state);
    }

    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        var q = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx < 0)
            {
                result[Uri.UnescapeDataString(part)] = "";
            }
            else
            {
                var key = Uri.UnescapeDataString(part[..idx]);
                var value = Uri.UnescapeDataString(part[(idx + 1)..]);
                result[key] = value;
            }
        }

        return result;
    }
}
