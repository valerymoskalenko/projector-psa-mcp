namespace Projector.Domain.Exceptions;

public sealed class ProjectorApiException : Exception
{
    public string? ErrorCode { get; }

    public ProjectorApiException(string message, string? errorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// True when Projector rejected the call because the session ticket is no longer valid.
    /// Projector has used two codes: "InvalidSessionTicket" and "InvalidSession"
    /// ("The specified session is not valid or is no longer valid. Please re-establish a session.").
    /// The call did nothing on Projector's side, so the caller can refresh the session and run it again.
    /// </summary>
    public bool IsSessionInvalid =>
        string.Equals(ErrorCode, "InvalidSessionTicket", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ErrorCode, "InvalidSession", StringComparison.OrdinalIgnoreCase)
        || (Message.Contains("session", StringComparison.OrdinalIgnoreCase)
            && (Message.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                || Message.Contains("not valid", StringComparison.OrdinalIgnoreCase)));
}
