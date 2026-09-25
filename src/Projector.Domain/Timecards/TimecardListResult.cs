namespace Projector.Domain.Timecards;

/// <summary>PwsGetTimeCards work-card page plus Projector RowCountExceeded flag.</summary>
public sealed class TimecardListResult
{
    public required IReadOnlyList<Timecard> Timecards { get; init; }

    public bool ServerTruncated { get; init; }
}
