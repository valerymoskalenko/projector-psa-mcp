namespace Projector.Domain.Engagements;

/// <summary>PwsGetEngagementList page plus Projector RowCountExceeded flag.</summary>
public sealed class EngagementListResult
{
    public required IReadOnlyList<EngagementSummary> Engagements { get; init; }

    public bool ServerTruncated { get; init; }
}
