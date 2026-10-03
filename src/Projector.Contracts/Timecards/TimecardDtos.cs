using System.Text.Json.Serialization;

namespace Projector.Contracts.Timecards;

public sealed record TimecardDto(
    string? ProjectCode,
    string? ProjectName,
    string? EngagementCode,
    string? EngagementName,
    string? ClientName,
    string? ClientNumber,
    bool? Billable,
    string? ProjectStageName,
    string? WorkDate,
    int WorkMinutes,
    double WorkHours,
    string? Status,
    string? CardStatusCode,
    string? RateTypeName,
    string? TaskName,
    string? RoleName,
    string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LocationName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedByDisplayName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedByEmail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedByUserReferenceSystemId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedTimestamp = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimecardUid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProjectTaskUid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProjectRoleUid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProjectRateTypeUid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TaskPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TaskWbsCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Editable = null);

/// <summary>
/// list_timecards with compact=true: short cards (fixes and lookups; group_by=task is the history read), about half the size of
/// <see cref="TimecardDto"/>.
/// </summary>
public sealed record CompactTimecardDto(
    string? WorkDate,
    double WorkHours,
    string? ProjectCode,
    string? ProjectName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TaskPath,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TaskWbsCode,
    string? RoleName,
    string? RateTypeName,
    string? Status,
    string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimecardUid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Editable = null);

public sealed record ListTimecardsResponse(
    int Count,
    IReadOnlyList<TimecardDto> Timecards,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastWorkDate = null);
