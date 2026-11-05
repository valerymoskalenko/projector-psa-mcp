using System.Text.Json.Serialization;
using Projector.Contracts.Common;

namespace Projector.Contracts.Resources;

/// <summary>
/// <see cref="CostCenter"/>, <see cref="Department"/> and <see cref="ListGroups"/> switch to Projector's resource export
/// (needs the Projector permission Export Data): people filtered by cost center or department, or the list of groups.
/// </summary>
public sealed record ListResourcesRequest(
    string? Query = null,
    bool IncludeInactive = false,
    int MaxRows = 50,
    string? CostCenter = null,
    string? Department = null,
    bool ListGroups = false)
{
    public bool UsesExport =>
        ListGroups || !string.IsNullOrWhiteSpace(CostCenter) || !string.IsNullOrWhiteSpace(Department);
}

public sealed record ResourceLinkDto(string Uri, string? Name);

public sealed record ListResourcesResponse(
    IReadOnlyList<ResourceSummaryDto> Resources,
    int Count,
    [property: JsonPropertyName("has_more")] bool HasMore,
    SearchCoverageDto SearchCoverage,
    [property: JsonPropertyName("resource_links")] IReadOnlyList<ResourceLinkDto> ResourceLinks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("next_offset")] int? NextOffset = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("groups")] ResourceGroupsDto? Groups = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("note")] string? Note = null);

/// <summary>Cost centers and departments of the people the caller can see, with head counts (list_groups).</summary>
public sealed record ResourceGroupsDto(
    [property: JsonPropertyName("cost_centers")] IReadOnlyList<ResourceGroupCountDto> CostCenters,
    [property: JsonPropertyName("departments")] IReadOnlyList<ResourceGroupCountDto> Departments);

public sealed record ResourceGroupCountDto(string Name, int People);

/// <summary>Cost center, department and title are filled only on the export path (cost_center / department).</summary>
public sealed record ResourceSummaryDto(
    string? ResourceUid,
    string? ResourceReferenceSystemId,
    string? DisplayName,
    string? EmailAddress,
    bool Inactive,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("cost_center")] string? CostCenter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("department")] string? Department = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("title")] string? Title = null);

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
