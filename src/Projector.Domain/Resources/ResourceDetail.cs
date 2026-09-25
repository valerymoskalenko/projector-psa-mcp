namespace Projector.Domain.Resources;

public sealed class ResourceDetail
{
    public string? ResourceUid { get; init; }

    public string? ResourceReferenceSystemId { get; init; }

    public string? DisplayName { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? EmailAddress { get; init; }

    public bool Inactive { get; init; }

    public string? ManagerDisplayName { get; init; }

    public string? ManagerEmail { get; init; }

    public string? TimecardApproverDisplayName { get; init; }

    public string? ExpenseApproverDisplayName { get; init; }

    public string? LocationName { get; init; }

    public string? CostCenterName { get; init; }

    public string? DepartmentName { get; init; }

    public string? TitleName { get; init; }

    public string? ResourceTypeName { get; init; }

    public IReadOnlyList<ResourceUdf> Udfs { get; init; } = [];

    public IReadOnlyList<ResourceHistoryEntry> History { get; init; } = [];
}

public sealed class ResourceUdf
{
    public string? Name { get; init; }

    public string? Value { get; init; }
}

public sealed class ResourceHistoryEntry
{
    public int Index { get; init; }

    public bool IsActive { get; init; }

    public string? EffectiveDate { get; init; }

    public string? LocationName { get; init; }

    public string? CostCenterName { get; init; }

    public string? DepartmentName { get; init; }

    public string? TitleName { get; init; }

    public string? ResourceTypeName { get; init; }

    public bool? TrackMissingTime { get; init; }
}
