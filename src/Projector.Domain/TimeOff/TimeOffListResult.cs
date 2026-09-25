namespace Projector.Domain.TimeOff;

/// <summary>PwsGetTimeCards time-off page plus Projector RowCountExceeded flag.</summary>
public sealed class TimeOffListResult
{
    public required IReadOnlyList<TimeOffCard> Cards { get; init; }

    public bool ServerTruncated { get; init; }
}
