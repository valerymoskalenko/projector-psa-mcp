using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Projector.Domain.Auth;
using Projector.Domain.Bookings;
using Projector.Domain.Exceptions;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Role create, task-role assign and scheduler booking. Writes go through <see cref="ProjectorSoapWriteHttp"/>
/// once and are never retried.
/// </summary>
public sealed class ProjectorBookingClient : IProjectorBookingClient
{
    private readonly ProjectorSoapHttp _read;
    private readonly ProjectorSoapWriteHttp _write;
    private readonly ILogger<ProjectorBookingClient> _logger;

    public ProjectorBookingClient(
        ProjectorSoapHttp read,
        ProjectorSoapWriteHttp write,
        ILogger<ProjectorBookingClient> logger)
    {
        _read = read;
        _write = write;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RoleScheduleState>> GetRoleSchedulesAsync(
        ProjectorConnection connection,
        string projectCode,
        string startDate,
        int minimumWeekCount,
        CancellationToken cancellationToken = default)
    {
        var envelope = XDocument.Parse(ProjectorEnvelopeBuilders.BuildGetResourceSchedulingRoleData(
            connection.SessionTicket, projectCode, startDate, "A", minimumWeekCount));
        var body = envelope.Root?.Element(SoapNamespaces.SoapEnv + "Body")?.Elements().FirstOrDefault()
            ?? throw new InvalidOperationException("GetResourceSchedulingRoleData envelope has no body.");
        var doc = await _read.PostWcfAsync(connection, "PwsGetResourceSchedulingRoleData", body, cancellationToken);
        ProjectorSoapHttp.ThrowIfResultError(XmlNodeHelpers.LocalNode(doc, "PwsGetResourceSchedulingRoleDataResult"));
        return ProjectorBookingParsers.ParseRoleSchedules(doc);
    }

    public Task<SaveProjectRoleResult> SaveProjectRoleAsync(
        ProjectorConnection connection,
        SaveProjectRoleRequest request,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            connection,
            "PwsSaveProjectRole",
            ProjectorBookingEnvelopes.SaveProjectRole(connection.SessionTicket, request),
            ProjectorBookingParsers.ParseSaveProjectRole,
            "the project role was created",
            cancellationToken);

    public Task SaveProjectTaskRoleAsync(
        ProjectorConnection connection,
        SaveProjectTaskRoleRequest request,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            connection,
            "PwsSaveProjectTaskRole",
            ProjectorBookingEnvelopes.SaveProjectTaskRole(connection.SessionTicket, request),
            doc =>
            {
                ProjectorBookingParsers.ParseSaveProjectTaskRole(doc);
                return true;
            },
            "the role was assigned to the task",
            cancellationToken);

    public Task BookRoleHoursAsync(
        ProjectorConnection connection,
        BookRoleHoursRequest request,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            connection,
            "PwsRequestOrBookRoleHours",
            ProjectorBookingEnvelopes.RequestOrBookRoleHours(connection.SessionTicket, request),
            doc =>
            {
                ProjectorBookingParsers.ParseBookRoleHours(doc);
                return true;
            },
            "the hours were booked",
            cancellationToken);

    private async Task<T> WriteAsync<T>(
        ProjectorConnection connection,
        string method,
        XElement body,
        Func<XDocument, T> parse,
        string whatMayHaveHappened,
        CancellationToken cancellationToken)
    {
        try
        {
            var doc = await _write.Soap.PostWcfAsync(connection, method, body, cancellationToken);
            return parse(doc);
        }
        catch (Exception ex) when (IsAmbiguousTransportFailure(ex, cancellationToken))
        {
            _logger.LogError(ex, "{Method} outcome unknown ({ExceptionType})", method, ex.GetType().Name);
            throw new ProjectorApiException(
                $"Projector did not answer {method} in time, so it is unknown whether {whatMayHaveHappened}. " +
                "Check list_proj_bookings / list_project_roles before trying again.",
                ProjectorTimeEntryClient.WriteOutcomeUnknown,
                ex);
        }
    }

    private static bool IsAmbiguousTransportFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is not ProjectorApiException
        && !cancellationToken.IsCancellationRequested
        && (ex is HttpRequestException or TaskCanceledException or TimeoutException
            || ex.InnerException is HttpRequestException or TaskCanceledException or TimeoutException);
}
