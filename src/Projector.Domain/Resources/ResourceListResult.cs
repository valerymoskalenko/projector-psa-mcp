namespace Projector.Domain.Resources;

/// <summary>PwsGetResourceList page plus Projector RowCountExceeded flag.</summary>
public sealed class ResourceListResult
{
    public required IReadOnlyList<ResourceSummary> Resources { get; init; }

    public bool ServerTruncated { get; init; }
}
