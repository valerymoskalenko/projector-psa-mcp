using System.Text.Json.Serialization;
using Projector.Contracts.Common;

namespace Projector.Contracts.Resources;

public sealed record ListResourcesRequest(
    string? Query = null,
    bool IncludeInactive = false,
    int MaxRows = 50);

public sealed record ResourceLinkDto(string Uri, string? Name);

public sealed record ListResourcesResponse(
    IReadOnlyList<ResourceSummaryDto> Resources,
    int Count,
    [property: JsonPropertyName("has_more")] bool HasMore,
    SearchCoverageDto SearchCoverage,
    [property: JsonPropertyName("resource_links")] IReadOnlyList<ResourceLinkDto> ResourceLinks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("next_offset")] int? NextOffset = null);

public sealed record ResourceSummaryDto(
    string? ResourceUid,
    string? ResourceReferenceSystemId,
    string? DisplayName,
    string? EmailAddress,
    bool Inactive);

public sealed record GetResourceRequest(
    string Id,
    bool IncludeHistory = false,
    bool IncludeUdfs = true);

public sealed record GetResourceResponse(
    string Uri,
    ResourceDetailDto Resource,
    [property: JsonPropertyName("resource_links")] IReadOnlyList<ResourceLinkDto> ResourceLinks);

public sealed record ResourceDetailDto(
    string? ResourceUid,
    string? ResourceReferenceSystemId,
    string? DisplayName,
    string? FirstName,
    string? LastName,
    string? EmailAddress,
    bool Inactive,
    string? ManagerDisplayName,
    string? ManagerEmail,
    string? TimecardApproverDisplayName,
    string? ExpenseApproverDisplayName,
    string? LocationName,
    string? CostCenterName,
    string? DepartmentName,
    string? TitleName,
    string? ResourceTypeName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ResourceUdfDto>? Udfs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ResourceHistoryDto>? History);

public sealed record ResourceUdfDto(string? Name, string? Value);

public sealed record ResourceHistoryDto(
    int Index,
    bool IsActive,
    string? EffectiveDate,
    string? LocationName,
    string? CostCenterName,
    string? DepartmentName,
    string? TitleName,
    string? ResourceTypeName,
    bool? TrackMissingTime);
