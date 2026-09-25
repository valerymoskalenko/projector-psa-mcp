namespace Projector.Domain.Exceptions;

public sealed class ProjectorApiException : Exception
{
    public string? ErrorCode { get; }

    public ProjectorApiException(string message, string? errorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }
}
