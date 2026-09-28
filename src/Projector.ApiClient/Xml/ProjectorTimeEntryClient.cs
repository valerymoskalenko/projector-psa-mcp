using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Projector.Domain.Auth;
using Projector.Domain.Exceptions;
using Projector.Domain.Timecards;

namespace Projector.ApiClient.Xml;

/// <summary>
/// SOAP transport for writes: its HttpClient has no retry handler, so a save is sent exactly once.
/// </summary>
public sealed class ProjectorSoapWriteHttp
{
    public ProjectorSoapWriteHttp(HttpClient http, ILogger<ProjectorSoapHttp> logger, ProjectorCallLimiter? limiter = null)
    {
        Soap = new ProjectorSoapHttp(http, logger, limiter, isWrite: true);
    }

    public ProjectorSoapHttp Soap { get; }
}

/// <summary>
/// Time entry for the signed-in user. Lookups use the shared (retrying) transport;
/// <see cref="SaveTimecardAsync"/> uses <see cref="ProjectorSoapWriteHttp"/> and is never retried.
/// </summary>
public sealed class ProjectorTimeEntryClient : IProjectorTimeEntryClient
{
    public const string WriteOutcomeUnknown = "write_outcome_unknown";

    private readonly ProjectorSoapHttp _read;
    private readonly ProjectorSoapWriteHttp _write;
    private readonly ILogger<ProjectorTimeEntryClient> _logger;

    public ProjectorTimeEntryClient(
        ProjectorSoapHttp read,
        ProjectorSoapWriteHttp write,
        ILogger<ProjectorTimeEntryClient> logger)
    {
        _read = read;
        _write = write;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TimeEntryProjectSummary>> SearchTimeEntryProjectsAsync(
        ProjectorConnection connection,
        string workDate,
        string? query = null,
        string? projectCode = null,
        CancellationToken cancellationToken = default)
    {
        var xml = ProjectorEnvelopeBuilders.BuildSearchTimeEntryProjects(
            connection.SessionTicket, workDate, query, projectCode);
        var doc = await PostAsync(_read, connection, "PwsSearchProjects", xml, cancellationToken);
        ProjectorSoapHttp.ThrowIfResultError(XmlNodeHelpers.LocalNode(doc, "PwsSearchProjectsResult"));
        return ProjectorTimeEntryParsers.ParseSearchProjects(doc);
    }

    public async Task<TimeEntryProjectSetup?> GetTimeEntryProjectAsync(
        ProjectorConnection connection,
        string projectCode,
        string workDate,
        CancellationToken cancellationToken = default)
    {
        var xml = ProjectorEnvelopeBuilders.BuildGetTimeEntryProjectRole(
            connection.SessionTicket, projectCode, workDate);
        var doc = await PostAsync(_read, connection, "PwsGetTimeEntryProjectRole", xml, cancellationToken);
        ProjectorSoapHttp.ThrowIfResultError(XmlNodeHelpers.LocalNode(doc, "PwsGetTimeEntryProjectRoleResult"));
        return ProjectorTimeEntryParsers.ParseTimeEntryProject(doc);
    }

    public async Task<TaskAssignments> GetTaskAssignmentsAsync(
        ProjectorConnection connection,
        string projectCode,
        CancellationToken cancellationToken = default)
    {
        var xml = ProjectorEnvelopeBuilders.BuildGetProjectWithTasks(connection.SessionTicket, projectCode);
        var doc = await PostAsync(_read, connection, "PwsGetProject", xml, cancellationToken);
        ProjectorSoapHttp.ThrowIfResultError(XmlNodeHelpers.LocalNode(doc, "PwsGetProjectResult"));
        return ProjectorTimeEntryParsers.ParseTaskAssignments(doc);
    }

    public async Task<TimeEntryParameters> GetTimeEntryParametersAsync(
        ProjectorConnection connection,
        CancellationToken cancellationToken = default)
    {
        var xml = ProjectorEnvelopeBuilders.BuildGetTimeEntryParameters(connection.SessionTicket);
        var doc = await PostAsync(_read, connection, "PwsGetTimeEntryParameters", xml, cancellationToken);
        ProjectorSoapHttp.ThrowIfResultError(XmlNodeHelpers.LocalNode(doc, "PwsGetTimeEntryParametersResult"));
        return ProjectorTimeEntryParsers.ParseTimeEntryParameters(doc);
    }

    public async Task<IReadOnlyList<Timecard>> ListOwnTimecardsAsync(
        ProjectorConnection connection,
        string workDate,
        CancellationToken cancellationToken = default)
    {
        var xml = ProjectorEnvelopeBuilders.BuildGetTimeCards(connection.SessionTicket, null, workDate, workDate);
        var doc = await PostAsync(_read, connection, "PwsGetTimeCards", xml, cancellationToken);
        ProjectorSoapHttp.ThrowIfResultError(XmlNodeHelpers.LocalNode(doc, "PwsGetTimeCardsResult"));
        return ProjectorResponseParsers.ParseTimeCards(doc);
    }

    public async Task<OwnTimecard?> GetOwnTimecardAsync(
        ProjectorConnection connection,
        string timecardUid,
        string workDate,
        CancellationToken cancellationToken = default)
    {
        var xml = ProjectorEnvelopeBuilders.BuildGetOwnTimecard(connection.SessionTicket, timecardUid, workDate);
        var doc = await PostAsync(_read, connection, "PwsGetTimeCards", xml, cancellationToken);
        ProjectorSoapHttp.ThrowIfResultError(XmlNodeHelpers.LocalNode(doc, "PwsGetTimeCardsResult"));
        return ProjectorTimeEntryParsers.ParseOwnTimecard(doc, timecardUid);
    }

    public async Task<TimecardSaveResult> SaveTimecardAsync(
        ProjectorConnection connection,
        TimecardSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var xml = ProjectorEnvelopeBuilders.BuildSaveTimecard(connection.SessionTicket, request);
        XDocument doc;
        try
        {
            doc = await PostAsync(_write, connection, "PwsSaveTimeCards", xml, cancellationToken);
        }
        catch (Exception ex) when (IsAmbiguousTransportFailure(ex, cancellationToken))
        {
            // The request may or may not have reached Projector. Never resend automatically.
            _logger.LogError(ex, "PwsSaveTimeCards outcome unknown ({ExceptionType})", ex.GetType().Name);
            throw new ProjectorApiException(
                "Projector did not answer the save in time, so it is unknown whether the time card was saved. " +
                "Check list_timecards for that date before trying again.",
                WriteOutcomeUnknown,
                ex);
        }

        return ProjectorTimeEntryParsers.ParseSaveResult(doc);
    }

    private static bool IsAmbiguousTransportFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException
        || ((ex is TaskCanceledException or TimeoutException) && !cancellationToken.IsCancellationRequested);

    private static Task<XDocument> PostAsync(
        ProjectorSoapHttp soap,
        ProjectorConnection connection,
        string method,
        string envelopeXml,
        CancellationToken cancellationToken)
    {
        var body = XDocument.Parse(envelopeXml).Root?
            .Element(SoapNamespaces.SoapEnv + "Body")?
            .Elements()
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Envelope for {method} has no body element.");
        return soap.PostWcfAsync(connection, method, body, cancellationToken);
    }

    private static Task<XDocument> PostAsync(
        ProjectorSoapWriteHttp write,
        ProjectorConnection connection,
        string method,
        string envelopeXml,
        CancellationToken cancellationToken) =>
        PostAsync(write.Soap, connection, method, envelopeXml, cancellationToken);
}
