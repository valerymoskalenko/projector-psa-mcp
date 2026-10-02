namespace Projector.Domain.Reports;

/// <summary>A table of text cells: a saved report's CSV output, or cleaned export rows.</summary>
public sealed record ReportTable(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows);

/// <summary>One run of a saved report (GetReportStatus). The list without a UID carries no usable <see cref="OutputUid"/>.</summary>
public sealed record ReportRun(
    string? OutputUid,
    string? Name,
    string Status,
    DateTimeOffset? Requested,
    DateTimeOffset? Completed);

/// <summary>Rows of one export call, each as element name → text, and Projector's RowCount.</summary>
public sealed record ExportPage(int RowCount, IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows);

/// <summary>One call of a batch export: its status, RowCount (-1 until completed) and rows.</summary>
public sealed record BatchPage(string Status, int RowCount, IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows)
{
    public bool IsWaiting => Status is "Queued" or "Running";
}

/// <summary>Parameters of SubmitOlapGinsuExport. Dates are yyyy-MM-dd; <see cref="BucketWidth"/> is D, W, M, Q, Y or empty.</summary>
public sealed record GinsuExportRequest(
    string BeginDate,
    string EndDate,
    string CutoffDate,
    string BucketWidth,
    string? CostCenterNumber,
    bool FilterByResources,
    bool BillableOnly,
    bool IncludeTimeOff,
    bool IncludeUnapproved,
    string CurrencyCode = "USD");
