using Projector.Domain.Auth;

namespace Projector.Domain.Expenses;

/// <summary>
/// Expense reports, cost cards and receipts. Reads may name another resource (Projector applies the caller's
/// permissions); the writes are for the caller's own reports. Nothing here submits or approves.
/// </summary>
public interface IProjectorExpenseClient
{
    /// <summary>A null resource lists the caller's reports.</summary>
    Task<ExpenseReportList> ListReportsAsync(
        ProjectorConnection connection, string? resourceId, int months, bool unreceivedOnly, CancellationToken cancellationToken = default);

    /// <summary>Null when Projector does not find the report.</summary>
    Task<ExpenseReportDetail?> GetReportAsync(
        ProjectorConnection connection, string reportNumber, CancellationToken cancellationToken = default);

    /// <summary>An empty new report for the resource (nothing is created): its disbursed currency.</summary>
    Task<ExpenseReportDetail?> GetNewReportAsync(
        ProjectorConnection connection, string resourceId, CancellationToken cancellationToken = default);

    Task<ExpenseEntryInfo> GetEntryInfoAsync(
        ProjectorConnection connection, string resourceId, string startDate, string endDate, CancellationToken cancellationToken = default);

    Task<ExpenseEntryRules> GetEntryRulesAsync(ProjectorConnection connection, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExpenseDay>> GetScheduleAsync(
        ProjectorConnection connection, string? resourceId, string startDate, string endDate, CancellationToken cancellationToken = default);

    /// <summary>Active currencies with Projector's rate into <paramref name="disbursedCurrency"/> on <paramref name="date"/>.</summary>
    Task<IReadOnlyList<CurrencyRate>> GetCurrenciesAsync(
        ProjectorConnection connection, string resourceId, string disbursedCurrency, string date, CancellationToken cancellationToken = default);

    /// <summary>Per expense type: is a receipt needed to submit (thresholds in <paramref name="disbursedCurrency"/>).</summary>
    Task<IReadOnlyList<ExpenseReceiptRule>> GetReceiptRulesAsync(
        ProjectorConnection connection, string resourceId, string disbursedCurrency, string date, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's resource and user, from their own expense reports and resource record (no API returns the
    /// caller directly). Null when the caller has no expense report yet.
    /// </summary>
    Task<ExpenseIdentity?> FindSelfAsync(ProjectorConnection connection, CancellationToken cancellationToken = default);

    Task<ReceiptPool> GetReceiptPoolAsync(ProjectorConnection connection, string userUid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PoolReceipt>> ListPoolAsync(
        ProjectorConnection connection, string folderUid, CancellationToken cancellationToken = default);

    /// <summary>Sent once, never retried; a timeout throws <c>write_outcome_unknown</c>.</summary>
    Task<UploadedReceipt> UploadReceiptAsync(
        ProjectorConnection connection, ReceiptPool pool, string fileName, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>One PwsSaveExpenseDocument, never retried, never submits; a timeout throws <c>write_outcome_unknown</c>.</summary>
    Task<ExpenseSaveResult> SaveAsync(
        ProjectorConnection connection, ExpenseSaveRequest request, CancellationToken cancellationToken = default);
}
