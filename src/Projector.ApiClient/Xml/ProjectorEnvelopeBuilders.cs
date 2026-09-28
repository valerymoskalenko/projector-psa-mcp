using System.Xml.Linq;

namespace Projector.ApiClient.Xml;

/// <summary>
/// SOAP envelope builders ported from New-Projector*Envelope in ProjectorDomain.ps1.
/// </summary>
public static class ProjectorEnvelopeBuilders
{
    public static string BuildGetUserList(
        string sessionTicket,
        bool includeInactive = true,
        string? query = null,
        int maxRows = 50)
    {
        var body = new XElement(SoapNamespaces.Pws + "PwsGetUserList",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Req + "IncludeInactiveFlag", includeInactive ? "true" : "false"),
                new XElement(SoapNamespaces.Req + "MaxRowsToReturn", maxRows),
                string.IsNullOrWhiteSpace(query)
                    ? null
                    : new XElement(SoapNamespaces.Req + "QueryString", query)));
        return EnvelopeString(body);
    }

    public static string BuildGetUser(string sessionTicket, string id)
    {
        var body = new XElement(SoapNamespaces.Pws + "PwsGetUser",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Req + "UserIdentities",
                    ProjectorIdentityRefs.BuildUserRef(id))));
        return EnvelopeString(body);
    }

    public static string BuildGetResourceList(
        string sessionTicket,
        string? query = null,
        bool includeInactive = false,
        int maxRows = 50)
    {
        var body = new XElement(SoapNamespaces.Pws + "PwsGetResourceList",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Req + "IncludeInactiveFlag", includeInactive ? "true" : "false"),
                new XElement(SoapNamespaces.Req + "MaxRowsToReturn", maxRows),
                string.IsNullOrWhiteSpace(query)
                    ? null
                    : new XElement(SoapNamespaces.Req + "QueryString", query)));
        return EnvelopeString(body);
    }

    public static string BuildGetResource(string sessionTicket, string id)
    {
        var body = new XElement(SoapNamespaces.Pws + "PwsGetResource",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Req + "ResourceIdentities",
                    ProjectorIdentityRefs.BuildResourceRef(id))));
        return EnvelopeString(body);
    }

    /// <summary>
    /// Work and/or time-off cards in every status (Projector leaves Rejected out unless asked). A null resource sends no
    /// ResourceIdentity: Projector uses the caller. IncludeReferencedTasksOnlyFlag keeps only the tasks the cards use
    /// instead of each project's whole task tree (one day: 2.8 MB / 9.8 s → 0.5 MB / 3.3 s, measured 2026-09-28).
    /// Element order follows the WCF contract (alphabetical).
    /// </summary>
    public static string BuildGetTimeCards(
        string sessionTicket,
        string? resourceReferenceSystemId,
        string startDate,
        string endDate,
        bool includeTimeCards = true,
        bool includeTimeOffCards = false,
        string? timecardType = null)
    {
        var start = ProjectorDateHelpers.ToSoapDate(startDate);
        var end = ProjectorDateHelpers.ToSoapDate(endDate);

        XElement? identityXml = null;
        if (!string.IsNullOrWhiteSpace(timecardType))
        {
            identityXml = new XElement(SoapNamespaces.Tim + "TimeCardIdentity",
                new XElement(SoapNamespaces.Com + "TimecardType", timecardType));
        }

        var body = new XElement(SoapNamespaces.Pws + "PwsGetTimeCards",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Tim + "EndDate", end),
                new XElement(SoapNamespaces.Tim + "IncludeApprovedFlag", "true"),
                new XElement(SoapNamespaces.Tim + "IncludeDraftFlag", "true"),
                new XElement(SoapNamespaces.Tim + "IncludeReferencedTasksOnlyFlag", "true"),
                new XElement(SoapNamespaces.Tim + "IncludeRejectedFlag", "true"),
                new XElement(SoapNamespaces.Tim + "IncludeSubmittedFlag", "true"),
                new XElement(SoapNamespaces.Tim + "IncludeTimeCardsFlag", includeTimeCards ? "true" : "false"),
                new XElement(SoapNamespaces.Tim + "IncludeTimeOffCardsFlag", includeTimeOffCards ? "true" : "false"),
                ResourceIdentity(resourceReferenceSystemId),
                new XElement(SoapNamespaces.Tim + "StartDate", start),
                identityXml));
        return EnvelopeString(body, includeTim: true);
    }

    private static XElement? ResourceIdentity(string? resourceReferenceSystemId) =>
        string.IsNullOrWhiteSpace(resourceReferenceSystemId)
            ? null
            : new XElement(SoapNamespaces.Tim + "ResourceIdentity",
                new XElement(SoapNamespaces.Com + "ResourceReferenceSystemId", resourceReferenceSystemId));

    /// <summary>PTO-only cards via PwsGetTimeCards (never PwsGetTimeEntryTimeOff).</summary>
    public static string BuildGetTimeOffCards(
        string sessionTicket,
        string? resourceReferenceSystemId,
        string startDate,
        string endDate) =>
        BuildGetTimeCards(
            sessionTicket,
            resourceReferenceSystemId,
            startDate,
            endDate,
            includeTimeCards: false,
            includeTimeOffCards: true);

    /// <summary>
    /// Projects the caller can enter time on for one day (ListType=T). No ResourceIdentity: Projector uses the caller.
    /// Element order follows PwsSearchProjectsRq (WCF rejects out-of-order elements).
    /// </summary>
    public static string BuildSearchTimeEntryProjects(
        string sessionTicket,
        string workDate,
        string? query = null,
        string? projectCode = null)
    {
        var date = ProjectorDateHelpers.ToSoapDate(workDate);
        var body = new XElement(SoapNamespaces.Pws + "PwsSearchProjects",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Tim + "AvailableProjectsOnlyFlag", "true"),
                new XElement(SoapNamespaces.Tim + "EndDate", date),
                new XElement(SoapNamespaces.Tim + "ListType", "T"),
                string.IsNullOrWhiteSpace(projectCode)
                    ? null
                    : new XElement(SoapNamespaces.Tim + "ProjectIdentity",
                        new XElement(SoapNamespaces.Com + "ProjectCode", projectCode.Trim())),
                string.IsNullOrWhiteSpace(query)
                    ? null
                    : new XElement(SoapNamespaces.Tim + "SearchString", query.Trim()),
                new XElement(SoapNamespaces.Tim + "StartDate", date)));
        return EnvelopeString(body, includeTim: true);
    }

    /// <summary>One project's tasks, task types, rate types and time-entry flags for the caller on one day.</summary>
    public static string BuildGetTimeEntryProjectRole(
        string sessionTicket,
        string projectCode,
        string workDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectCode);
        var date = ProjectorDateHelpers.ToSoapDate(workDate);
        var body = new XElement(SoapNamespaces.Pws + "PwsGetTimeEntryProjectRole",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Tim + "EndDate", date),
                new XElement(SoapNamespaces.Tim + "ProjectIdentity",
                    new XElement(SoapNamespaces.Com + "ProjectCode", projectCode.Trim())),
                new XElement(SoapNamespaces.Tim + "StartDate", date)));
        return EnvelopeString(body, includeTim: true);
    }

    public static string BuildGetTimeEntryParameters(string sessionTicket)
    {
        var body = new XElement(SoapNamespaces.Pws + "PwsGetTimeEntryParameters",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket)));
        return EnvelopeString(body, includeTim: true);
    }

    /// <summary>
    /// One of the caller's own work cards by UID on its work date, any status. No ResourceIdentity, so a card
    /// that belongs to someone else is simply not found.
    /// </summary>
    public static string BuildGetOwnTimecard(string sessionTicket, string timecardUid, string workDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timecardUid);
        var date = ProjectorDateHelpers.ToSoapDate(workDate);
        var body = new XElement(SoapNamespaces.Pws + "PwsGetTimeCards",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Tim + "EndDate", date),
                new XElement(SoapNamespaces.Tim + "IncludeApprovedFlag", "true"),
                new XElement(SoapNamespaces.Tim + "IncludeDraftFlag", "true"),
                new XElement(SoapNamespaces.Tim + "IncludeReferencedTasksOnlyFlag", "true"),
                new XElement(SoapNamespaces.Tim + "IncludeRejectedFlag", "true"),
                new XElement(SoapNamespaces.Tim + "IncludeSubmittedFlag", "true"),
                new XElement(SoapNamespaces.Tim + "IncludeTimeCardsFlag", "true"),
                new XElement(SoapNamespaces.Tim + "IncludeTimeOffCardsFlag", "false"),
                new XElement(SoapNamespaces.Tim + "StartDate", date),
                new XElement(SoapNamespaces.Tim + "TimeCardIdentity",
                    new XElement(SoapNamespaces.Com + "TimecardUid", timecardUid.Trim()))));
        return EnvelopeString(body, includeTim: true);
    }

    /// <summary>
    /// PwsSaveTimeCards for exactly one of the caller's work cards. Always SubmitFlag=false and
    /// SendNotificationEmailFlag=false; never sends ResourceIdentity, DeleteTimeCards or any submit element.
    /// Create sends CardStatus=D; update sends TimecardUid + Timestamp and no CardStatus (Projector itself saves an
    /// updated card as Draft, including a Rejected one).
    /// WorkDate and ProjectIdentity are sent on update too (Projector rejects an update without WorkDate); the caller
    /// passes the card's current values, since this tool does not move cards.
    /// Element order follows PwsSaveTimeCardsRq and PwsTimeCardDetail (base members first).
    /// </summary>
    public static string BuildSaveTimecard(string sessionTicket, Domain.Timecards.TimecardSaveRequest card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var date = ProjectorDateHelpers.ToSoapDate(card.WorkDate);
        var isUpdate = !string.IsNullOrWhiteSpace(card.TimecardUid);

        var detail = new XElement(SoapNamespaces.Tim + "PwsTimecardDetail",
            new XElement(SoapNamespaces.Com + "ReferenceId", "1"),
            new XElement(SoapNamespaces.Com + "TimecardType", "T"),
            isUpdate ? new XElement(SoapNamespaces.Com + "TimecardUid", card.TimecardUid!.Trim()) : null,
            new XElement(SoapNamespaces.Tim + "Description", card.Description),
            new XElement(SoapNamespaces.Tim + "WorkDate", date),
            new XElement(SoapNamespaces.Tim + "WorkMinutes", card.WorkMinutes),
            isUpdate && !string.IsNullOrWhiteSpace(card.Timestamp)
                ? new XElement(SoapNamespaces.Tim + "Timestamp", card.Timestamp)
                : null,
            isUpdate ? null : new XElement(SoapNamespaces.Tim + "CardStatus", "D"),
            string.IsNullOrWhiteSpace(card.LocationName)
                ? null
                : new XElement(SoapNamespaces.Tim + "LocationIdentity",
                    new XElement(SoapNamespaces.Com + "LocationName", card.LocationName.Trim())),
            new XElement(SoapNamespaces.Tim + "ProjectIdentity",
                new XElement(SoapNamespaces.Com + "ProjectCode", card.ProjectCode.Trim())),
            new XElement(SoapNamespaces.Tim + "ProjectRateTypeIdentity",
                new XElement(SoapNamespaces.Com + "ProjectRateTypeUid", card.RateTypeUid)),
            new XElement(SoapNamespaces.Tim + "ProjectTaskIdentity",
                new XElement(SoapNamespaces.Com + "ProjectTaskUid", card.TaskUid)),
            new XElement(SoapNamespaces.Tim + "RoleIdentity",
                new XElement(SoapNamespaces.Com + "ProjectRoleUid", card.RoleUid)),
            BuildUdf("Udf1", card.Udf1),
            BuildUdf("Udf2", card.Udf2));

        var body = new XElement(SoapNamespaces.Pws + "PwsSaveTimeCards",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Tim + "EndDate", date),
                isUpdate ? new XElement(SoapNamespaces.Tim + "InsertIfNotFoundOnUpdateFlag", "false") : null,
                new XElement(SoapNamespaces.Tim + "SaveTimeCards", detail),
                new XElement(SoapNamespaces.Tim + "SendNotificationEmailFlag", "false"),
                new XElement(SoapNamespaces.Tim + "StartDate", date),
                new XElement(SoapNamespaces.Tim + "SubmitFlag", "false")));
        return EnvelopeString(body, includeTim: true);
    }

    private static XElement? BuildUdf(string elementName, Domain.Timecards.TimecardUdfValue? udf)
    {
        if (udf is null)
        {
            return null;
        }

        // Identify the UDF by UID when known, else by name (as in the PwsSaveTimeCards update example).
        return new XElement(SoapNamespaces.Tim + elementName,
            string.IsNullOrWhiteSpace(udf.UdfUid)
                ? new XElement(SoapNamespaces.Com + "UdfName", udf.UdfName)
                : new XElement(SoapNamespaces.Com + "UdfUid", udf.UdfUid),
            new XElement(SoapNamespaces.Com + "TextValue", udf.TextValue));
    }

    /// <summary>A null resource sends no ResourceIdentity: Projector returns the caller's schedule.</summary>
    public static string BuildGetResourceSchedule(
        string sessionTicket,
        string? resourceReferenceSystemId,
        string startDate,
        string endDate)
    {
        var start = ProjectorDateHelpers.ToSoapDate(startDate);
        var end = ProjectorDateHelpers.ToSoapDate(endDate);
        var body = new XElement(SoapNamespaces.Pws + "PwsGetResourceSchedule",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Tim + "EndDate", end),
                new XElement(SoapNamespaces.Tim + "IncludeScheduledTimeFlag", "true"),
                new XElement(SoapNamespaces.Tim + "IncludeTimeOffFlag", "true"),
                ResourceIdentity(resourceReferenceSystemId),
                new XElement(SoapNamespaces.Tim + "StartDate", start)));
        return EnvelopeString(body, includeTim: true);
    }

    public static string BuildGetResourcePto(
        string sessionTicket,
        string resourceReferenceSystemId,
        string cutoffDate)
    {
        var cutoff = ProjectorDateHelpers.ToSoapDate(cutoffDate);
        var body = new XElement(SoapNamespaces.Pws + "PwsGetResourcePto",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Tim + "CutoffDate", cutoff),
                new XElement(SoapNamespaces.Tim + "IncludePastRequestedTimeOffFlag", "false"),
                new XElement(SoapNamespaces.Tim + "ResourceIdentity",
                    new XElement(SoapNamespaces.Com + "ResourceReferenceSystemId", resourceReferenceSystemId))));
        return EnvelopeString(body, includeTim: true);
    }

    public static string BuildGetEngagementList(
        string sessionTicket,
        bool includeClosed = true,
        int maxRows = 50,
        string? query = null)
    {
        var body = new XElement(SoapNamespaces.Pws + "PwsGetEngagementList",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Sch + "IncludeClosedFlag", includeClosed ? "true" : "false"),
                new XElement(SoapNamespaces.Sch + "MaxRowsToReturn", maxRows),
                string.IsNullOrWhiteSpace(query)
                    ? null
                    : new XElement(SoapNamespaces.Sch + "QueryString", query.Trim())));
        return EnvelopeString(body, includeSch: true);
    }

    public static string BuildGetEngagement(string sessionTicket, IEnumerable<string> engagementCodes)
    {
        var identities = engagementCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(ProjectorIdentityRefs.BuildEngagementRef)
            .ToArray();

        var body = new XElement(SoapNamespaces.Pws + "PwsGetEngagement",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Sch + "EngagementIdentities", identities)));
        return EnvelopeString(body, includeSch: true);
    }

    public static string BuildGetProject(string sessionTicket, IEnumerable<string> projectCodes)
    {
        var identities = projectCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(ProjectorIdentityRefs.BuildProjectRef)
            .ToArray();

        var body = new XElement(SoapNamespaces.Pws + "PwsGetProject",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Sch + "Mode", "R"),
                new XElement(SoapNamespaces.Sch + "ProjectIdentities", identities),
                new XElement(SoapNamespaces.Sch + "ExcludeSubEntityElementsFlag", "true")));
        return EnvelopeString(body, includeSch: true);
    }

    /// <summary>One project with its sub-entities (tasks and their role assignments). Large on big projects.</summary>
    public static string BuildGetProjectWithTasks(string sessionTicket, string projectCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectCode);
        var body = new XElement(SoapNamespaces.Pws + "PwsGetProject",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Sch + "Mode", "R"),
                new XElement(SoapNamespaces.Sch + "ProjectIdentities", ProjectorIdentityRefs.BuildProjectRef(projectCode.Trim())),
                new XElement(SoapNamespaces.Sch + "ExcludeSubEntityElementsFlag", "false")));
        return EnvelopeString(body, includeSch: true);
    }

    public static string BuildGetProjectRoles(
        string sessionTicket,
        IEnumerable<string> projectCodes,
        string mode = "A")
    {
        var identities = projectCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => ProjectorIdentityRefs.BuildProjectRef(c.Trim()))
            .ToArray();
        if (identities.Length == 0)
        {
            throw new ArgumentException("At least one project_code is required.");
        }

        var body = new XElement(SoapNamespaces.Pws + "PwsGetProjectRoles",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Sch + "IncludeDeletedRolesFlag", "false"),
                new XElement(SoapNamespaces.Sch + "Mode", mode),
                new XElement(SoapNamespaces.Sch + "ProjectIdentities", identities)));
        return EnvelopeString(body, includeSch: true);
    }

    /// <summary>
    /// Wiki Example 01: ProjectIdentity with direct com:ProjectCode (no PwsProjectRef).
    /// Do not include ProjectRoleIdentities in the same request.
    /// </summary>
    public static string BuildGetResourceSchedulingRoleData(
        string sessionTicket,
        string projectCode,
        string startDate,
        string requestOrScheduleMode = "A",
        int minimumWeekCount = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectCode);
        if (minimumWeekCount < 1)
        {
            minimumWeekCount = 1;
        }

        var body = new XElement(SoapNamespaces.Pws + "PwsGetResourceSchedulingRoleData",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Sch + "IncludeActualTimeDataFlag", "false"),
                new XElement(SoapNamespaces.Sch + "IncludeConcurrentRolesFlag", "false"),
                new XElement(SoapNamespaces.Sch + "IncludeTaskDataFlag", "false"),
                new XElement(SoapNamespaces.Sch + "MinimumWeekCount", minimumWeekCount),
                new XElement(SoapNamespaces.Sch + "ProjectIdentity",
                    new XElement(SoapNamespaces.Com + "ProjectCode", projectCode.Trim())),
                new XElement(SoapNamespaces.Sch + "RequestOrScheduleMode", requestOrScheduleMode),
                new XElement(SoapNamespaces.Sch + "StartDate", ProjectorDateHelpers.ToSoapDate(startDate))));
        return EnvelopeString(body, includeSch: true);
    }

    public static string BuildGetResourceUtilization(string sessionTicket, string resourceReferenceSystemId)
    {
        var body = new XElement(SoapNamespaces.Pws + "PwsGetResourceUtilizationSummary",
            new XElement(SoapNamespaces.Pws + "serviceRequest",
                new XElement(SoapNamespaces.Req + "SessionTicket", sessionTicket),
                new XElement(SoapNamespaces.Tim + "IncludeSubmittedTimeFlag", "true"),
                new XElement(SoapNamespaces.Tim + "ResourceIdentity",
                    new XElement(SoapNamespaces.Com + "ResourceReferenceSystemId", resourceReferenceSystemId))));
        return EnvelopeString(body, includeTim: true);
    }

    public static string BuildExportScheduledTimeoff(
        string sessionTicket,
        string dateBookmark,
        int maxRows = 10000)
    {
        var date = ProjectorDateHelpers.ToShortDate(dateBookmark);
        var doc = new XDocument(
            new XElement(SoapNamespaces.SoapEnv + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soapenv", SoapNamespaces.SoapEnv),
                new XAttribute(XNamespace.Xmlns + "data", SoapNamespaces.Data),
                new XElement(SoapNamespaces.SoapEnv + "Header",
                    new XElement(SoapNamespaces.Data + "OpsAuthenticationHeader",
                        new XElement(SoapNamespaces.Data + "SessionTicket", sessionTicket))),
                new XElement(SoapNamespaces.SoapEnv + "Body",
                    new XElement(SoapNamespaces.Data + "ExportScheduledTimeoff",
                        new XElement(SoapNamespaces.Data + "request",
                            new XElement(SoapNamespaces.Data + "Parameters",
                                new XElement(SoapNamespaces.Data + "MaxRowsToReturn", maxRows),
                                new XElement(SoapNamespaces.Data + "OnlyCountRows", "false"),
                                new XElement(SoapNamespaces.Data + "IncludeRequestsFlag", "false"),
                                new XElement(SoapNamespaces.Data + "DateBookmark", date)))))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    public static string BuildExportResources(
        string sessionTicket,
        string effectiveDate,
        string effectiveDateEnd,
        bool includeInactive = false,
        int maxRows = 10000)
    {
        var from = ProjectorDateHelpers.ToShortDate(effectiveDate);
        var to = ProjectorDateHelpers.ToShortDate(effectiveDateEnd);
        var doc = new XDocument(
            new XElement(SoapNamespaces.SoapEnv + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soapenv", SoapNamespaces.SoapEnv),
                new XAttribute(XNamespace.Xmlns + "data", SoapNamespaces.Data),
                new XElement(SoapNamespaces.SoapEnv + "Header",
                    new XElement(SoapNamespaces.Data + "OpsAuthenticationHeader",
                        new XElement(SoapNamespaces.Data + "SessionTicket", sessionTicket))),
                new XElement(SoapNamespaces.SoapEnv + "Body",
                    new XElement(SoapNamespaces.Data + "ExportResources",
                        new XElement(SoapNamespaces.Data + "request",
                            new XElement(SoapNamespaces.Data + "Parameters",
                                new XElement(SoapNamespaces.Data + "MaxRowsToReturn", maxRows),
                                new XElement(SoapNamespaces.Data + "OnlyCountRows", "false"),
                                new XElement(SoapNamespaces.Data + "EffectiveDate", from),
                                new XElement(SoapNamespaces.Data + "EffectiveDateEnd", to),
                                new XElement(SoapNamespaces.Data + "IncludeInactiveFlag", includeInactive ? "true" : "false")))))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static string EnvelopeString(XElement body, bool includeTim = false, bool includeSch = false)
    {
        var envelope = new XElement(SoapNamespaces.SoapEnv + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soapenv", SoapNamespaces.SoapEnv),
            new XAttribute(XNamespace.Xmlns + "pws", SoapNamespaces.Pws),
            new XAttribute(XNamespace.Xmlns + "req", SoapNamespaces.Req),
            new XAttribute(XNamespace.Xmlns + "com", SoapNamespaces.Com));

        if (includeTim)
        {
            envelope.Add(new XAttribute(XNamespace.Xmlns + "tim", SoapNamespaces.Tim));
        }

        if (includeSch)
        {
            envelope.Add(new XAttribute(XNamespace.Xmlns + "sch", SoapNamespaces.Sch));
        }

        envelope.Add(new XElement(SoapNamespaces.SoapEnv + "Header"));
        envelope.Add(new XElement(SoapNamespaces.SoapEnv + "Body", body));
        return new XDocument(envelope).ToString(SaveOptions.DisableFormatting);
    }
}
