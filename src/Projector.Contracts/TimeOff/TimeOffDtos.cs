using System.Text.Json.Serialization;

namespace Projector.Contracts.TimeOff;

public sealed record TimeOffCardDto(
    string? TimeOffReason,
    string? TimeOffDate,
    int TimeOffMinutes,
    double TimeOffHours,
    string? Narrative,
    string? CardStatus,
    string? CardStatusCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedByDisplayName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedByEmail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedByUserReferenceSystemId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedTimestamp = null);

public sealed record ScheduledTimeOffDto(
    string? UserReferenceSystemId,
    string DisplayName,
    string? ScheduledTimeOffDate,
    int TimeOffMinutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TimeOffHours = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Narrative = null,
    string? TimeOffReasonName = null,
    string? ApprovalStatus = null);
