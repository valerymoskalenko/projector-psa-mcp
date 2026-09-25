namespace Projector.Domain.Exceptions;

public sealed class ProjectorAuthorizationException : Exception
{
    public ProjectorAuthorizationException(string message) : base(message)
    {
    }
}
