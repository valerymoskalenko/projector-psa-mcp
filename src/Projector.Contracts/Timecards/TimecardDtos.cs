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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectedTimestamp = null);

public sealed record ListTimecardsResponse(
    int Count,
    IReadOnlyList<TimecardDto> Timecards,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LastWorkDate = null);
