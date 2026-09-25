using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Projector.Domain.Auth;
using Projector.Domain.Availability;
using Projector.Domain.Engagements;
using Projector.Domain.Holidays;
using Projector.Domain.Resources;
using Projector.Domain.Schedule;
using Projector.Domain.Timecards;
using Projector.Domain.TimeOff;
using Projector.Domain.Users;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Full SOAP client: resources + timecards/schedule/engagements/holidays (OAuth session ticket only).
/// </summary>
public sealed class ProjectorSoapClient :
    IProjectorSoapClient,
    IProjectorUserClient
{
    private readonly ProjectorSoapHttp _soap;
    private readonly HttpClient _http;
    private readonly ILogger<ProjectorSoapClient> _logger;

    public ProjectorSoapClient(
        ProjectorSoapHttp soap,
        HttpClient http,
        ILogger<ProjectorSoapClient> logger)
    {
        _soap = soap;
        _http = http;
        _logger = logger;
    }

    public async Task<ResourceListResult> ListResourcesAsync(
        ProjectorConnection connection,
        string? query,
        bool includeInactive,
        int maxRows,
        CancellationToken cancellationToken = default)
    {
        var body = new XElement(SoapNamespaces.Pws + "PwsGetResourceList",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", connection.SessionTicket),
                new XElement(SoapNamespaces.Req + "IncludeInactiveFlag", includeInactive ? "true" : "false"),
                new XElement(SoapNamespaces.Req + "MaxRowsToReturn", maxRows),
                string.IsNullOrWhiteSpace(query) ? null : new XElement(SoapNamespaces.Req + "QueryString", query)));

        var doc = await _soap.PostWcfAsync(connection, "PwsGetResourceList", body, cancellationToken);
        ProjectorSoapHttp.ThrowIfResultError(XmlNodeHelpers.LocalNode(doc, "PwsGetResourceListResult"));
        return new ResourceListResult
        {
            Resources = ProjectorResponseParsers.ParseResourceList(doc),
            ServerTruncated = ProjectorSoapHttp.IsRowCountExceeded(doc)
        };
    }

    public async Task<ResourceDetail?> GetResourceAsync(
        ProjectorConnection connection,
        string id,
        bool includeHistory,
        bool includeUdfs,
        CancellationToken cancellationToken = default)
    {
        var body = new XElement(SoapNamespaces.Pws + "PwsGetResource",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", connection.SessionTicket),
                new XElement(SoapNamespaces.Req + "ResourceIdentities",
                    ProjectorIdentityRefs.BuildResourceRef(id))));

        var doc = await _soap.PostWcfAsync(connection, "PwsGetResource", body, cancellationToken);
        ProjectorSoapHttp.ThrowIfResultError(XmlNodeHelpers.LocalNode(doc, "PwsGetResourceResult"));
        return ProjectorResponseParsers.ParseResource(doc, includeHistory, includeUdfs);
    }

    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(
        ProjectorConnection connection,
        string? query,
        bool includeInactive,
        int maxRows,
        CancellationToken cancellationToken = default)
    {
        var xml = ProjectorEnvelopeBuilders.BuildGetUserList(connection.SessionTicket, includeInactive, query, maxRows);
        var doc = await PostEnvelopeAsync(connection, "PwsGetUserList", xml, cancellationToken);
        return ProjectorResponseParsers.ParseUserList(doc);
    }

    public async Task<UserSummary?> GetUserAsync(
        ProjectorConnection connection,
        string id,
        CancellationToken cancellationToken = default)
    {
        var xml = ProjectorEnvelopeBuilders.BuildGetUser(connection.SessionTicket, id);
        var doc = await PostEnvelopeAsync(connection, "PwsGetUser", xml, cancellationToken);
        return ProjectorResponseParsers.ParseUser(doc);
    }

    public async Task<TimecardListResult> ListTimecardsAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
        string startDate,
        string endDate,
        string? projectCode = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        ProjectorDateHelpers.AssertDateWindow(startDate, endDate, Domain.Common.ProjectorDateWindows.TimecardsDays);
        RejectEmailAsResourceId(resourceReferenceSystemId);
        var xml = ProjectorEnvelopeBuilders.BuildGetTimeCards(
            connection.SessionTicket, resourceReferenceSystemId, startDate, endDate);
        var doc = await PostEnvelopeAsync(connection, "PwsGetTimeCards", xml, cancellationToken);
        return new TimecardListResult
        {
            Timecards = ProjectorResponseParsers.ParseTimeCards(doc, projectCode, status),
            ServerTruncated = ProjectorSoapHttp.IsRowCountExceeded(doc)
        };
    }

    public async Task<TimeOffListResult> ListTimeOffCardsAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
        string startDate,
        string endDate,
        CancellationToken cancellationToken = default)
    {
        ProjectorDateHelpers.AssertDateWindow(startDate, endDate, Domain.Common.ProjectorDateWindows.TimeOffDays);
        RejectEmailAsResourceId(resourceReferenceSystemId);
        var xml = ProjectorEnvelopeBuilders.BuildGetTimeOffCards(
            connection.SessionTicket, resourceReferenceSystemId, startDate, endDate);
        var doc = await PostEnvelopeAsync(connection, "PwsGetTimeCards", xml, cancellationToken);
        return new TimeOffListResult
        {
            Cards = ProjectorResponseParsers.ParseTimeOff(doc),
            ServerTruncated = ProjectorSoapHttp.IsRowCountExceeded(doc)
        };
    }

    public async Task<ResourceSchedule> GetResourceScheduleAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
        string startDate,
        string endDate,
        CancellationToken cancellationToken = default)
    {
        ProjectorDateHelpers.AssertDateWindow(startDate, endDate, Domain.Common.ProjectorDateWindows.ScheduleDays);
        return await GetResourceScheduleCoreAsync(
            connection, resourceReferenceSystemId, startDate, endDate, cancellationToken);
    }

    public async Task<AvailabilitySummary> CheckAvailabilityAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
        string startDate,
        string endDate,
        string? displayName = null,
        string? emailAddress = null,
        double requiredMinutesPerWeek = 0,
        CancellationToken cancellationToken = default)
    {
        // Availability allows up to 366 days; schedule tool itself is capped at 56.
        ProjectorDateHelpers.AssertDateWindow(startDate, endDate, Domain.Common.ProjectorDateWindows.AvailabilityDays);
        var schedule = await GetResourceScheduleCoreAsync(
            connection, resourceReferenceSystemId, startDate, endDate, cancellationToken);
        return AvailabilityCalculator.ToAvailabilitySummary(
            schedule, resourceReferenceSystemId, displayName, emailAddress, requiredMinutesPerWeek);
    }

    private async Task<ResourceSchedule> GetResourceScheduleCoreAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
        string startDate,
        string endDate,
        CancellationToken cancellationToken)
    {
        RejectEmailAsResourceId(resourceReferenceSystemId);
        var xml = ProjectorEnvelopeBuilders.BuildGetResourceSchedule(
            connection.SessionTicket, resourceReferenceSystemId, startDate, endDate);
        var doc = await PostEnvelopeAsync(connection, "PwsGetResourceSchedule", xml, cancellationToken);
        return ProjectorResponseParsers.ParseResourceSchedule(doc);
    }

    public async Task<IReadOnlyList<HolidayEntry>> GetResourcePtoHolidaysAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
        string cutoffDate,
        CancellationToken cancellationToken = default)
    {
        RejectEmailAsResourceId(resourceReferenceSystemId);
        var xml = ProjectorEnvelopeBuilders.BuildGetResourcePto(
            connection.SessionTicket, resourceReferenceSystemId, cutoffDate);
        var doc = await PostEnvelopeAsync(connection, "PwsGetResourcePto", xml, cancellationToken);
        return ProjectorResponseParsers.ParseResourcePtoHolidays(doc);
    }

    public async Task<IReadOnlyList<UtilizationYear>> GetUtilizationAsync(
        ProjectorConnection connection,
        string resourceReferenceSystemId,
        CancellationToken cancellationToken = default)
    {
        RejectEmailAsResourceId(resourceReferenceSystemId);
        var xml = ProjectorEnvelopeBuilders.BuildGetResourceUtilization(
            connection.SessionTicket, resourceReferenceSystemId);
        var doc = await PostEnvelopeAsync(connection, "PwsGetResourceUtilizationSummary", xml, cancellationToken);
        return ProjectorResponseParsers.ParseUtilization(doc);
    }

    public async Task<EngagementListResult> ListEngagementsAsync(
        ProjectorConnection connection,
        string? query,
        bool includeClosed,
        int maxRows,
        CancellationToken cancellationToken = default)
    {
        var xml = ProjectorEnvelopeBuilders.BuildGetEngagementList(
            connection.SessionTicket, includeClosed, maxRows, query);
        var doc = await PostEnvelopeAsync(connection, "PwsGetEngagementList", xml, cancellationToken);
        return new EngagementListResult
        {
            Engagements = ProjectorResponseParsers.ParseEngagementList(doc),
            ServerTruncated = ProjectorSoapHttp.IsRowCountExceeded(doc)
        };
    }

    public async Task<EngagementDetail?> GetEngagementAsync(
        ProjectorConnection connection,
        string engagementCode,
        CancellationToken cancellationToken = default)
    {
        var items = await GetEngagementsByCodeAsync(connection, [engagementCode], cancellationToken);
        return items.Count == 0 ? null : items[0];
    }

    public async Task<IReadOnlyList<EngagementDetail>> GetEngagementsByCodeAsync(
        ProjectorConnection connection,
        IReadOnlyList<string> engagementCodes,
        CancellationToken cancellationToken = default)
    {
        var codes = engagementCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
        {
            return [];
        }

        var items = new List<EngagementDetail>();
        for (var i = 0; i < codes.Count; i += 100)
        {
            var batch = codes.Skip(i).Take(100).ToList();
            var xml = ProjectorEnvelopeBuilders.BuildGetEngagement(connection.SessionTicket, batch);
            var doc = await PostEnvelopeAsync(connection, "PwsGetEngagement", xml, cancellationToken);
            items.AddRange(ProjectorResponseParsers.ParseEngagements(doc));
        }

        return items;
    }

    public async Task<IReadOnlyList<ProjectSummary>> GetProjectsByCodeAsync(
        ProjectorConnection connection,
        IReadOnlyList<string> projectCodes,
        CancellationToken cancellationToken = default)
    {
        var codes = projectCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
        {
            return [];
        }

        var items = new List<ProjectSummary>();
        for (var i = 0; i < codes.Count; i += 100)
        {
            var batch = codes.Skip(i).Take(100).ToList();
            var xml = ProjectorEnvelopeBuilders.BuildGetProject(connection.SessionTicket, batch);
            var doc = await PostEnvelopeAsync(connection, "PwsGetProject", xml, cancellationToken);
            items.AddRange(ProjectorResponseParsers.ParseProjects(doc));
        }

        return items;
    }

    public async Task<ProjectRoleListResult> ListProjectRolesAsync(
        ProjectorConnection connection,
        IReadOnlyList<string> projectCodes,
        CancellationToken cancellationToken = default)
    {
        var codes = NormalizeProjectCodes(projectCodes);
        if (codes.Count == 0)
        {
            throw new Projector.Domain.Exceptions.ProjectorApiException(
                "At least one project_code is required.",
                "InvalidArgument");
        }

        if (codes.Count > 100)
        {
            throw new Projector.Domain.Exceptions.ProjectorApiException(
                "At most 100 project_codes are allowed per call.",
                "InvalidArgument");
        }

        var xml = ProjectorEnvelopeBuilders.BuildGetProjectRoles(connection.SessionTicket, codes);
        var doc = await PostEnvelopeAsync(connection, "PwsGetProjectRoles", xml, cancellationToken);
        return new ProjectRoleListResult
        {
            Roles = ProjectorResponseParsers.ParseProjectRoles(doc),
            ServerTruncated = ProjectorSoapHttp.IsRowCountExceeded(doc)
        };
    }

    public async Task<ProjectBookingListResult> ListProjectBookingsAsync(
        ProjectorConnection connection,
        IReadOnlyList<string> projectCodes,
        string startDate,
        string endDate,
        CancellationToken cancellationToken = default)
    {
        ProjectorDateHelpers.AssertDateWindow(startDate, endDate, Domain.Common.ProjectorDateWindows.HolidaysDays);
        var codes = NormalizeProjectCodes(projectCodes);
        if (codes.Count == 0)
        {
            throw new Projector.Domain.Exceptions.ProjectorApiException(
                "At least one project_code is required.",
                "InvalidArgument");
        }

        if (codes.Count > 100)
        {
            throw new Projector.Domain.Exceptions.ProjectorApiException(
                "At most 100 project_codes are allowed per call.",
                "InvalidArgument");
        }

        var start = ProjectorDateHelpers.ToShortDate(startDate);
        var end = ProjectorDateHelpers.ToShortDate(endDate);
        var weekCount = ProjectorDateHelpers.GetMinimumWeekCountForWindow(start, end);
        var wcfUrl = ProjectorSoapHttp.GetWcfUrl(connection);
        var requests = codes.Select(code =>
        {
            var envelopeXml = ProjectorEnvelopeBuilders.BuildGetResourceSchedulingRoleData(
                connection.SessionTicket, code, start, "A", weekCount);
            return new SoapRequestItem(
                code,
                wcfUrl,
                SoapNamespaces.WcfSoapActionPrefix + "PwsGetResourceSchedulingRoleData",
                XDocument.Parse(envelopeXml));
        }).ToList();

        var responses = await ProjectorSoapConcurrent.PostAsync(_http, requests, cancellationToken: cancellationToken);
        var rows = new List<ProjectBookingRow>();
        var failed = new List<string>();
        var truncated = false;
        foreach (var response in responses)
        {
            var fault = ProjectorSoapHttp.GetSoapFault(response.Response);
            if (fault is not null)
            {
                failed.Add(response.Key);
                continue;
            }

            if (ProjectorSoapHttp.IsRowCountExceeded(response.Response))
            {
                truncated = true;
            }

            rows.AddRange(ProjectorResponseParsers.ParseResourceSchedulingRoleData(response.Response, start, end));
        }

        return new ProjectBookingListResult
        {
            Bookings = rows,
            ServerTruncated = truncated,
            FailedProjectCodes = failed
        };
    }

    private static List<string> NormalizeProjectCodes(IReadOnlyList<string> projectCodes) =>
        projectCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<CompanyHolidayCalendarsResult> GetCompanyHolidayCalendarsAsync(
        ProjectorConnection connection,
        string startDate,
        string endDate,
        string? location = null,
        int maxRows = 10000,
        CancellationToken cancellationToken = default)
    {
        ProjectorDateHelpers.AssertDateWindow(startDate, endDate, Domain.Common.ProjectorDateWindows.HolidaysDays);
        var start = ProjectorDateHelpers.ToShortDate(startDate);
        var end = ProjectorDateHelpers.ToShortDate(endDate);

        var exportXml = ProjectorEnvelopeBuilders.BuildExportResources(
            connection.SessionTicket, start, end, includeInactive: false, maxRows: maxRows);
        var exportDoc = XDocument.Parse(exportXml);
        var asmxDoc = await _soap.PostAsmxAsync(
            connection,
            SoapNamespaces.AsmxExportResourcesAction,
            exportDoc,
            "ExportResources",
            cancellationToken);

        var exported = ProjectorResponseParsers.ParseExportedResourcesAsmx(asmxDoc);
        var resources = HolidayCalendarLogic.GroupResourcesByHolidayCalendar(exported);
        var truncated = ProjectorSoapHttp.IsRowCountExceeded(asmxDoc) || exported.Count >= maxRows;
        var matched = HolidayCalendarLogic.FilterByLocation(resources, location);
        var probes = HolidayCalendarLogic.SelectLocationProbeTargets(matched);

        if (matched.Count == 0)
        {
            return HolidayCalendarLogic.BuildCompanyHolidayCalendarsResult(
                start, end, location, resources, matched, new Dictionary<string, IReadOnlyList<HolidayEntry>>(),
                truncated, maxRows, holidayCalls: 0);
        }

        var windowStart = DateTime.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var ptoCutoff = windowStart.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var wcfUrl = ProjectorSoapHttp.GetWcfUrl(connection);
        var requests = probes.Select(kv =>
        {
            var envelopeXml = ProjectorEnvelopeBuilders.BuildGetResourcePto(
                connection.SessionTicket, kv.Value, ptoCutoff);
            return new SoapRequestItem(
                kv.Key,
                wcfUrl,
                SoapNamespaces.WcfSoapActionPrefix + "PwsGetResourcePto",
                XDocument.Parse(envelopeXml));
        }).ToList();

        var responses = await ProjectorSoapConcurrent.PostAsync(_http, requests, cancellationToken: cancellationToken);
        var holidaysByLocation = new Dictionary<string, IReadOnlyList<HolidayEntry>>(StringComparer.Ordinal);
        foreach (var response in responses)
        {
            ProjectorSoapHttp.AssertNoSoapFault(response.Response, $"PwsGetResourcePto for location '{response.Key}'");
            holidaysByLocation[response.Key] = ProjectorResponseParsers.ParseResourcePtoHolidays(response.Response);
        }

        _logger.LogDebug(
            "Company holiday calendars: {Locations} probes for {Resources} resources",
            probes.Count,
            resources.Count);

        return HolidayCalendarLogic.BuildCompanyHolidayCalendarsResult(
            start, end, location, resources, matched, holidaysByLocation, truncated, maxRows, requests.Count);
    }

    public async Task<IReadOnlyList<ScheduledTimeOffExportRow>> ListScheduledTimeOffExportAsync(
        ProjectorConnection connection,
        string dateBookmark,
        int maxRows = 10000,
        CancellationToken cancellationToken = default)
    {
        var xml = ProjectorEnvelopeBuilders.BuildExportScheduledTimeoff(
            connection.SessionTicket, dateBookmark, maxRows);
        var doc = await _soap.PostAsmxAsync(
            connection,
            SoapNamespaces.AsmxExportScheduledTimeoffAction,
            XDocument.Parse(xml),
            "ExportScheduledTimeoff",
            cancellationToken);
        return ProjectorResponseParsers.ParseScheduledTimeOffAsmx(doc);
    }

    private async Task<XDocument> PostEnvelopeAsync(
        ProjectorConnection connection,
        string method,
        string envelopeXml,
        CancellationToken cancellationToken)
    {
        var doc = XDocument.Parse(envelopeXml);
        // Body element is the first child of soap Body
        var body = doc.Root?
            .Element(SoapNamespaces.SoapEnv + "Body")?
            .Elements()
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Envelope for {method} has no body element.");

        return await _soap.PostWcfAsync(connection, method, body, cancellationToken);
    }

    private static void RejectEmailAsResourceId(string resourceReferenceSystemId)
    {
        if (resourceReferenceSystemId.Contains('@', StringComparison.Ordinal))
        {
            ProjectorIdentityRefs.BuildResourceRef(resourceReferenceSystemId);
        }
    }
}
