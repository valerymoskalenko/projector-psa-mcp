namespace Projector.Domain.TimeOff;

public sealed class TimeOffCard
{
    public string? TimeOffReason { get; init; }

    public string? TimeOffDate { get; init; }

    public int TimeOffMinutes { get; init; }

    public double TimeOffHours { get; init; }

    public string? Narrative { get; init; }

    public string? CardStatus { get; init; }

    public string? CardStatusCode { get; init; }

    public string? RejectedByDisplayName { get; init; }

    public string? RejectedByEmail { get; init; }

    public string? RejectedByUserReferenceSystemId { get; init; }

    public string? RejectedReason { get; init; }

    public string? RejectedTimestamp { get; init; }
}

public sealed class ScheduledTimeOffExportRow
{
    public string? UserReferenceSystemId { get; init; }

    public string DisplayName { get; init; } = "";

    public string? ScheduledTimeOffDate { get; init; }

    public int TimeOffMinutes { get; init; }

    public int? TimeOffHours { get; init; }

    public string? Narrative { get; init; }

    public string? TimeOffReasonName { get; init; }

    public string? ApprovalStatus { get; init; }
}
