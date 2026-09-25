namespace Projector.Domain.Resources;

public sealed class ResourceSummary
{
    public string? ResourceUid { get; init; }

    public string? ResourceReferenceSystemId { get; init; }

    public string? DisplayName { get; init; }

    public string? EmailAddress { get; init; }

    public bool Inactive { get; init; }
}
