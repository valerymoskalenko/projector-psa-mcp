using System.Xml.Linq;
using Projector.Domain.Exceptions;
using Projector.Domain.Timecards;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Parsers for time entry lookups and PwsSaveTimeCards. Reads direct children where names repeat
/// deeper in the tree (task Name vs role names, nested UIDs).
/// </summary>
public static class ProjectorTimeEntryParsers
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public static IReadOnlyList<TimeEntryProjectSummary> ParseSearchProjects(XDocument response)
    {
        var projects = new List<TimeEntryProjectSummary>();
        foreach (var d in XmlNodeHelpers.LocalNodes(response, "PwsProjectDescriptor"))
        {
            var code = ChildValue(d, "ProjectCode");
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            projects.Add(new TimeEntryProjectSummary
            {
                ProjectCode = code,
                ProjectUid = ChildValue(d, "ProjectUid"),
                ProjectName = ChildValue(d, "ProjectName"),
                EngagementCode = ChildValue(Child(d, "EngagementDescriptor"), "EngagementCode"),
                EngagementName = ChildValue(Child(d, "EngagementDescriptor"), "EngagementName"),
                ClientName = XmlNodeHelpers.NestedValue(d, "EngagementDescriptor", "ClientDescriptor", "ClientName"),
                Billable = ParseBool(XmlNodeHelpers.NestedValue(d, "EngagementDescriptor", "EngagementTypeDescriptor", "BillableFlag")),
                LocationName = ChildValue(Child(d, "LocationIdentity"), "LocationName"),
                UnavailableReasonCode = ChildValue(d, "UnavailableReasonCode"),
                Roles = ParseRoles(Child(d, "ProjectRoles"))
            });
        }

        return projects;
    }

    public static TimeEntryProjectSetup? ParseTimeEntryProject(XDocument response)
    {
        var p = XmlNodeHelpers.LocalNode(response, "TimeEntryProject");
        if (p is null || IsNil(p))
        {
            return null;
        }

        var descriptor = Child(p, "ProjectDescriptor");
        var code = ChildValue(descriptor, "ProjectCode");
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var taskTypes = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var type in XmlNodeHelpers.ChildLocalNodes(Child(p, "ProjectTaskTypes"), "PwsTaskType"))
        {
            var uid = ChildValue(type, "ProjectTaskTypeUid");
            if (uid is not null)
            {
                taskTypes[uid] = type;
            }
        }

        var tasks = new List<TimeEntryTask>();
        foreach (var t in XmlNodeHelpers.ChildLocalNodes(Child(p, "ProjectTasks"), "PwsProjectTask"))
        {
            var uid = ChildValue(t, "ProjectTaskUid");
            if (uid is null)
            {
                continue;
            }

            var typeUid = ChildValue(Child(t, "ProjectTaskTypeIdentity"), "ProjectTaskTypeUid");
            taskTypes.TryGetValue(typeUid ?? string.Empty, out var type);
            tasks.Add(new TimeEntryTask
            {
                Uid = uid,
                Name = ChildValue(t, "Name"),
                WbsCode = ChildValue(t, "WbsCode"),
                ParentTaskName = ChildValue(t, "ParentTaskName"),
                ParentTaskUid = ChildValue(Child(t, "ParentProjectTaskIdentity"), "ProjectTaskUid"),
                OpenForTime = ParseBool(ChildValue(t, "OpenForTimeFlag")) == true,
                TaskTypeName = ChildValue(type, "ProjectTaskTypeName"),
                NarrativeRequired = ParseBool(ChildValue(type, "NarrativeRequiredFlag")) == true,
                AllowedRateTypes = ParseRateTypes(Child(type, "AllowedProjectRateTypes")),
                DefaultRateTypeUid = ChildValue(Child(type, "DefaultProjectRateTypeIdentity"), "ProjectRateTypeUid")
            });
        }

        return new TimeEntryProjectSetup
        {
            ProjectCode = code,
            ProjectName = ChildValue(descriptor, "ProjectName"),
            EngagementCode = ChildValue(Child(descriptor, "EngagementDescriptor"), "EngagementCode"),
            ClientName = XmlNodeHelpers.NestedValue(descriptor, "EngagementDescriptor", "ClientDescriptor", "ClientName"),
            Billable = ParseBool(XmlNodeHelpers.NestedValue(descriptor, "EngagementDescriptor", "EngagementTypeDescriptor", "BillableFlag")),
            OpenForTime = ParseBool(ChildValue(p, "OpenFlag")) == true,
            DescriptionRequired = ParseBool(ChildValue(p, "DescriptionRequiredFlag")) == true,
            Udf1Treatment = ChildValue(p, "Udf1Treatment"),
            Udf2Treatment = ChildValue(p, "Udf2Treatment"),
            RateTypes = ParseRateTypes(Child(p, "ProjectRateTypes")),
            Tasks = WithPaths(tasks)
        };
    }

    public static TimeEntryParameters ParseTimeEntryParameters(XDocument response)
    {
        var p = XmlNodeHelpers.LocalNode(response, "Parameters");
        var increment = int.TryParse(ChildValue(p, "ReportingTimeIncrement"), out var n) && n > 0 ? n : 1;
        return new TimeEntryParameters
        {
            ReportingTimeIncrementMinutes = increment,
            RequireLocation = ParseBool(ChildValue(p, "RequireLocationFlag")) == true,
            Enforce24HourDailyLimit = ParseBool(ChildValue(p, "Enforce24HourDailyLimitFlag")) == true,
            Udf1 = ParseUdf(Child(p, "Udf1")),
            Udf2 = ParseUdf(Child(p, "Udf2"))
        };
    }

    public static OwnTimecard? ParseOwnTimecard(XDocument response, string timecardUid)
    {
        foreach (var project in XmlNodeHelpers.LocalNodes(response, "PwsTimeEntryProject"))
        {
            foreach (var card in XmlNodeHelpers.LocalNodes(project, "PwsTimecardDetail"))
            {
                if (!string.Equals(ChildValue(card, "TimecardUid"), timecardUid.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                return new OwnTimecard
                {
                    TimecardUid = timecardUid.Trim(),
                    WorkDate = XmlNodeHelpers.ShortDate(ChildValue(card, "WorkDate")),
                    WorkMinutes = int.TryParse(ChildValue(card, "WorkMinutes"), out var m) ? m : 0,
                    CardStatusCode = ChildValue(card, "CardStatus"),
                    ProjectCode = ChildValue(Child(card, "ProjectIdentity"), "ProjectCode")
                        ?? XmlNodeHelpers.NestedValue(project, "ProjectDescriptor", "ProjectCode"),
                    TaskUid = ChildValue(Child(card, "ProjectTaskIdentity"), "ProjectTaskUid"),
                    RoleUid = ChildValue(Child(card, "RoleIdentity"), "ProjectRoleUid"),
                    RateTypeUid = ChildValue(Child(card, "ProjectRateTypeIdentity"), "ProjectRateTypeUid"),
                    Description = ChildValue(card, "Description"),
                    Timestamp = ChildValue(card, "Timestamp")
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the single card result. A per-card ErrorDetail is thrown with Projector's own code
    /// before any summary message (e.g. TimeCardErrors) so the caller sees the real reason.
    /// </summary>
    public static TimecardSaveResult ParseSaveResult(XDocument response)
    {
        var result = XmlNodeHelpers.LocalNode(response, "PwsSaveTimeCardsResult");
        var submitted = ParseBool(ChildValue(result, "SubmittedFlag")) == true;

        var cardResult = XmlNodeHelpers.ChildLocalNodes(Child(result, "TimecardResults"), "PwsSaveTimecardResult")
            .Concat(XmlNodeHelpers.ChildLocalNodes(Child(result, "TimecardResults"), "PwsSaveTimeCardResult"))
            .FirstOrDefault();

        var error = Child(cardResult, "ErrorDetail");
        if (error is not null && !IsNil(error))
        {
            var code = XmlNodeHelpers.Value(error, "ErrorCode") ?? "TimeCardErrors";
            var text = XmlNodeHelpers.Value(error, "ErrorText")
                ?? XmlNodeHelpers.Value(error, "AdditionalErrorText")
                ?? XmlNodeHelpers.Value(error, "MessageText")
                ?? code;
            throw new ProjectorApiException(text, code);
        }

        ProjectorSoapHttp.ThrowIfResultError(result);

        var card = Child(cardResult, "TimeCard");
        if (card is null || IsNil(card))
        {
            throw new ProjectorApiException(
                "Projector returned no time card result for the save.", "write_outcome_unknown");
        }

        return new TimecardSaveResult
        {
            TimecardUid = ChildValue(card, "TimecardUid"),
            WorkDate = XmlNodeHelpers.ShortDate(ChildValue(card, "WorkDate")),
            WorkMinutes = int.TryParse(ChildValue(card, "WorkMinutes"), out var m) ? m : null,
            CardStatusCode = ChildValue(card, "CardStatus"),
            SubmittedFlag = submitted
        };
    }

    private static IReadOnlyList<TimeEntryRole> ParseRoles(XElement? roles) =>
        XmlNodeHelpers.ChildLocalNodes(roles, "PwsRole")
            .Select(r => (Uid: ChildValue(r, "ProjectRoleUid"), Role: r))
            .Where(x => x.Uid is not null)
            .Select(x => new TimeEntryRole(
                x.Uid!,
                ChildValue(x.Role, "RoleName"),
                XmlNodeHelpers.ShortDate(ChildValue(x.Role, "RoleStartDate")),
                XmlNodeHelpers.ShortDate(ChildValue(x.Role, "RoleEndDate"))))
            .ToList();

    private static IReadOnlyList<TimeEntryRateType> ParseRateTypes(XElement? parent) =>
        XmlNodeHelpers.ChildLocalNodes(parent, "PwsProjectRateTypeSummary")
            .Select(r => (Uid: ChildValue(r, "ProjectRateTypeUid"), Name: ChildValue(r, "ProjectRateTypeName")))
            .Where(x => x.Uid is not null)
            .Select(x => new TimeEntryRateType(x.Uid!, x.Name))
            .ToList();

    private static TimeEntryUdf? ParseUdf(XElement? udf)
    {
        if (udf is null || IsNil(udf))
        {
            return null;
        }

        var identity = Child(udf, "UdfIdentity");
        var values = Child(udf, "UdfValues")?.Elements()
            .Select(v => v.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList() ?? [];
        return new TimeEntryUdf(
            ChildValue(identity, "UdfUid"),
            ChildValue(identity, "UdfName"),
            ChildValue(udf, "UdfDatatype"),
            ParseBool(ChildValue(udf, "UdfRequiredFlag")) == true,
            values);
    }

    private static IReadOnlyList<TimeEntryTask> WithPaths(List<TimeEntryTask> tasks)
    {
        var paths = TaskPaths.Build(tasks.Select(t => (t.Uid, t.Name, t.ParentTaskUid)));
        foreach (var task in tasks)
        {
            task.Path = paths.GetValueOrDefault(task.Uid);
        }

        return tasks;
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? ChildValue(XElement? parent, string localName)
    {
        var node = Child(parent, localName);
        if (node is null || IsNil(node))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(node.Value) ? null : node.Value;
    }

    private static bool IsNil(XElement e) =>
        string.Equals((string?)e.Attribute(Xsi + "nil"), "true", StringComparison.OrdinalIgnoreCase);

    private static bool? ParseBool(string? raw) =>
        bool.TryParse(raw, out var b) ? b : null;
}
