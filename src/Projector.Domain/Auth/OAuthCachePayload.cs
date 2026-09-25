namespace Projector.Domain.Auth;

/// <summary>
/// On-disk OAuth cache shape shared with PowerShell prototypes
/// (%LOCALAPPDATA%\ProjectorMcp\oauth-sessions\*.bin, DPAPI CurrentUser).
/// </summary>
public sealed class OAuthCachePayload
{
    public string AccountCode { get; set; } = "";

    public string RequestedScope { get; set; } = "";

    public string GrantedScope { get; set; } = "";

    public string AccessToken { get; set; } = "";

    public string RefreshToken { get; set; } = "";

    public string SoapAuthority { get; set; } = "";

    public string RestAuthority { get; set; } = "";

    public string? ExpiresAtUtc { get; set; }

    public string? ObtainedAtUtc { get; set; }
}
