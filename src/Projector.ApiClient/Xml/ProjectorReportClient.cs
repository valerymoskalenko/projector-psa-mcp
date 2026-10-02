using System.Globalization;
using System.Xml.Linq;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;
using Projector.Domain.Reports;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Saved reports and legacy exports for get_report, in the caller's own session. Reads use the shared (retrying)
/// transport; starting a report run or an export batch uses <see cref="ProjectorSoapWriteHttp"/>, so a timeout can
/// never start it twice.
/// </summary>
public sealed class ProjectorReportClient : IProjectorReportClient
{
    private readonly ProjectorSoapHttp _read;
    private readonly ProjectorSoapWriteHttp _submit;

    public ProjectorReportClient(ProjectorSoapHttp read, ProjectorSoapWriteHttp submit)
    {
        _read = read;
        _submit = submit;
    }

    public async Task<ReportTable> GetReportOutputAsync(
        ProjectorConnection connection,
        string? webServiceCode,
        string? outputUid,
        CancellationToken cancellationToken = default)
    {
        var body = ProjectorReportEnvelopes.BuildGetReportOutput(connection.SessionTicket, webServiceCode, outputUid);
        var doc = await _read.PostWcfAsync(connection, "PwsGetReportOutput", body, cancellationToken);
        ProjectorSoapHttp.ThrowIfResultError(XmlNodeHelpers.LocalNode(doc, "PwsGetReportOutputResult"));
        return ProjectorReportParsers.ParseReportOutput(doc);
    }

    public async Task<string> SubmitReportSpecAsync(
        ProjectorConnection connection,
        string specUid,
        CancellationToken cancellationToken = default)
    {
        var doc = await SubmitAsync(
            connection, "SubmitReportSpec",
            ProjectorReportEnvelopes.BuildSubmitReportSpec(connection.SessionTicket, specUid), cancellationToken);
        return ProjectorReportParsers.SubmittedOutputUid(doc)
            ?? throw new ProjectorApiException("Projector started no report run (no output id returned).", "ReportNotStarted");
    }

    public async Task<IReadOnlyList<ReportRun>> GetReportStatusAsync(
        ProjectorConnection connection,
        string? outputUid,
        CancellationToken cancellationToken = default)
    {
        var doc = await ReadAsync(
            connection, "GetReportStatus",
            ProjectorReportEnvelopes.BuildGetReportStatus(connection.SessionTicket, outputUid), cancellationToken);
        return ProjectorReportParsers.ParseReportRuns(doc);
    }

    public async Task<string> SubmitGinsuExportAsync(
        ProjectorConnection connection,
        GinsuExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var doc = await SubmitAsync(
            connection, "SubmitOlapGinsuExport",
            ProjectorReportEnvelopes.BuildSubmitOlapGinsuExport(connection.SessionTicket, request), cancellationToken);
        return ProjectorReportParsers.SubmittedRequestId(doc)
            ?? throw new ProjectorApiException("Projector started no export (no request id returned).", "ExportNotStarted");
    }

    public async Task<BatchPage> GetGinsuRecordsAsync(
        ProjectorConnection connection,
        string requestId,
        long startAfterRowIndex,
        int maxRows,
        bool onlyCount,
        CancellationToken cancellationToken = default)
    {
        var doc = await ReadAsync(
            connection, "ExportOlapGinsuRecords",
            ProjectorReportEnvelopes.BuildExportOlapGinsuRecords(
                connection.SessionTicket, requestId, startAfterRowIndex, maxRows, onlyCount),
            cancellationToken);
        return ProjectorReportParsers.ParseBatchPage(doc, "OlapGinsuRecord");
    }

    public async Task<ExportPage> ExportProjectListAsync(
        ProjectorConnection connection,
        bool openForTimeOnly,
        string? projectCodesAfter,
        int maxRows,
        bool onlyCount,
        CancellationToken cancellationToken = default)
    {
        var doc = await ReadAsync(
            connection, "ExportProjectList",
            ProjectorReportEnvelopes.BuildExportProjectList(
                connection.SessionTicket, openForTimeOnly, projectCodesAfter, maxRows, onlyCount),
            cancellationToken);
        return new ExportPage(ProjectorReportParsers.RowCount(doc), ProjectorReportParsers.ParseRows(doc, "Project"));
    }

    public async Task<ExportPage> ExportTimeCardsAsync(
        ProjectorConnection connection,
        string minWorkDate,
        string maxWorkDate,
        string? approvedMinTimestamp,
        string? approvedIdsAfter,
        int maxRows,
        bool onlyCount,
        CancellationToken cancellationToken = default)
    {
        var doc = await ReadAsync(
            connection, "ExportTimeCards",
            ProjectorReportEnvelopes.BuildExportTimeCards(
                connection.SessionTicket, minWorkDate, maxWorkDate, approvedMinTimestamp, approvedIdsAfter, maxRows, onlyCount),
            cancellationToken);
        return new ExportPage(ProjectorReportParsers.RowCount(doc), ProjectorReportParsers.ParseRows(doc, "TimeCard"));
    }

    public async Task<IReadOnlyDictionary<string, string>> ExportResourceNamesAsync(
        ProjectorConnection connection,
        CancellationToken cancellationToken = default)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var envelope = XDocument.Parse(ProjectorEnvelopeBuilders.BuildExportResources(
            connection.SessionTicket, today, today, includeInactive: true));
        var doc = await ReadAsync(connection, "ExportResources", envelope, cancellationToken);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ProjectorResponseParsers.ParseExportedResourcesAsmx(doc))
        {
            if (!string.IsNullOrWhiteSpace(row.ResourceReferenceSystemId) && !string.IsNullOrWhiteSpace(row.DisplayName))
            {
                names[row.ResourceReferenceSystemId] = row.DisplayName;
            }
        }

        return names;
    }

    private async Task<XDocument> ReadAsync(
        ProjectorConnection connection, string method, XDocument envelope, CancellationToken cancellationToken)
    {
        var doc = await _read.PostAsmxAsync(connection, SoapNamespaces.AsmxAction(method), envelope, method, cancellationToken);
        ProjectorReportParsers.ThrowIfOpsError(doc, method);
        return doc;
    }

    private async Task<XDocument> SubmitAsync(
        ProjectorConnection connection, string method, XDocument envelope, CancellationToken cancellationToken)
    {
        try
        {
            var doc = await _submit.Soap.PostAsmxAsync(connection, SoapNamespaces.AsmxAction(method), envelope, method, cancellationToken);
            ProjectorReportParsers.ThrowIfOpsError(doc, method);
            return doc;
        }
        catch (ProjectorApiException ex) when (ProjectorCallLimiter.IsBusy(ex) || ex.ErrorCode == ProjectorCallLimiter.BusyErrorCode)
        {
            // The write transport words a busy refusal as a failed save; nothing is saved here.
            throw new ProjectorApiException(
                "Projector is busy with your other requests and did not start the run. Retry this call in a few seconds.",
                ProjectorCallLimiter.BusyErrorCode,
                ex);
        }
    }
}
