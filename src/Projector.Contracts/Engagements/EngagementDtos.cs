using System.Text.Json.Serialization;
using Projector.Contracts.Common;

namespace Projector.Contracts.Engagements;

public sealed record EngagementSummaryDto(
    string? EngagementCode,
    string? EngagementName,
    string? ClientName,
    string? ClientNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EngagementManagerDisplayName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EngagementManagerEmail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<EngagementProjectDto>? Projects = null);

public sealed record EngagementDetailDto(
    string? EngagementCode,
    string? EngagementName,
    bool Billable,
    bool Productive,
    string? CostCenterName,
    string? ClientName,
    string? ClientNumber,
    string? EngagementManagerDisplayName,
    string? EngagementManagerEmail,
    IReadOnlyList<EngagementContractDto> Contracts,
    IReadOnlyList<EngagementProjectDto> Projects,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BudgetVisibility = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WorkMinutesTimeBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? WorkHoursTimeBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ChargeableMinutesTimeBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ChargeableHoursTimeBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CurrencyCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimeBudgetMetric = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimeBudgetMetricLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ContractRevenueTimeBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? BillingAdjustedRevenueTimeBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ResourceDirectCostTimeBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CostBudgetMetric = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CostBudgetMetricLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ClientAmountCostBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DisbursedAmountCostBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ExpenseAmountCostBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ProjectContractRevenueTimeBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ProjectBillingAdjustedRevenueTimeBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ProjectResourceDirectCostTimeBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ProjectWorkMinutesTimeBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ProjectChargeableMinutesTimeBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ProjectClientAmountCostBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ProjectDisbursedAmountCostBudgetAmount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ProjectExpenseAmountCostBudgetAmount = null);

public sealed record EngagementContractDto(string? ContractTypeName);

public sealed record EngagementProjectDto(
    string? ProjectCode,
    string? ProjectName,
    string? ContractTypeName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Stage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BeginDate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EndDate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProjectManagerDisplayName = null);

public sealed record ListEngagementsResponse(
    int Count,
    int Offset,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [property: JsonPropertyName("next_offset")] int? NextOffset,
    SearchCoverageDto SearchCoverage,
    IReadOnlyList<EngagementSummaryDto> Engagements);

public sealed record UtilizationYearDto(
    string? Year,
    double? BillableUtilization,
    double? ChargeableUtilization,
    double? ProductiveUtilization,
    double? TotalUtilization);
