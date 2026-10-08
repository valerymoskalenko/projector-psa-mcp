using System.Globalization;
using System.Xml.Linq;
using Projector.Domain.Bookings;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Request bodies for role create, task-role assign and scheduler booking. Element order follows the WCF contracts.
/// Never includes submit or finalize order elements.
/// </summary>
public static class ProjectorBookingEnvelopes
{
    private static readonly XNamespace Pws = SoapNamespaces.Pws;
    private static readonly XNamespace Req = SoapNamespaces.Req;
    private static readonly XNamespace Com = SoapNamespaces.Com;
    private static readonly XNamespace Sch = SoapNamespaces.Sch;
    private static readonly XNamespace Arr = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";

    public static XElement SaveProjectRole(string ticket, SaveProjectRoleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resource = ResourceRef(request.ResourceReferenceSystemId, request.ResourceUid)
            ?? throw new ArgumentException("A resource identity is required to create a role.", nameof(request));

        // Named resource: RoleName + ResourceIdentity + DefaultSchedulingMode. ResourceTypeAnyFlag is deprecated
        // (V19: Deprecated_ResourceTypeAnyFlag). Empty criteria + clear flags = any cost center / location / type.
        var role = new XElement(Sch + "ProjectRole",
            new XElement(Com + "DefaultSchedulingMode", request.DefaultSchedulingMode),
            resource,
            new XElement(Com + "RoleName", request.RoleName.Trim()));

        // Request element order follows PwsSaveProjectRoleRq (clear flags around Mode / ProjectRole).
        return Body("PwsSaveProjectRole", ticket,
            new XElement(Sch + "CostCenterCriteriaClearFlag", "true"),
            new XElement(Sch + "LocationCriteriaClearFlag", "true"),
            new XElement(Sch + "Mode", "A"),
            new XElement(Sch + "ProjectIdentity", new XElement(Com + "ProjectCode", request.ProjectCode.Trim())),
            role,
            new XElement(Sch + "TitleCriteriaClearFlag", "true"),
            new XElement(Sch + "ResourceTypeCriteriaClearFlag", "true"),
            new XElement(Sch + "MakeRoleNameUniqueFlag", "false"),
            new XElement(Sch + "NameRoleFlag", "false"));
    }

    public static XElement SaveProjectTaskRole(string ticket, SaveProjectTaskRoleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var detail = new XElement(Sch + "ProjectTaskRole",
            new XElement(Com + "ProjectRoleIdentity",
                new XElement(Com + "ProjectRoleUid", request.RoleUid.Trim())),
            new XElement(Com + "ProjectTaskIdentity",
                new XElement(Com + "ProjectTaskUid", request.TaskUid.Trim())),
            new XElement(Com + "EffortMinutes", request.EffortMinutes.ToString(CultureInfo.InvariantCulture)));

        return Body("PwsSaveProjectTaskRole", ticket, detail);
    }

    /// <summary>
    /// One PwsRequestOrBookRoleHours for a single role. Mode=A (scheduler). Does not clear existing hours for a
    /// period, submit, or finalize. Hours and notes buckets first, then ProjectRoleIdentity (documented order).
    /// </summary>
    public static XElement RequestOrBookRoleHours(string ticket, BookRoleHoursRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RoleUid))
        {
            throw new ArgumentException("RoleUid is required.", nameof(request));
        }

        var hours = request.HoursBuckets.Select(HoursBucket).ToArray();
        var notes = request.NotesBuckets
            .Where(b => b.Notes is not null)
            .Select(NotesBucket)
            .ToArray();

        // Array item types live in Common (same as PwsGetResourceSchedulingRoleData BookedBuckets),
        // while ProjectRoles / Mode stay in Scheduling.
        var role = new XElement(Com + "PwsProjectRoleHours",
            hours.Length == 0 ? null : new XElement(Com + "HoursBuckets", hours),
            notes.Length == 0 ? null : new XElement(Com + "NotesBuckets", notes),
            new XElement(Com + "ProjectRoleIdentity",
                new XElement(Com + "ProjectRoleUid", request.RoleUid.Trim())));

        return Body("PwsRequestOrBookRoleHours", ticket,
            new XElement(Sch + "Mode", "A"),
            new XElement(Sch + "ProjectRoles", role));
    }

    private static XElement HoursBucket(RoleHoursBucket bucket)
    {
        var mode = bucket.SchedulingMode.Trim().ToUpperInvariant();
        var el = new XElement(Com + "PwsProjectRoleHoursBucket",
            new XElement(Com + "BucketStartDate", ProjectorDateHelpers.ToSoapDate(bucket.WeekStart)));

        if (mode == "D")
        {
            var days = bucket.DailyMinutes ?? throw new ArgumentException(
                $"Daily bucket for {bucket.WeekStart} needs DailyMinutes.", nameof(bucket));
            if (days.Count != 7)
            {
                throw new ArgumentException("DailyMinutes must have exactly 7 entries.", nameof(bucket));
            }

            el.Add(new XElement(Com + "DailyMinutes",
                days.Select(m => new XElement(Arr + "short", ClampShort(m).ToString(CultureInfo.InvariantCulture)))));
            el.Add(new XElement(Com + "SchedulingMode", "D"));
        }
        else
        {
            var weekly = bucket.WeeklyMinutes
                ?? throw new ArgumentException($"Weekly bucket for {bucket.WeekStart} needs WeeklyMinutes.", nameof(bucket));
            el.Add(new XElement(Com + "SchedulingMode", "W"));
            el.Add(new XElement(Com + "WeeklyMinutes", ClampShort(weekly).ToString(CultureInfo.InvariantCulture)));
        }

        return el;
    }

    private static XElement NotesBucket(RoleHoursBucket bucket)
    {
        var notes = bucket.Notes ?? throw new ArgumentException("NotesBucket needs Notes.", nameof(bucket));
        if (notes.Count != 7)
        {
            throw new ArgumentException("Notes must have exactly 7 entries.", nameof(bucket));
        }

        return new XElement(Com + "PwsProjectRoleNotesBucket",
            new XElement(Com + "BucketStartDate", ProjectorDateHelpers.ToSoapDate(bucket.WeekStart)),
            new XElement(Com + "Notes",
                notes.Select(n => new XElement(Arr + "string", n ?? string.Empty))));
    }

    private static XElement? ResourceRef(string? referenceSystemId, string? resourceUid)
    {
        if (!string.IsNullOrWhiteSpace(referenceSystemId))
        {
            return new XElement(Com + "ResourceIdentity",
                new XElement(Com + "ResourceReferenceSystemId", referenceSystemId.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(resourceUid))
        {
            return new XElement(Com + "ResourceIdentity",
                new XElement(Com + "ResourceUid", resourceUid.Trim()));
        }

        return null;
    }

    private static int ClampShort(int minutes) =>
        minutes < 0 ? 0 : minutes > short.MaxValue ? short.MaxValue : minutes;

    private static XElement Body(string method, string ticket, params XElement?[] children) =>
        new(Pws + method,
            new XElement(Pws + "serviceRequest",
                new XElement(Req + "SessionTicket", ticket),
                children.Where(c => c is not null)));
}
