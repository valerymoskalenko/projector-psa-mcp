using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;

namespace Projector.ApiClient.OAuth;

public sealed class ProjectorTokenClient : IProjectorTokenClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ProjectorOptions _options;
    private readonly ILogger<ProjectorTokenClient> _logger;

    public ProjectorTokenClient(
        HttpClient http,
        IOptions<ProjectorOptions> options,
        ILogger<ProjectorTokenClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ProjectorTokenResponse> ExchangeAuthorizationCodeAsync(
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        // Projector documents grant_type=code (not authorization_code).
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "code",
            ["code"] = code,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        };

        return await PostTokenAsync(_options.TokenUrl, form, cancellationToken);
    }

    public async Task<ProjectorTokenResponse> RefreshAsync(
        ProjectorConnection connection,
        CancellationToken cancellationToken = default)
    {
        var tokenUrl = string.IsNullOrWhiteSpace(connection.RestServiceAuthority)
            ? _options.TokenUrl
            : $"{connection.RestServiceAuthority.TrimEnd('/')}/oauth2token";

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = connection.RefreshToken,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret
        };

        return await PostTokenAsync(tokenUrl, form, cancellationToken);
    }

    public async Task RevokeAsync(
        ProjectorConnection connection,
        CancellationToken cancellationToken = default)
    {
        var revokeUrl = string.IsNullOrWhiteSpace(connection.RestServiceAuthority)
            ? _options.RevokeUrl
            : $"{connection.RestServiceAuthority.TrimEnd('/')}/oauth2revoketoken";

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["token"] = connection.RefreshToken,
            ["token_type"] = "refresh_token"
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(revokeUrl, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Projector revoke failed with {Status}", response.StatusCode);
            throw new ProjectorApiException($"Token revoke failed: {response.StatusCode}. {body}");
        }
    }

    private async Task<ProjectorTokenResponse> PostTokenAsync(
        string url,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);

        _logger.LogInformation(
            "POST {TokenUrl} grant_type={GrantType} redirect_uri={RedirectUri} code_verifier_len={VerifierLen} client_id_len={ClientIdLen}",
            url,
            form.GetValueOrDefault("grant_type"),
            form.GetValueOrDefault("redirect_uri"),
            form.GetValueOrDefault("code_verifier")?.Length ?? 0,
            form.GetValueOrDefault("client_id")?.Length ?? 0);

        using var response = await _http.PostAsync(url, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var safeBody = RedactSecrets(body);
            _logger.LogWarning(
                "Projector token endpoint returned {Status}: {Body}",
                response.StatusCode,
                safeBody);
            throw new ProjectorApiException(
                $"Token request failed: {response.StatusCode}. {safeBody}");
        }

        var token = JsonSerializer.Deserialize<ProjectorTokenResponse>(body, JsonOptions)
            ?? throw new ProjectorApiException("Token response was empty.");

        if (!string.Equals(token.TokenType, "projector_session_ticket", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Unexpected token_type {TokenType}", token.TokenType);
        }

        _logger.LogInformation(
            "Projector token acquired: scope={Scope} soap={Soap} rest={Rest} expires_in={ExpiresIn} ticket_len={TicketLen}",
            token.Scope,
            token.SoapServiceAuthority,
            token.RestServiceAuthority,
            token.ExpiresIn,
            token.AccessToken.Length);

        return token;
    }

    private static string RedactSecrets(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }

        // Avoid echoing accidental secret material if Projector ever mirrored form fields.
        return body.Length > 500 ? body[..500] + "…" : body;
    }
}
