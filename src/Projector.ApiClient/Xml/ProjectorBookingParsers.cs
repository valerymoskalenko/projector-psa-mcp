using System.Globalization;
using System.Xml.Linq;
using Projector.Domain.Bookings;

namespace Projector.ApiClient.Xml;

/// <summary>Parsers for role schedule state and the three booking write responses.</summary>
public static class ProjectorBookingParsers
{
    public static IReadOnlyList<RoleScheduleState> ParseRoleSchedules(XDocument response)
    {
        var emailByUid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var res in XmlNodeHelpers.LocalNodes(response, "PwsResourceSchedule"))
        {
            var uid = XmlNodeHelpers.Value(res, "ResourceUid");
            var email = XmlNodeHelpers.Value(res, "EmailAddress");
            if (!string.IsNullOrWhiteSpace(uid) && !string.IsNullOrWhiteSpace(email))
            {
                emailByUid[uid] = email;
            }
        }

        var roles = new List<RoleScheduleState>();
        foreach (var role in XmlNodeHelpers.LocalNodes(response, "PwsProjectRoleSchedule"))
        {
            var roleUid = XmlNodeHelpers.NestedValue(role, "ProjectRoleIdentity", "ProjectRoleUid")
                ?? XmlNodeHelpers.Value(role, "ProjectRoleUid");
            if (string.IsNullOrWhiteSpace(roleUid))
            {
                continue;
            }

            var resourceNode = SelectCandidate(role);
            var resourceUid = XmlNodeHelpers.Value(resourceNode, "ResourceUid");
            var email = XmlNodeHelpers.Value(resourceNode, "EmailAddress");
            if (string.IsNullOrWhiteSpace(email)
                && !string.IsNullOrWhiteSpace(resourceUid)
                && emailByUid.TryGetValue(resourceUid, out var mapped))
            {
                email = mapped;
            }

            var notesByWeek = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var notesParent = role.Elements().FirstOrDefault(e => e.Name.LocalName == "NotesBuckets");
            foreach (var bucket in XmlNodeHelpers.LocalNodes(notesParent, "PwsProjectRoleNotesBucket"))
            {
                var weekStart = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(bucket, "BucketStartDate"));
                if (string.IsNullOrWhiteSpace(weekStart))
                {
                    continue;
                }

                notesByWeek[weekStart] = ReadSeven(bucket, "Notes", parseInt: false)
                    .Select(s => s ?? string.Empty)
                    .ToArray();
            }

            var weeks = new Dictionary<string, RoleWeekState>(StringComparer.Ordinal);
            var bookedParent = role.Elements().FirstOrDefault(e => e.Name.LocalName == "BookedBuckets");
            foreach (var bucket in XmlNodeHelpers.LocalNodes(bookedParent, "PwsProjectRoleHoursBucket"))
            {
                var weekStart = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(bucket, "BucketStartDate"));
                if (string.IsNullOrWhiteSpace(weekStart))
                {
                    continue;
                }

                var mode = XmlNodeHelpers.Value(bucket, "SchedulingMode")?.Trim().ToUpperInvariant() ?? "W";
                var daily = ReadSevenInts(bucket);
                var weeklyRaw = XmlNodeHelpers.Value(bucket, "WeeklyMinutes");
                var weekly = int.TryParse(weeklyRaw, out var w) ? w : daily.Sum();

                weeks[weekStart] = new RoleWeekState
                {
                    WeekStart = weekStart,
                    SchedulingMode = mode,
                    WeeklyMinutes = weekly,
                    DailyMinutes = daily,
                    Notes = notesByWeek.GetValueOrDefault(weekStart) ?? ["", "", "", "", "", "", ""]
                };
            }

            foreach (var (weekStart, notes) in notesByWeek)
            {
                if (weeks.ContainsKey(weekStart))
                {
                    continue;
                }

                weeks[weekStart] = new RoleWeekState
                {
                    WeekStart = weekStart,
                    SchedulingMode = null,
                    WeeklyMinutes = 0,
                    DailyMinutes = [0, 0, 0, 0, 0, 0, 0],
                    Notes = notes
                };
            }

            roles.Add(new RoleScheduleState
            {
                RoleUid = roleUid,
                RoleName = XmlNodeHelpers.Value(role, "ProjectRoleName"),
                ResourceId = XmlNodeHelpers.Value(resourceNode, "ResourceReferenceSystemId"),
                ResourceUid = resourceUid,
                DisplayName = XmlNodeHelpers.Value(resourceNode, "ResourceDisplayName"),
                Email = email,
                Weeks = weeks.Values.OrderBy(w => w.WeekStart, StringComparer.Ordinal).ToList()
            });
        }

        return roles;
    }

    public static SaveProjectRoleResult ParseSaveProjectRole(XDocument response)
    {
        var result = XmlNodeHelpers.LocalNode(response, "PwsSaveProjectRoleResult");
        ProjectorSoapHttp.ThrowIfResultError(result);
        var identity = XmlNodeHelpers.LocalNode(result, "ProjectRoleIdentity")
            ?? XmlNodeHelpers.LocalNode(result, "PwsProjectRoleRef")
            ?? result;
        var uid = XmlNodeHelpers.Value(identity, "ProjectRoleUid")
            ?? throw new InvalidOperationException("PwsSaveProjectRole returned no ProjectRoleUid.");
        return new SaveProjectRoleResult
        {
            RoleUid = uid,
            RoleName = XmlNodeHelpers.Value(identity, "ExternalSystemIdentifier")
        };
    }

    public static void ParseSaveProjectTaskRole(XDocument response)
    {
        var result = XmlNodeHelpers.LocalNode(response, "PwsSaveProjectTaskRoleResult");
        ProjectorSoapHttp.ThrowIfResultError(result);
    }

    public static void ParseBookRoleHours(XDocument response)
    {
        var result = XmlNodeHelpers.LocalNode(response, "PwsRequestOrBookRoleHoursResult");
        ProjectorSoapHttp.ThrowIfResultError(result);
    }

    private static XElement? SelectCandidate(XElement role)
    {
        var candidates = role.Elements().FirstOrDefault(e => e.Name.LocalName == "Candidates");
        foreach (var candidate in XmlNodeHelpers.LocalNodes(candidates, "PwsProjectRoleCandidate"))
        {
            var selected = XmlNodeHelpers.Value(candidate, "SelectedCandidateFlag");
            var assigned = XmlNodeHelpers.Value(candidate, "AssignedCandidateFlag");
            if (string.Equals(selected, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(assigned, "true", StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Elements().FirstOrDefault(e => e.Name.LocalName == "ResourceIdentity")
                    ?? candidate;
            }
        }

        return role.Elements().FirstOrDefault(e => e.Name.LocalName == "ResourceIdentity")
            ?? XmlNodeHelpers.LocalNodes(candidates, "PwsProjectRoleCandidate").FirstOrDefault();
    }

    private static int[] ReadSevenInts(XElement bucket)
    {
        var values = ReadSeven(bucket, "DailyMinutes", parseInt: true);
        var minutes = new int[7];
        for (var i = 0; i < 7; i++)
        {
            minutes[i] = int.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
                ? m
                : 0;
        }

        return minutes;
    }

    private static string[] ReadSeven(XElement parent, string localName, bool parseInt)
    {
        var node = parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        var result = new string[7];
        if (node is null)
        {
            return result;
        }

        var children = node.Elements().ToList();
        if (children.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(node.Value))
            {
                result[0] = node.Value.Trim();
            }

            return result;
        }

        for (var i = 0; i < 7 && i < children.Count; i++)
        {
            var nil = children[i].Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "nil");
            if (nil is not null && string.Equals(nil.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                result[i] = string.Empty;
                continue;
            }

            result[i] = children[i].Value?.Trim() ?? string.Empty;
        }

        return result;
    }
}
