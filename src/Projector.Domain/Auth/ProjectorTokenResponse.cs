using System.Text.Json.Serialization;

namespace Projector.Domain.Auth;

/// <summary>
/// Token payload returned by Projector OAuth (and our Development mock).
/// </summary>
public sealed class ProjectorTokenResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "projector_session_ticket";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = string.Empty;

    [JsonPropertyName("soap_service_authority")]
    public required string SoapServiceAuthority { get; init; }

    [JsonPropertyName("rest_service_authority")]
    public required string RestServiceAuthority { get; init; }
}
