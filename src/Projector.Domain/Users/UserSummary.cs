namespace Projector.Domain.Users;

public sealed class UserSummary
{
    public string? UserUid { get; init; }

    public string? UserReferenceSystemId { get; init; }

    public string? EmailAddress { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? DisplayName { get; init; }

    public bool Inactive { get; init; }
}
