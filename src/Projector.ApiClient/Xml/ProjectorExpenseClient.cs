using System.Net.Http.Headers;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;
using Projector.Domain.Expenses;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Receipt files go to Projector's document server as a multipart POST, not SOAP. No retry handler: an upload is
/// sent once.
/// </summary>
public sealed class ProjectorDocumentUploadHttp(HttpClient http)
{
    public HttpClient Http { get; } = http;
}

/// <summary>
/// Expense reports for the expense tools. Reads use the shared (retrying) transport; the save and the receipt
/// upload are sent exactly once (<see cref="ProjectorSoapWriteHttp"/>, <see cref="ProjectorDocumentUploadHttp"/>).
/// </summary>
public sealed class ProjectorExpenseClient : IProjectorExpenseClient
{
    /// <summary>Months of the caller's reports searched to find who the caller is.</summary>
    private const int SelfSearchMonths = 120;

    private readonly ProjectorSoapHttp _read;
    private readonly ProjectorSoapWriteHttp _write;
    private readonly ProjectorDocumentUploadHttp _upload;
    private readonly ILogger<ProjectorExpenseClient> _logger;

    public ProjectorExpenseClient(
        ProjectorSoapHttp read,
        ProjectorSoapWriteHttp write,
        ProjectorDocumentUploadHttp upload,
        ILogger<ProjectorExpenseClient> logger)
    {
        _read = read;
        _write = write;
        _upload = upload;
        _logger = logger;
    }

    public async Task<ExpenseReportList> ListReportsAsync(
        ProjectorConnection connection, string? resourceId, int months, bool unreceivedOnly, CancellationToken cancellationToken = default)
    {
        var doc = await ReadAsync(connection, ProjectorExpenseEnvelopes.GetExpenseReports(
            connection.SessionTicket, resourceId, months, unreceivedOnly), cancellationToken);
        return ProjectorExpenseParsers.ParseReportList(doc);
    }

    public async Task<ExpenseReportDetail?> GetReportAsync(
        ProjectorConnection connection, string reportNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            var doc = await ReadAsync(connection, ProjectorExpenseEnvelopes.GetExpenseDocument(
                connection.SessionTicket, reportNumber), cancellationToken);
            return ProjectorExpenseParsers.ParseReportDetail(doc);
        }
        catch (ProjectorApiException ex) when (ex.ErrorCode is "EntityNotFound" or "ExpenseDocumentNotFound")
        {
            return null;
        }
    }

    public async Task<ExpenseReportDetail?> GetNewReportAsync(
        ProjectorConnection connection, string resourceId, CancellationToken cancellationToken = default)
    {
        var doc = await ReadAsync(connection, ProjectorExpenseEnvelopes.GetNewExpenseReport(
            connection.SessionTicket, resourceId), cancellationToken);
        return ProjectorExpenseParsers.ParseReportDetail(doc);
    }

    public async Task<ExpenseEntryInfo> GetEntryInfoAsync(
        ProjectorConnection connection, string resourceId, string startDate, string endDate, CancellationToken cancellationToken = default)
    {
        var doc = await ReadAsync(connection, ProjectorExpenseEnvelopes.GetResourceExpenseEntryInfo(
            connection.SessionTicket, resourceId, startDate, endDate), cancellationToken);
        return ProjectorExpenseParsers.ParseEntryInfo(doc);
    }

    public async Task<ExpenseEntryRules> GetEntryRulesAsync(ProjectorConnection connection, CancellationToken cancellationToken = default)
    {
        var doc = await ReadAsync(connection, ProjectorExpenseEnvelopes.GetExpenseEntryParameters(connection.SessionTicket), cancellationToken);
        return ProjectorExpenseParsers.ParseEntryRules(doc);
    }

    public async Task<IReadOnlyList<ExpenseDay>> GetScheduleAsync(
        ProjectorConnection connection, string? resourceId, string startDate, string endDate, CancellationToken cancellationToken = default)
    {
        var doc = await ReadAsync(connection, ProjectorExpenseEnvelopes.GetResourceExpenseSchedule(
            connection.SessionTicket, resourceId, startDate, endDate), cancellationToken);
        return ProjectorExpenseParsers.ParseSchedule(doc);
    }

    public async Task<IReadOnlyList<CurrencyRate>> GetCurrenciesAsync(
        ProjectorConnection connection, string resourceId, string disbursedCurrency, string date, CancellationToken cancellationToken = default)
    {
        var doc = await ReadAsync(connection, ProjectorExpenseEnvelopes.GetCurrencies(
            connection.SessionTicket, resourceId, disbursedCurrency, date), cancellationToken);
        return ProjectorExpenseParsers.ParseCurrencies(doc);
    }

    public async Task<ExpenseIdentity?> FindSelfAsync(ProjectorConnection connection, CancellationToken cancellationToken = default)
    {
        var reports = await ListReportsAsync(connection, null, SelfSearchMonths, unreceivedOnly: false, cancellationToken);
        var own = reports.Reports.FirstOrDefault(r => r.ResourceId is not null);
        if (own?.ResourceId is null)
        {
            return null;
        }

        var resourceDoc = await PostAsync(_read, connection,
            XDocument.Parse(ProjectorEnvelopeBuilders.BuildGetResource(connection.SessionTicket, own.ResourceId)), cancellationToken);
        return new ExpenseIdentity(own.ResourceId, own.ResourceUid, own.ResourceName, ProjectorExpenseParsers.ParseResourceUserUid(resourceDoc));
    }

    public async Task<ReceiptPool> GetReceiptPoolAsync(ProjectorConnection connection, string userUid, CancellationToken cancellationToken = default)
    {
        var parameters = await ReadAsync(connection,
            ProjectorExpenseEnvelopes.GetDocumentManagementParameters(connection.SessionTicket), cancellationToken);
        var folder = await ReadAsync(connection,
            ProjectorExpenseEnvelopes.GetReceiptPoolFolder(connection.SessionTicket, userUid), cancellationToken);
        var server = ProjectorExpenseParsers.ParseDocumentServerUrl(parameters)
            ?? throw new ProjectorApiException("Projector did not return its document server, so receipts can't be uploaded.", "NoDocumentServer");
        var folderUid = ProjectorExpenseParsers.ParseFolderUid(folder)
            ?? throw new ProjectorApiException("Projector did not return your receipt folder, so receipts can't be uploaded.", "NoReceiptFolder");
        return new ReceiptPool(folderUid, server);
    }

    public async Task<IReadOnlyList<PoolReceipt>> ListPoolAsync(
        ProjectorConnection connection, string folderUid, CancellationToken cancellationToken = default)
    {
        var doc = await ReadAsync(connection, ProjectorExpenseEnvelopes.GetFolderContents(connection.SessionTicket, folderUid), cancellationToken);
        return ProjectorExpenseParsers.ParseFolderContents(doc);
    }

    public async Task<UploadedReceipt> UploadReceiptAsync(
        ProjectorConnection connection, ReceiptPool pool, string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(connection.SessionTicket), "sessionTicket");
        form.Add(new StringContent(pool.FolderUid), "folderUid");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(MimeType(fileName));
        form.Add(file, "file", fileName);

        string json;
        try
        {
            using var response = await _upload.Http.PostAsync(pool.DocumentServerUrl.TrimEnd('/') + "/AjxAddDocument", form, cancellationToken);
            json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ProjectorApiException(
                    $"Projector's document server refused the receipt ({(int)response.StatusCode}).", "ReceiptUploadFailed");
            }
        }
        catch (Exception ex) when (IsAmbiguousTransportFailure(ex, cancellationToken))
        {
            _logger.LogError(ex, "Receipt upload outcome unknown ({ExceptionType})", ex.GetType().Name);
            throw new ProjectorApiException(
                "Projector did not answer the receipt upload in time, so it is unknown whether the receipt was stored. " +
                "list_expenses with include_options shows the receipts waiting in your pool.",
                ProjectorTimeEntryClient.WriteOutcomeUnknown,
                ex);
        }

        return ProjectorExpenseParsers.ParseUploadResult(json);
    }

    public async Task<ExpenseSaveResult> SaveAsync(
        ProjectorConnection connection, ExpenseSaveRequest request, CancellationToken cancellationToken = default)
    {
        var body = ProjectorExpenseEnvelopes.SaveExpenseDocument(connection.SessionTicket, request);
        XDocument doc;
        try
        {
            doc = await _write.Soap.PostWcfAsync(connection, "PwsSaveExpenseDocument", body, cancellationToken);
        }
        catch (Exception ex) when (IsAmbiguousTransportFailure(ex, cancellationToken))
        {
            // The request may or may not have reached Projector. Never resend automatically.
            _logger.LogError(ex, "PwsSaveExpenseDocument outcome unknown ({ExceptionType})", ex.GetType().Name);
            throw new ProjectorApiException(
                "Projector did not answer the save in time, so it is unknown whether the expenses were saved. " +
                "Check list_expenses before trying again.",
                ProjectorTimeEntryClient.WriteOutcomeUnknown,
                ex);
        }

        return ProjectorExpenseParsers.ParseSaveResult(doc);
    }

    private async Task<XDocument> ReadAsync(ProjectorConnection connection, XElement body, CancellationToken cancellationToken)
    {
        var doc = await _read.PostWcfAsync(connection, body.Name.LocalName, body, cancellationToken);
        ProjectorSoapHttp.ThrowIfResultError(XmlNodeHelpers.LocalNode(doc, body.Name.LocalName + "Result"));
        return doc;
    }

    private static async Task<XDocument> PostAsync(
        ProjectorSoapHttp soap, ProjectorConnection connection, XDocument envelope, CancellationToken cancellationToken)
    {
        var body = envelope.Root?.Element(SoapNamespaces.SoapEnv + "Body")?.Elements().FirstOrDefault()
            ?? throw new InvalidOperationException("Envelope has no body element.");
        var doc = await soap.PostWcfAsync(connection, body.Name.LocalName, body, cancellationToken);
        ProjectorSoapHttp.ThrowIfResultError(XmlNodeHelpers.LocalNode(doc, body.Name.LocalName + "Result"));
        return doc;
    }

    internal static string MimeType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };

    private static bool IsAmbiguousTransportFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException
        || ((ex is TaskCanceledException or TimeoutException) && !cancellationToken.IsCancellationRequested);
}
