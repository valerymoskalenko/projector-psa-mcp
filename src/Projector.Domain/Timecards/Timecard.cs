namespace Projector.Domain.Timecards;

public sealed class Timecard
{
    public string? ProjectCode { get; init; }

    public string? ProjectName { get; init; }

    public string? EngagementCode { get; init; }

    public string? EngagementName { get; init; }

    public string? ClientName { get; init; }

    public string? ClientNumber { get; init; }

    public bool? Billable { get; init; }

    public string? ProjectStageName { get; init; }

    public string? WorkDate { get; init; }

    public int WorkMinutes { get; init; }

    public double WorkHours { get; init; }

    public string? Status { get; init; }

    public string? CardStatusCode { get; init; }

    public string? RateTypeName { get; init; }

    public string? TaskName { get; init; }

    public string? RoleName { get; init; }

    public string? Description { get; init; }

    public string? LocationName { get; init; }

    public string? RejectedByDisplayName { get; init; }

    public string? RejectedByEmail { get; init; }

    public string? RejectedByUserReferenceSystemId { get; init; }

    public string? RejectedReason { get; init; }

    public string? RejectedTimestamp { get; init; }
}
