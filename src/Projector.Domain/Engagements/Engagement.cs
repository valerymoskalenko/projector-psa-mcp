namespace Projector.Domain.Engagements;

public sealed class EngagementSummary
{
    public string? EngagementCode { get; init; }

    public string? EngagementName { get; init; }

    public string? ClientName { get; init; }

    public string? ClientNumber { get; init; }

    public string? EngagementManagerDisplayName { get; init; }

    public string? EngagementManagerEmail { get; init; }

    public IReadOnlyList<EngagementProject> Projects { get; init; } = [];
}

public sealed class EngagementDetail
{
    public string? EngagementCode { get; init; }

    public string? EngagementName { get; init; }

    public bool Billable { get; init; }

    public bool Productive { get; init; }

    public string? CostCenterName { get; init; }

    public string? ClientName { get; init; }

    public string? ClientNumber { get; init; }

    public string? EngagementManagerDisplayName { get; init; }

    public string? EngagementManagerEmail { get; init; }

    public IReadOnlyList<EngagementContract> Contracts { get; init; } = [];

    public IReadOnlyList<EngagementProject> Projects { get; init; } = [];

    /// <summary>hours_only | hours_and_money when any planned budget field was present.</summary>
    public string? BudgetVisibility { get; init; }

    public int? WorkMinutesTimeBudgetAmount { get; init; }

    public double? WorkHoursTimeBudgetAmount { get; init; }

    public int? ChargeableMinutesTimeBudgetAmount { get; init; }

    public double? ChargeableHoursTimeBudgetAmount { get; init; }

    public string? CurrencyCode { get; init; }

    public string? TimeBudgetMetric { get; init; }

    /// <summary>Human label for TimeBudgetMetric (e.g. Billing Adjusted Revenue).</summary>
    public string? TimeBudgetMetricLabel { get; init; }

    public double? ContractRevenueTimeBudgetAmount { get; init; }

    public double? BillingAdjustedRevenueTimeBudgetAmount { get; init; }

    public double? ResourceDirectCostTimeBudgetAmount { get; init; }

    public string? CostBudgetMetric { get; init; }

    /// <summary>Human label for CostBudgetMetric (e.g. Client Amount).</summary>
    public string? CostBudgetMetricLabel { get; init; }

    public double? ClientAmountCostBudgetAmount { get; init; }

    public double? DisbursedAmountCostBudgetAmount { get; init; }

    public double? ExpenseAmountCostBudgetAmount { get; init; }

    public double? ProjectContractRevenueTimeBudgetAmount { get; init; }

    public double? ProjectBillingAdjustedRevenueTimeBudgetAmount { get; init; }

    public double? ProjectResourceDirectCostTimeBudgetAmount { get; init; }

    public double? ProjectWorkMinutesTimeBudgetAmount { get; init; }

    public double? ProjectChargeableMinutesTimeBudgetAmount { get; init; }

    public double? ProjectClientAmountCostBudgetAmount { get; init; }

    public double? ProjectDisbursedAmountCostBudgetAmount { get; init; }

    public double? ProjectExpenseAmountCostBudgetAmount { get; init; }
}

public sealed class EngagementContract
{
    public string? ContractTypeName { get; init; }
}

public sealed class EngagementProject
{
    public string? ProjectCode { get; init; }

    public string? ProjectName { get; init; }

    public string? ContractTypeName { get; init; }

    public string? Stage { get; init; }

    public string? BeginDate { get; init; }

    public string? EndDate { get; init; }

    public string? ProjectManagerDisplayName { get; init; }
}

public sealed class ProjectSummary
{
    public string? ProjectCode { get; init; }

    public string? ProjectName { get; init; }

    public string? Stage { get; init; }

    public string? BeginDate { get; init; }

    public string? EndDate { get; init; }

    public string? ProjectManagerDisplayName { get; init; }
}

/// <summary>Booked/assigned role row from PwsGetProjectRoles (Mode=A).</summary>
public sealed class ProjectRoleAssignment
{
    public string? ProjectCode { get; init; }

    public string? RoleName { get; init; }

    public string? ResourceId { get; init; }

    public string? DisplayName { get; init; }

    public string? Email { get; init; }
}

/// <summary>Booked-hour bucket row from PwsGetResourceSchedulingRoleData (mode A).</summary>
public sealed class ProjectBookingRow
{
    public string? ProjectCode { get; init; }

    public string? ProjectName { get; init; }

    public string? RoleName { get; init; }

    public string? ResourceId { get; init; }

    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    public string? Date { get; init; }

    public string? DailyWeeklyFlag { get; init; }

    public string? SchedulingMode { get; init; }

    public int ScheduledMinutes { get; init; }

    public double ScheduledHours { get; init; }
}

public sealed class UtilizationYear
{
    public string? Year { get; init; }

    public double? BillableUtilization { get; init; }

    public double? ChargeableUtilization { get; init; }

    public double? ProductiveUtilization { get; init; }

    public double? TotalUtilization { get; init; }
}
