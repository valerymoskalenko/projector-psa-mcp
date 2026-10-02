namespace Projector.Application.Tools;

/// <summary>How a column's text is turned into a cell of the answer.</summary>
public enum ReportColumnKind
{
    Text,

    /// <summary>The first ten characters of a Projector date (yyyy-MM-dd).</summary>
    Date,

    Number,

    /// <summary>Minutes in Projector, hours in the answer.</summary>
    Hours,

    Bool,

    /// <summary>A resource reference id, shown as the person's display name when the resource list is readable.</summary>
    PersonName
}

/// <summary>One column of an export dataset: its name in the answer and the Projector field behind it.</summary>
public sealed record ReportColumn(string Name, string Field, ReportColumnKind Kind = ReportColumnKind.Text);

/// <summary>The datasets of get_report: their columns, default columns and the catalog the tool returns without arguments.</summary>
public static class ReportDatasets
{
    public const string Report = "report";
    public const string Ginsu = "ginsu";
    public const string Projects = "projects";
    public const string TimeCards = "time_cards";

    public static readonly string[] Names = [Report, Ginsu, Projects, TimeCards];

    public static readonly ReportColumn[] ProjectColumns =
    [
        new("project_code", "ProjectCode"),
        new("project_name", "ProjectName"),
        new("client", "ClientName1"),
        new("client_number", "ClientNumber1"),
        new("engagement_code", "EngagementCode"),
        new("engagement_name", "EngagementName"),
        new("engagement_manager", "EngagementMgrReferenceSystemId", ReportColumnKind.PersonName),
        new("engagement_manager_id", "EngagementMgrReferenceSystemId"),
        new("project_manager", "ProjectMgrReferenceSystemId", ReportColumnKind.PersonName),
        new("project_manager_id", "ProjectMgrReferenceSystemId"),
        new("engagement_type", "EngagementTypeLongName"),
        new("billable", "BillableFlag", ReportColumnKind.Bool),
        new("currency", "CurrencyCode"),
        new("location", "LocationName"),
        new("begin_date", "BeginDate", ReportColumnKind.Date),
        new("end_date", "EndDate", ReportColumnKind.Date),
        new("open_for_time", "OpenForTimeFlag", ReportColumnKind.Bool),
        new("open_for_cost", "OpenForCostFlag", ReportColumnKind.Bool),
        new("stage", "ProjectStageName"),
        new("cost_center_number", "CostCenterReferenceSystemId")
    ];

    public static readonly string[] ProjectDefaultColumns =
    [
        "project_code", "project_name", "client", "engagement_manager", "project_manager",
        "begin_date", "end_date", "open_for_time", "stage"
    ];

    public static readonly ReportColumn[] TimeCardColumns =
    [
        new("work_date", "WorkDate", ReportColumnKind.Date),
        new("person", "ResourceReferenceSystemId", ReportColumnKind.PersonName),
        new("person_id", "ResourceReferenceSystemId"),
        new("project_code", "ProjectCode"),
        new("task", "ProjectTaskName"),
        new("task_type", "ProjectTaskTypeName"),
        new("role", "RoleName"),
        new("rate_type", "ProjectRateTypeName"),
        new("hours", "WorkMinutes", ReportColumnKind.Hours),
        new("narrative", "Narrative"),
        new("approved_at", "ApprovedTimestamp"),
        new("location", "LocationName"),
        new("non_billable", "NonBillableFlag", ReportColumnKind.Bool),
        new("system_revenue", "SystemRevenueAmount", ReportColumnKind.Number),
        new("contract_revenue", "ContractRevenueAmount", ReportColumnKind.Number),
        new("adjusted_revenue", "AdjustedRevenueAmount", ReportColumnKind.Number),
        new("currency", "CurrencyCode"),
        new("card_id", "ReferenceSystemId")
    ];

    public static readonly string[] TimeCardDefaultColumns =
    [
        "work_date", "person", "project_code", "task", "role", "hours", "narrative"
    ];

    /// <summary>What get_report answers without a dataset: what each dataset is for and how to call it.</summary>
    public static IReadOnlyList<object> Catalog() =>
    [
        new
        {
            dataset = Report,
            answers = "A report the signed-in user saved and ran in Projector (Reports page), as rows. Filters and dates are fixed in the report.",
            parameters = "one of code (the report's web service code, set on its Output tab: the latest run), spec_uid (Additional Actions > Show Report Spec UID: runs the report now) or output_uid (one run); query, columns, max_rows",
            example = new { dataset = Report, code = "MY_REPORT_CODE", max_rows = 100 }
        },
        new
        {
            dataset = Ginsu,
            answers = "Hours across people and projects for any date range: posted (approved, unapproved) and planned (booked) hours, time off and holidays, with revenue. Rows are grouped by the chosen columns and the numbers added up. No card descriptions.",
            parameters = "start_date, end_date (required); cutoff_date (last day of actuals; later days are planned hours), bucket (day, week, month, quarter, year, none), cost_center, by (projects or resources), billable_only, include_unapproved, include_time_off; query, columns, max_rows",
            columns = GinsuCleaner.Dimensions.Select(d => d.Name).Concat(GinsuCleaner.Measures.Select(m => m.Name)).ToArray(),
            default_columns = GinsuCleaner.DefaultColumns,
            example = new { dataset = Ginsu, start_date = "2026-07-01", end_date = "2026-09-30", bucket = "month", columns = new[] { "period", "person", "status", "hours" } }
        },
        new
        {
            dataset = Projects,
            answers = "All projects with engagement, client, engagement and project manager, dates, stage and whether they are open for time.",
            parameters = "include_closed (default false: only projects open for time); query, columns, max_rows",
            columns = ProjectColumns.Select(c => c.Name).ToArray(),
            default_columns = ProjectDefaultColumns,
            example = new { dataset = Projects, include_closed = true, columns = new[] { "project_code", "project_name", "project_manager" } }
        },
        new
        {
            dataset = TimeCards,
            answers = "Approved time cards of everyone the user may see, card by card with the description. Unapproved cards are not included: use ginsu for hours in every status, or list_timecards for one person.",
            parameters = "start_date, end_date (required); query, columns, max_rows",
            columns = TimeCardColumns.Select(c => c.Name).ToArray(),
            default_columns = TimeCardDefaultColumns,
            example = new { dataset = TimeCards, start_date = "2026-09-21", end_date = "2026-09-27" }
        }
    ];
}
