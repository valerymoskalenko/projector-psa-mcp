namespace Projector.Domain.Expenses;

/// <summary>One expense report (PwsExpenseDocument) as PwsGetExpenseReports lists it.</summary>
public sealed record ExpenseReportSummary
{
    public string? Number { get; init; }
    public string? Uid { get; init; }
    public string? Name { get; init; }
    /// <summary>Composite status code: D, PR, R, PS, S, PA, A, F (approved to pay), P (paid).</summary>
    public string? Status { get; init; }
    public string? EarliestDate { get; init; }
    public string? LatestDate { get; init; }
    public int CardCount { get; init; }
    public double? Reimbursement { get; init; }
    public double? Total { get; init; }
    public string? Currency { get; init; }
    public IReadOnlyList<ExpenseProjectRef> Projects { get; init; } = [];
    public bool Locked { get; init; }
    public string? ResourceId { get; init; }
    public string? ResourceUid { get; init; }
    public string? ResourceName { get; init; }
}

public sealed record ExpenseProjectRef(string? Code, string? Name);

public sealed record ExpenseReportList(IReadOnlyList<ExpenseReportSummary> Reports, bool CanCreate);

/// <summary>One expense report with its cost cards and receipts (PwsGetExpenseDocument).</summary>
public sealed record ExpenseReportDetail
{
    public string? Number { get; init; }
    public string? Uid { get; init; }
    public string? Name { get; init; }
    /// <summary>Base64 version stamp; an update must echo it in the ExpenseDocument block.</summary>
    public string? Timestamp { get; init; }
    public string? Status { get; init; }
    public string? Currency { get; init; }
    public int CurrencyDigits { get; init; } = 2;
    public bool Locked { get; init; }
    /// <summary>LOK (all cards approved), PRM (no permission) or null (editable).</summary>
    public string? MaintainUnavailableReason { get; init; }
    public string? ResourceId { get; init; }
    public string? ResourceUid { get; init; }
    public string? ResourceName { get; init; }
    public double? Total { get; init; }
    public IReadOnlyList<ExpenseCard> Cards { get; init; } = [];
    public IReadOnlyList<ExpenseReceipt> Receipts { get; init; } = [];

    public bool Editable => !Locked && MaintainUnavailableReason is null;
}

/// <summary>One cost card on an expense report.</summary>
public sealed record ExpenseCard
{
    public string? Uid { get; init; }
    public string? Timestamp { get; init; }
    public string? Date { get; init; }
    public string? ExpenseType { get; init; }
    public string? Description { get; init; }
    public double? Amount { get; init; }
    public string? Currency { get; init; }
    public double? FxRate { get; init; }
    public double? DisbursedAmount { get; init; }
    public double? Units { get; init; }
    public string? Location { get; init; }
    public string? ProjectCode { get; init; }
    public string? ProjectName { get; init; }
    /// <summary>D draft, R rejected, S submitted, A approved.</summary>
    public string? ApprovalStatus { get; init; }
    public bool Locked { get; init; }
    public string? RejectedReason { get; init; }
    public int DocumentCount { get; init; }

    public bool Editable => !Locked && ApprovalStatus is "D" or "R";
}

/// <summary>A receipt document linked to a report or to cost cards.</summary>
public sealed record ExpenseReceipt
{
    public string? ReceiptUid { get; init; }
    public string? DocumentUid { get; init; }
    public string? Name { get; init; }
    public string? MimeType { get; init; }
    public bool EntireReport { get; init; }
    public IReadOnlyList<string> CardUids { get; init; } = [];
}

/// <summary>What the person may enter expenses on (PwsGetResourceExpenseEntryInfo).</summary>
public sealed record ExpenseEntryInfo(
    IReadOnlyList<ExpenseEntryProject> Projects,
    IReadOnlyList<ExpenseTypeInfo> ExpenseTypes,
    IReadOnlyList<string> Locations);

public sealed record ExpenseEntryProject
{
    public string? Code { get; init; }
    public string? Name { get; init; }
    public string? ClientName { get; init; }
    public string? EngagementName { get; init; }
    public string? OpenDate { get; init; }
    public string? CloseDate { get; init; }
    public bool AnyExpenseType { get; init; }
    public IReadOnlyList<string> ExpenseTypes { get; init; } = [];
}

public sealed record ExpenseTypeInfo
{
    public string? Name { get; init; }
    public string? Group { get; init; }
    public bool DescriptionRequired { get; init; }
    public bool UnitDriven { get; init; }
    public bool Mileage { get; init; }
    public double? DefaultUnitCost { get; init; }
    public string? Instructions { get; init; }
}

/// <summary>Account-wide expense entry rules (PwsGetExpenseEntryParameters).</summary>
public sealed record ExpenseEntryRules
{
    public bool ReceiptsOnCards { get; init; }
    public bool ReceiptsOnReport { get; init; }
    public bool NonBillableAllowed { get; init; }
    public bool OutsideProjectDatesAllowed { get; init; }
    public bool LocationRequired { get; init; }
    public long ReceiptMaxBytes { get; init; }
    public bool EntryForOthersAllowed { get; init; }
}

public sealed record ExpenseDay(string Date, bool CanEnter, bool PeriodClosed);

/// <summary>A currency and, when asked for a date and a disbursed currency, Projector's rate into it.</summary>
/// <summary>
/// Whether Projector needs a receipt before a card of this type can be submitted (PwsGetExpenseTypes); from
/// <see cref="Threshold"/> (in the disbursed currency) upward when it is above 0.
/// </summary>
public sealed record ExpenseReceiptRule(string Name, bool Required, double? Threshold)
{
    public bool AppliesTo(double? disbursedAmount) =>
        Required && (Threshold is not > 0 || (disbursedAmount ?? 0) >= Threshold);
}

public sealed record CurrencyRate(string Code, string? Name, int Digits, double? Rate);

/// <summary>A receipt in the user's receipt pool, not linked to a report yet.</summary>
public sealed record PoolReceipt(string DocumentUid, string? Name, long? Size, string? Created, string? MimeType);

/// <summary>The signed-in user's Projector identity, found from their own data (no API returns it directly).</summary>
public sealed record ExpenseIdentity(string ResourceId, string? ResourceUid, string? DisplayName, string? UserUid);

/// <summary>Where the user's receipts are uploaded before they are linked.</summary>
public sealed record ReceiptPool(string FolderUid, string DocumentServerUrl);

public sealed record UploadedReceipt(string DocumentUid, string? Name, long? Size, string? MimeType);

/// <summary>One PwsSaveExpenseDocument call. Never submits.</summary>
public sealed record ExpenseSaveRequest
{
    /// <summary>Null for a new report.</summary>
    public string? ReportUid { get; init; }
    /// <summary>Required with <see cref="ReportUid"/>: without the ExpenseDocument block Projector ignores card and receipt changes.</summary>
    public string? ReportTimestamp { get; init; }
    public required string ReportName { get; init; }
    public required string ResourceId { get; init; }
    public IReadOnlyList<ExpenseCardWrite> Cards { get; init; } = [];
    public IReadOnlyList<ExpenseReceiptLink> Receipts { get; init; } = [];
}

public sealed record ExpenseCardWrite
{
    /// <summary>Echoed in CostCardResults; links a receipt to a new card.</summary>
    public required string ReferenceId { get; init; }
    public string? CardUid { get; init; }
    public string? Timestamp { get; init; }
    public required string Date { get; init; }
    public required string ExpenseType { get; init; }
    public string? Description { get; init; }
    public required double Amount { get; init; }
    public required string Currency { get; init; }
    /// <summary>The amount in the report's currency (required by Projector for new cards).</summary>
    public required double DisbursedAmount { get; init; }
    public required string ProjectCode { get; init; }
    public string? Location { get; init; }
}

public sealed record ExpenseReceiptLink
{
    public required string DocumentUid { get; init; }
    public required string DocumentName { get; init; }
    /// <summary>Echoed in ReceiptResults.</summary>
    public string? ReferenceId { get; init; }
    /// <summary>The card's ReferenceId in the same save (new card).</summary>
    public string? CardReferenceId { get; init; }
    /// <summary>The card's UID (existing card).</summary>
    public string? CardUid { get; init; }
}

public sealed record ExpenseSaveIssue(string? ReferenceId, string? Code, string? Text);

public sealed record ExpenseSaveResult
{
    public bool Succeeded { get; init; }
    public string? Number { get; init; }
    public string? Uid { get; init; }
    public IReadOnlyList<ExpenseSaveIssue> CardIssues { get; init; } = [];
    public IReadOnlyList<ExpenseSaveIssue> ReceiptIssues { get; init; } = [];
    public IReadOnlyList<ExpenseSaveIssue> Messages { get; init; } = [];
    /// <summary>The report after the save (FullDetailFlag), when Projector returned it.</summary>
    public ExpenseReportDetail? Report { get; init; }
}
