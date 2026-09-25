using System.Globalization;
using System.Xml.Linq;
using Projector.Domain.Engagements;
using Projector.Domain.Holidays;
using Projector.Domain.Resources;
using Projector.Domain.Schedule;
using Projector.Domain.Timecards;
using Projector.Domain.TimeOff;
using Projector.Domain.Users;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Response parsers ported from ConvertFrom-Projector* in ProjectorDomain.ps1.
/// </summary>
public static class ProjectorResponseParsers
{
    public static IReadOnlyList<UserSummary> ParseUserList(XDocument response)
    {
        var users = new List<UserSummary>();
        foreach (var itm in XmlNodeHelpers.LocalNodes(response, "PwsUserSummaryElement"))
        {
            var summary = XmlNodeHelpers.LocalNode(itm, "UserSummary") ?? itm;
            users.Add(new UserSummary
            {
                UserUid = XmlNodeHelpers.Value(summary, "UserUid"),
                UserReferenceSystemId = XmlNodeHelpers.Value(summary, "UserReferenceSystemId"),
                EmailAddress = XmlNodeHelpers.Value(summary, "EmailAddress"),
                FirstName = XmlNodeHelpers.Value(summary, "FirstName"),
                LastName = XmlNodeHelpers.Value(summary, "LastName"),
                DisplayName = XmlNodeHelpers.Value(summary, "UserDisplayName"),
                Inactive = XmlNodeHelpers.Bool(summary, "InactiveFlag")
            });
        }

        return users;
    }

    public static UserSummary? ParseUser(XDocument response)
    {
        var element = XmlNodeHelpers.LocalNode(response, "PwsUserElement");
        if (element is null)
        {
            return null;
        }

        var detail = XmlNodeHelpers.LocalNode(element, "UserDetail") ?? element;
        return new UserSummary
        {
            UserUid = XmlNodeHelpers.Value(detail, "UserUid"),
            UserReferenceSystemId = XmlNodeHelpers.Value(detail, "UserReferenceSystemId"),
            EmailAddress = XmlNodeHelpers.Value(detail, "EmailAddress"),
            FirstName = XmlNodeHelpers.Value(detail, "FirstName"),
            LastName = XmlNodeHelpers.Value(detail, "LastName"),
            DisplayName = XmlNodeHelpers.Value(detail, "UserDisplayName"),
            Inactive = XmlNodeHelpers.Bool(detail, "InactiveFlag")
        };
    }

    public static IReadOnlyList<ResourceSummary> ParseResourceList(XDocument response)
    {
        var resources = new List<ResourceSummary>();
        foreach (var node in XmlNodeHelpers.LocalNodes(response, "PwsResourceSummary"))
        {
            resources.Add(new ResourceSummary
            {
                ResourceUid = XmlNodeHelpers.Value(node, "ResourceUid"),
                ResourceReferenceSystemId = XmlNodeHelpers.Value(node, "ResourceReferenceSystemId"),
                DisplayName = XmlNodeHelpers.Value(node, "ResourceDisplayName")
                    ?? XmlNodeHelpers.Value(node, "DisplayName"),
                EmailAddress = XmlNodeHelpers.Value(node, "EmailAddress"),
                Inactive = XmlNodeHelpers.Bool(node, "InactiveFlag")
            });
        }

        return resources;
    }

    public static ResourceDetail? ParseResource(
        XDocument response,
        bool includeHistory = false,
        bool includeUdfs = true)
    {
        var element = XmlNodeHelpers.LocalNode(response, "PwsResourceElement");
        if (element is null)
        {
            return null;
        }

        var detail = XmlNodeHelpers.LocalNode(element, "ResourceDetail") ?? element;
        var latest = XmlNodeHelpers.LocalNode(detail, "LatestHistoryRecord")
            ?? XmlNodeHelpers.LocalNode(element, "LatestHistoryRecord");

        var managerDisplayName = XmlNodeHelpers.NestedValue(detail, "ManagerResourceIdentity", "ResourceDisplayName")
            ?? XmlNodeHelpers.NestedValue(detail, "ManagerUserIdentity", "UserDisplayName");

        var timecardApprover = XmlNodeHelpers.NestedValue(detail, "TemporaryWorkerTimecardApproverIdentity", "ResourceDisplayName")
            ?? XmlNodeHelpers.NestedValue(detail, "TimecardApproverIdentity", "ResourceDisplayName");

        var udfs = new List<ResourceUdf>();
        if (includeUdfs)
        {
            foreach (var udf in XmlNodeHelpers.LocalNodes(detail, "PwsUserDefinedFieldDetail"))
            {
                udfs.Add(new ResourceUdf
                {
                    Name = XmlNodeHelpers.Value(udf, "UdfName"),
                    Value = XmlNodeHelpers.Value(udf, "UdfValue")
                        ?? XmlNodeHelpers.Value(udf, "TextValue")
                        ?? XmlNodeHelpers.Value(udf, "IntegerValue")
                });
            }
        }

        var history = new List<ResourceHistoryEntry>();
        if (includeHistory)
        {
            var activeIndex = int.TryParse(XmlNodeHelpers.Value(element, "ActiveHistoryIndex"), out var idx) ? idx : 0;
            var historyNodes = XmlNodeHelpers.LocalNodes(element, "PwsResourceHistory").ToList();
            for (var i = 0; i < historyNodes.Count; i++)
            {
                var record = XmlNodeHelpers.LocalNode(historyNodes[i], "Record") ?? historyNodes[i];
                bool? trackMissing = null;
                var trackRaw = XmlNodeHelpers.Value(record, "TrackMissingTimeFlag");
                if (!string.IsNullOrWhiteSpace(trackRaw) && bool.TryParse(trackRaw, out var parsed))
                {
                    trackMissing = parsed;
                }

                history.Add(new ResourceHistoryEntry
                {
                    Index = i,
                    IsActive = i == activeIndex,
                    EffectiveDate = XmlNodeHelpers.Value(record, "EffectiveDate"),
                    LocationName = XmlNodeHelpers.NestedValue(record, "LocationIdentity", "LocationName"),
                    CostCenterName = XmlNodeHelpers.NestedValue(record, "CostCenterIdentity", "CostCenterName"),
                    DepartmentName = XmlNodeHelpers.NestedValue(record, "TitleIdentity", "DepartmentIdentity", "DepartmentName"),
                    TitleName = XmlNodeHelpers.NestedValue(record, "TitleIdentity", "TitleName"),
                    ResourceTypeName = XmlNodeHelpers.NestedValue(record, "ResourceTypeIdentity", "ResourceTypeName"),
                    TrackMissingTime = trackMissing
                });
            }
        }

        return new ResourceDetail
        {
            ResourceUid = XmlNodeHelpers.Value(detail, "ResourceUid") ?? XmlNodeHelpers.Value(element, "ResourceUid"),
            ResourceReferenceSystemId = XmlNodeHelpers.Value(detail, "ResourceReferenceSystemId"),
            DisplayName = XmlNodeHelpers.Value(detail, "ResourceDisplayName"),
            FirstName = XmlNodeHelpers.Value(detail, "FirstName"),
            LastName = XmlNodeHelpers.Value(detail, "LastName"),
            EmailAddress = XmlNodeHelpers.Value(detail, "EmailAddress"),
            Inactive = XmlNodeHelpers.Bool(detail, "InactiveFlag"),
            ManagerDisplayName = managerDisplayName,
            ManagerEmail = XmlNodeHelpers.NestedValue(detail, "ManagerResourceIdentity", "EmailAddress"),
            TimecardApproverDisplayName = timecardApprover,
            ExpenseApproverDisplayName = XmlNodeHelpers.NestedValue(detail, "ExpenseApproverIdentity", "ResourceDisplayName"),
            LocationName = XmlNodeHelpers.NestedValue(latest, "LocationIdentity", "LocationName"),
            CostCenterName = XmlNodeHelpers.NestedValue(latest, "CostCenterIdentity", "CostCenterName"),
            DepartmentName = XmlNodeHelpers.NestedValue(latest, "TitleIdentity", "DepartmentIdentity", "DepartmentName"),
            TitleName = XmlNodeHelpers.NestedValue(latest, "TitleIdentity", "TitleName"),
            ResourceTypeName = XmlNodeHelpers.NestedValue(latest, "ResourceTypeIdentity", "ResourceTypeName"),
            Udfs = udfs,
            History = history
        };
    }

    public static IReadOnlyList<ResourceUdf> GetUdfSubset(
        ResourceDetail resource,
        IEnumerable<string>? names = null)
    {
        names ??=
        [
            "Resource Department",
            "Resource Division",
            "Team Manager",
            "Resource Technology",
            "Technology",
            "Service Line"
        ];
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        return resource.Udfs.Where(u => u.Name is not null && wanted.Contains(u.Name)).ToList();
    }

    public static string? MapTimeCardStatus(string? cardStatus)
    {
        if (string.IsNullOrWhiteSpace(cardStatus))
        {
            return null;
        }

        var raw = cardStatus.Trim();
        return raw.ToUpperInvariant() switch
        {
            "D" or "DRAFT" => "Draft",
            "R" or "REJECTED" => "Rejected",
            "S" or "SUBMITTED" => "Submitted",
            "A" or "APPROVED" => "Approved",
            "B" or "BILLED" => "Billed",
            "I" or "INVOICED" => "Invoiced",
            "M" or "MISSING" => "Missing",
            _ => raw
        };
    }

    public static IReadOnlyList<Timecard> ParseTimeCards(
        XDocument response,
        string? projectCode = null,
        string? status = null)
    {
        var cards = new List<Timecard>();
        foreach (var project in XmlNodeHelpers.LocalNodes(response, "PwsTimeEntryProject"))
        {
            var code = XmlNodeHelpers.NestedValue(project, "ProjectDescriptor", "ProjectCode")
                ?? XmlNodeHelpers.Value(project, "ProjectCode");
            var name = XmlNodeHelpers.NestedValue(project, "ProjectDescriptor", "ProjectName");

            if (!string.IsNullOrWhiteSpace(projectCode)
                && !string.Equals(code, projectCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var taskNameByUid = BuildLookup(project, "PwsProjectTask", "ProjectTaskUid", "Name");
            var roleNameByUid = BuildLookup(project, "PwsRole", "ProjectRoleUid", "RoleName");
            var rateNameByUid = BuildLookup(project, "PwsProjectRateTypeSummary", "ProjectRateTypeUid", "ProjectRateTypeName");

            var engagementCode = XmlNodeHelpers.NestedValue(project, "ProjectDescriptor", "EngagementCode")
                ?? XmlNodeHelpers.Value(project, "EngagementCode");
            var engagementName = XmlNodeHelpers.NestedValue(project, "ProjectDescriptor", "EngagementName")
                ?? XmlNodeHelpers.Value(project, "EngagementName");
            var clientName = XmlNodeHelpers.NestedValue(project, "ProjectDescriptor", "ClientDescriptor", "ClientName")
                ?? XmlNodeHelpers.NestedValue(project, "ClientDescriptor", "ClientName");
            var clientNumber = XmlNodeHelpers.NestedValue(project, "ProjectDescriptor", "ClientDescriptor", "ClientNumber")
                ?? XmlNodeHelpers.NestedValue(project, "ClientDescriptor", "ClientNumber");
            var projectStageName = XmlNodeHelpers.NestedValue(project, "ProjectDescriptor", "ProjectStageIdentity", "ProjectStageName")
                ?? XmlNodeHelpers.NestedValue(project, "ProjectStageIdentity", "ProjectStageName");

            bool? billable = null;
            var billableRaw = XmlNodeHelpers.NestedValue(project, "ProjectDescriptor", "EngagementTypeDescriptor", "BillableFlag")
                ?? XmlNodeHelpers.NestedValue(project, "EngagementTypeDescriptor", "BillableFlag");
            if (!string.IsNullOrWhiteSpace(billableRaw) && bool.TryParse(billableRaw, out var parsedBillable))
            {
                billable = parsedBillable;
            }

            foreach (var card in XmlNodeHelpers.LocalNodes(project, "PwsTimecardDetail"))
            {
                var cardStatusHuman = XmlNodeHelpers.Value(card, "Status");
                var cardStatusRaw = XmlNodeHelpers.Value(card, "CardStatus");
                if (string.IsNullOrWhiteSpace(cardStatusHuman) && !string.IsNullOrWhiteSpace(cardStatusRaw))
                {
                    cardStatusHuman = MapTimeCardStatus(cardStatusRaw);
                }

                if (!string.IsNullOrWhiteSpace(status)
                    && !string.Equals(cardStatusHuman, status, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(MapTimeCardStatus(cardStatusRaw), status, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var workDate = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(card, "WorkDate"));
                var minutes = XmlNodeHelpers.Int(card, "WorkMinutes");

                var rateTypeUid = XmlNodeHelpers.NestedValue(card, "ProjectRateTypeIdentity", "ProjectRateTypeUid");
                var taskUid = XmlNodeHelpers.NestedValue(card, "ProjectTaskIdentity", "ProjectTaskUid");
                var roleUid = XmlNodeHelpers.NestedValue(card, "RoleIdentity", "ProjectRoleUid");

                var rateTypeName = XmlNodeHelpers.NestedValue(card, "ProjectRateTypeIdentity", "ProjectRateTypeName");
                if (string.IsNullOrWhiteSpace(rateTypeName) && rateTypeUid is not null
                    && rateNameByUid.TryGetValue(rateTypeUid, out var rn))
                {
                    rateTypeName = rn;
                }

                var taskName = XmlNodeHelpers.NestedValue(card, "ProjectTaskIdentity", "ProjectTaskName");
                if (string.IsNullOrWhiteSpace(taskName) && taskUid is not null
                    && taskNameByUid.TryGetValue(taskUid, out var tn))
                {
                    taskName = tn;
                }

                var roleName = XmlNodeHelpers.NestedValue(card, "RoleIdentity", "RoleName");
                if (string.IsNullOrWhiteSpace(roleName) && roleUid is not null
                    && roleNameByUid.TryGetValue(roleUid, out var rol))
                {
                    roleName = rol;
                }

                var rejectedByDisplayName = XmlNodeHelpers.NestedValue(card, "RejectedByUser", "UserDisplayName");
                var rejectedByEmail = XmlNodeHelpers.NestedValue(card, "RejectedByUser", "EmailAddress");
                var rejectedByUserReferenceSystemId = XmlNodeHelpers.NestedValue(card, "RejectedByUser", "UserReferenceSystemId");
                var rejectedReason = XmlNodeHelpers.Value(card, "RejectedReason");
                var rejectedTimestamp = XmlNodeHelpers.Value(card, "RejectedTimestamp");
                var hasRejection = !string.IsNullOrWhiteSpace(rejectedByDisplayName)
                    || !string.IsNullOrWhiteSpace(rejectedByEmail)
                    || !string.IsNullOrWhiteSpace(rejectedByUserReferenceSystemId)
                    || !string.IsNullOrWhiteSpace(rejectedReason)
                    || !string.IsNullOrWhiteSpace(rejectedTimestamp);

                cards.Add(new Timecard
                {
                    ProjectCode = code,
                    ProjectName = name,
                    EngagementCode = engagementCode,
                    EngagementName = engagementName,
                    ClientName = clientName,
                    ClientNumber = clientNumber,
                    Billable = billable,
                    ProjectStageName = projectStageName,
                    WorkDate = workDate,
                    WorkMinutes = minutes,
                    WorkHours = ProjectorDateHelpers.MinutesAsHours(minutes),
                    Status = cardStatusHuman,
                    CardStatusCode = cardStatusRaw,
                    RateTypeName = rateTypeName,
                    TaskName = taskName,
                    RoleName = roleName,
                    Description = XmlNodeHelpers.Value(card, "Description"),
                    LocationName = XmlNodeHelpers.NestedValue(card, "LocationIdentity", "LocationName"),
                    RejectedByDisplayName = hasRejection ? rejectedByDisplayName : null,
                    RejectedByEmail = hasRejection ? rejectedByEmail : null,
                    RejectedByUserReferenceSystemId = hasRejection ? rejectedByUserReferenceSystemId : null,
                    RejectedReason = hasRejection ? rejectedReason : null,
                    RejectedTimestamp = hasRejection ? rejectedTimestamp : null
                });
            }
        }

        return cards;
    }

    public static string? GetLastTimecardDate(IEnumerable<Timecard> cards)
    {
        var dates = cards
            .Select(c => c.WorkDate)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
        return dates.Count == 0 ? null : dates[^1];
    }

    public static IReadOnlyList<TimeOffCard> ParseTimeOff(XDocument response)
    {
        var cards = new List<TimeOffCard>();
        foreach (var itm in XmlNodeHelpers.LocalNodes(response, "PwsTimeOffCardDetail"))
        {
            var workDate = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(itm, "WorkDate"));
            var minutes = XmlNodeHelpers.Int(itm, "WorkMinutes");
            var cardStatusRaw = XmlNodeHelpers.Value(itm, "CardStatus");
            var rejectedTimestamp = XmlNodeHelpers.Value(itm, "RejectedTimestamp");
            var rejectedByDisplayName = XmlNodeHelpers.NestedValue(itm, "RejectedByUser", "UserDisplayName");
            var rejectedByEmail = XmlNodeHelpers.NestedValue(itm, "RejectedByUser", "EmailAddress");
            var rejectedByUserReferenceSystemId = XmlNodeHelpers.NestedValue(itm, "RejectedByUser", "UserReferenceSystemId");
            var rejectedReason = XmlNodeHelpers.Value(itm, "RejectedReason");
            var hasRejection = !string.IsNullOrWhiteSpace(rejectedByDisplayName)
                || !string.IsNullOrWhiteSpace(rejectedByEmail)
                || !string.IsNullOrWhiteSpace(rejectedByUserReferenceSystemId)
                || !string.IsNullOrWhiteSpace(rejectedReason)
                || !string.IsNullOrWhiteSpace(rejectedTimestamp);

            cards.Add(new TimeOffCard
            {
                TimeOffReason = XmlNodeHelpers.NestedValue(itm, "TimeOffReasonIdentity", "TimeOffReasonName"),
                TimeOffDate = workDate,
                TimeOffMinutes = minutes,
                TimeOffHours = ProjectorDateHelpers.MinutesAsHours(minutes),
                Narrative = XmlNodeHelpers.Value(itm, "Description"),
                CardStatus = MapTimeCardStatus(cardStatusRaw),
                CardStatusCode = cardStatusRaw,
                RejectedByDisplayName = hasRejection ? rejectedByDisplayName : null,
                RejectedByEmail = hasRejection ? rejectedByEmail : null,
                RejectedByUserReferenceSystemId = hasRejection ? rejectedByUserReferenceSystemId : null,
                RejectedReason = hasRejection ? rejectedReason : null,
                RejectedTimestamp = hasRejection ? rejectedTimestamp : null
            });
        }

        return cards;
    }

    public static string? MapDailyWeeklyFlag(string? flag)
    {
        if (string.IsNullOrWhiteSpace(flag))
        {
            return null;
        }

        var trimmed = flag.Trim();
        return trimmed.ToUpperInvariant() switch
        {
            "D" or "DAILY" => "daily",
            "W" or "WEEKLY" => "weekly",
            _ => trimmed.ToLowerInvariant()
        };
    }

    public static ResourceSchedule ParseResourceSchedule(XDocument response)
    {
        var schedule = XmlNodeHelpers.LocalNode(response, "ResourceSchedule");
        if (schedule is null)
        {
            return new ResourceSchedule();
        }

        var dates = XmlNodeHelpers.LocalNodes(schedule, "PwsScheduleDate").Select(d =>
        {
            var nwm = XmlNodeHelpers.Int(d, "NormalWorkingMinutes");
            var ubm = XmlNodeHelpers.Int(d, "UtilizationBasisMinutes");
            return new ScheduleDate
            {
                Date = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(d, "Date")),
                NormalWorkingMinutes = nwm,
                NormalWorkingHours = ProjectorDateHelpers.MinutesAsHours(nwm),
                UtilizationBasisMinutes = ubm,
                UtilizationBasisHours = ProjectorDateHelpers.MinutesAsHours(ubm)
            };
        }).ToList();

        var holidays = XmlNodeHelpers.LocalNodes(schedule, "PwsScheduleHoliday").Select(h =>
        {
            var mins = XmlNodeHelpers.Int(h, "TimeOffMinutes");
            return new ScheduleHoliday
            {
                Date = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(h, "Date")),
                HolidayName = XmlNodeHelpers.Value(h, "HolidayName"),
                TimeOffMinutes = mins,
                TimeOffHours = ProjectorDateHelpers.MinutesAsHours(mins)
            };
        }).ToList();

        var timeOff = new List<ScheduleTimeOff>();
        foreach (var to in XmlNodeHelpers.LocalNodes(schedule, "PwsScheduleTimeOff"))
        {
            var reason = XmlNodeHelpers.Value(to, "TimeOffReasonName");
            foreach (var tod in XmlNodeHelpers.LocalNodes(to, "PwsScheduleTimeOffDate"))
            {
                var mins = XmlNodeHelpers.Int(tod, "TimeOffMinutes");
                timeOff.Add(new ScheduleTimeOff
                {
                    Date = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(tod, "Date")),
                    TimeOffReason = reason,
                    TimeOffMinutes = mins,
                    TimeOffHours = ProjectorDateHelpers.MinutesAsHours(mins)
                });
            }
        }

        var roles = new List<ScheduleRole>();
        var bookings = new List<ScheduleBooking>();
        foreach (var role in XmlNodeHelpers.LocalNodes(schedule, "PwsScheduleRole"))
        {
            var project = role.Elements().FirstOrDefault(e => e.Name.LocalName == "ProjectDescriptor")
                ?? XmlNodeHelpers.LocalNode(role, "ProjectDescriptor");
            var engagement = project?.Elements().FirstOrDefault(e => e.Name.LocalName == "EngagementDescriptor");

            var projectCode = XmlNodeHelpers.Value(project, "ProjectCode");
            var projectName = XmlNodeHelpers.Value(project, "ProjectName");
            var roleName = XmlNodeHelpers.Value(role, "RoleName");

            var projectManager = ParseUserIdentity(project?.Elements().FirstOrDefault(e => e.Name.LocalName == "ProjectManager"));
            var engagementManager = ParseUserIdentity(engagement?.Elements().FirstOrDefault(e => e.Name.LocalName == "EngagementManager"));
            var client = GetClientFields(engagement);
            var engagementType = engagement?.Elements().FirstOrDefault(e => e.Name.LocalName == "EngagementTypeDescriptor");

            roles.Add(new ScheduleRole
            {
                RoleName = roleName,
                RoleStartDate = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(role, "RoleStartDate")),
                RoleEndDate = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(role, "RoleEndDate")),
                ProjectCode = projectCode,
                ProjectName = projectName,
                ProjectStage = XmlNodeHelpers.NestedValue(project, "ProjectStageIdentity", "ProjectStageName"),
                ProjectOpenDate = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(project, "ProjectOpenDate")),
                ProjectCloseDate = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(project, "ProjectCloseDate")),
                ColorMapColor = XmlNodeHelpers.Value(project, "ColorMapColor"),
                EngagementCode = XmlNodeHelpers.Value(engagement, "EngagementCode"),
                EngagementName = XmlNodeHelpers.Value(engagement, "EngagementName"),
                ClientName = client.ClientName,
                ClientNumber = client.ClientNumber,
                ProjectManagerDisplayName = projectManager?.DisplayName,
                ProjectManagerEmail = projectManager?.EmailAddress,
                EngagementManagerDisplayName = engagementManager?.DisplayName,
                EngagementManagerEmail = engagementManager?.EmailAddress,
                ContractTypeName = XmlNodeHelpers.Value(engagementType, "EngagementTypeName"),
                ContractTypeShortName = XmlNodeHelpers.Value(engagementType, "EngagementTypeShortName"),
                Billable = XmlNodeHelpers.Bool(engagementType, "BillableFlag"),
                Productive = XmlNodeHelpers.Bool(engagementType, "BusyFlag")
            });

            var bookingsParent = role.Elements().FirstOrDefault(e => e.Name.LocalName == "Bookings");
            var bookingNodes = bookingsParent is not null
                ? XmlNodeHelpers.ChildLocalNodes(bookingsParent, "PwsScheduleBooking")
                : XmlNodeHelpers.LocalNodes(role, "PwsScheduleBooking");

            foreach (var b in bookingNodes)
            {
                var scheduled = XmlNodeHelpers.Int(b, "ScheduledMinutes");
                var dailyWeeklyFlag = XmlNodeHelpers.Value(b, "DailyWeeklyFlag");
                bookings.Add(new ScheduleBooking
                {
                    ProjectCode = projectCode,
                    ProjectName = projectName,
                    RoleName = roleName,
                    BookingStatus = XmlNodeHelpers.Value(b, "BookingStatus"),
                    DailyWeeklyFlag = dailyWeeklyFlag,
                    SchedulingMode = MapDailyWeeklyFlag(dailyWeeklyFlag),
                    Date = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(b, "Date")),
                    ScheduledMinutes = scheduled,
                    ScheduledHours = ProjectorDateHelpers.MinutesAsHours(scheduled),
                    Notes = GetScheduleBookingNotes(b)
                });
            }
        }

        return new ResourceSchedule
        {
            Dates = dates,
            Holidays = holidays,
            TimeOff = timeOff,
            Roles = roles,
            Bookings = bookings
        };
    }

    public static IReadOnlyList<HolidayEntry> ParseResourcePtoHolidays(XDocument response)
    {
        var parent = XmlNodeHelpers.LocalNode(response, "ScheduledHolidaysFuture");
        if (parent is null)
        {
            return [];
        }

        return XmlNodeHelpers.LocalNodes(parent, "PwsHolidayPto").Select(holiday =>
        {
            var mins = XmlNodeHelpers.Int(holiday, "TimeOffMinutes");
            return new HolidayEntry
            {
                Date = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(holiday, "Date")),
                HolidayName = XmlNodeHelpers.Value(holiday, "Name"),
                TimeOffMinutes = mins,
                TimeOffHours = ProjectorDateHelpers.MinutesAsHours(mins)
            };
        }).ToList();
    }

    public static IReadOnlyList<EngagementSummary> ParseEngagementList(XDocument response)
    {
        return XmlNodeHelpers.LocalNodes(response, "PwsEngagementSummary").Select(eng =>
        {
            var client = GetClientFields(eng);
            return new EngagementSummary
            {
                EngagementCode = XmlNodeHelpers.Value(eng, "EngagementCode"),
                EngagementName = XmlNodeHelpers.Value(eng, "EngagementName"),
                ClientName = client.ClientName,
                ClientNumber = client.ClientNumber
            };
        }).ToList();
    }

    public static EngagementDetail ParseEngagementElement(XElement element)
    {
        var detail = element.Elements().FirstOrDefault(e => e.Name.LocalName == "EngagementDetail")
            ?? XmlNodeHelpers.LocalNode(element, "EngagementDetail")
            ?? element;

        var managerNode = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Manager")
            ?? detail.Elements().FirstOrDefault(e => e.Name.LocalName == "ManagerIdentity");
        var manager = ParseUserIdentity(managerNode);
        var client = GetClientFields(element);

        var contracts = new List<EngagementContract>();
        var projects = new List<EngagementProject>();
        foreach (var cli in XmlNodeHelpers.LocalNodes(element, "PwsContractLineItemElement"))
        {
            var contractTypeName = XmlNodeHelpers.NestedValue(cli, "ContractLineItemDetail", "ContractTypeIdentity", "ContractTypeName");
            contracts.Add(new EngagementContract { ContractTypeName = contractTypeName });
            foreach (var proj in XmlNodeHelpers.LocalNodes(cli, "PwsProjectSummary"))
            {
                projects.Add(new EngagementProject
                {
                    ProjectCode = XmlNodeHelpers.Value(proj, "ProjectCode"),
                    ProjectName = XmlNodeHelpers.Value(proj, "ProjectName"),
                    ContractTypeName = contractTypeName
                });
            }
        }

        var workMinutes = XmlNodeHelpers.NullableInt(detail, "WorkMinutesTimeBudgetAmount");
        var chargeableMinutes = XmlNodeHelpers.NullableInt(detail, "ChargeableMinutesTimeBudgetAmount");
        var currencyCode = XmlNodeHelpers.NestedValue(detail, "CurrencyIdentity", "CurrencyCode");
        var timeBudgetMetric = XmlNodeHelpers.Value(detail, "TimeBudgetMetric");
        var contractRevenue = XmlNodeHelpers.NullableDouble(detail, "ContractRevenueTimeBudgetAmount");
        var billingAdjusted = XmlNodeHelpers.NullableDouble(detail, "BillingAdjustedRevenueTimeBudgetAmount");
        var rdc = XmlNodeHelpers.NullableDouble(detail, "ResourceDirectCostTimeBudgetAmount");
        var costBudgetMetric = XmlNodeHelpers.Value(detail, "CostBudgetMetric");
        var clientAmount = XmlNodeHelpers.NullableDouble(detail, "ClientAmountCostBudgetAmount");
        var disbursedAmount = XmlNodeHelpers.NullableDouble(detail, "DisbursedAmountCostBudgetAmount");
        var expenseAmount = XmlNodeHelpers.NullableDouble(detail, "ExpenseAmountCostBudgetAmount");

        var projectContractRevenue = XmlNodeHelpers.NullableDouble(element, "ProjectContractRevenueTimeBudgetAmount");
        var projectBillingAdjusted = XmlNodeHelpers.NullableDouble(element, "ProjectBillingAdjustedRevenueTimeBudgetAmount");
        var projectRdc = XmlNodeHelpers.NullableDouble(element, "ProjectResourceDirectCostTimeBudgetAmount");
        var projectWorkMinutes = XmlNodeHelpers.NullableDouble(element, "ProjectWorkMinutesTimeBudgetAmount");
        var projectChargeableMinutes = XmlNodeHelpers.NullableDouble(element, "ProjectChargeableMinutesTimeBudgetAmount");
        var projectClientAmount = XmlNodeHelpers.NullableDouble(element, "ProjectClientAmountCostBudgetAmount");
        var projectDisbursedAmount = XmlNodeHelpers.NullableDouble(element, "ProjectDisbursedAmountCostBudgetAmount");
        var projectExpenseAmount = XmlNodeHelpers.NullableDouble(element, "ProjectExpenseAmountCostBudgetAmount");

        var hasHours = workMinutes is not null || chargeableMinutes is not null
            || projectWorkMinutes is not null || projectChargeableMinutes is not null;
        var hasMoney = contractRevenue is not null
            || billingAdjusted is not null
            || rdc is not null
            || clientAmount is not null
            || disbursedAmount is not null
            || expenseAmount is not null
            || projectContractRevenue is not null
            || projectBillingAdjusted is not null
            || projectRdc is not null
            || projectClientAmount is not null
            || projectDisbursedAmount is not null
            || projectExpenseAmount is not null;
        string? budgetVisibility = null;
        if (hasMoney)
        {
            budgetVisibility = "hours_and_money";
        }
        else if (hasHours)
        {
            budgetVisibility = "hours_only";
        }

        return new EngagementDetail
        {
            EngagementCode = XmlNodeHelpers.Value(detail, "EngagementCode"),
            EngagementName = XmlNodeHelpers.Value(detail, "EngagementName"),
            Billable = XmlNodeHelpers.Bool(element, "BillableFlag"),
            Productive = XmlNodeHelpers.Bool(element, "ProductiveFlag"),
            CostCenterName = XmlNodeHelpers.NestedValue(detail, "CostCenterIdentity", "CostCenterName"),
            ClientName = client.ClientName,
            ClientNumber = client.ClientNumber,
            EngagementManagerDisplayName = manager?.DisplayName,
            EngagementManagerEmail = manager?.EmailAddress,
            Contracts = contracts,
            Projects = projects,
            WorkMinutesTimeBudgetAmount = workMinutes,
            WorkHoursTimeBudgetAmount = workMinutes is null
                ? null
                : Math.Round(workMinutes.Value / 60.0, 4),
            ChargeableMinutesTimeBudgetAmount = chargeableMinutes,
            ChargeableHoursTimeBudgetAmount = chargeableMinutes is null
                ? null
                : Math.Round(chargeableMinutes.Value / 60.0, 4),
            CurrencyCode = currencyCode,
            TimeBudgetMetric = timeBudgetMetric,
            TimeBudgetMetricLabel = ConvertTimeBudgetMetricLabel(timeBudgetMetric),
            ContractRevenueTimeBudgetAmount = contractRevenue,
            BillingAdjustedRevenueTimeBudgetAmount = billingAdjusted,
            ResourceDirectCostTimeBudgetAmount = rdc,
            CostBudgetMetric = costBudgetMetric,
            CostBudgetMetricLabel = ConvertCostBudgetMetricLabel(costBudgetMetric),
            ClientAmountCostBudgetAmount = clientAmount,
            DisbursedAmountCostBudgetAmount = disbursedAmount,
            ExpenseAmountCostBudgetAmount = expenseAmount,
            ProjectContractRevenueTimeBudgetAmount = projectContractRevenue,
            ProjectBillingAdjustedRevenueTimeBudgetAmount = projectBillingAdjusted,
            ProjectResourceDirectCostTimeBudgetAmount = projectRdc,
            ProjectWorkMinutesTimeBudgetAmount = projectWorkMinutes,
            ProjectChargeableMinutesTimeBudgetAmount = projectChargeableMinutes,
            ProjectClientAmountCostBudgetAmount = projectClientAmount,
            ProjectDisbursedAmountCostBudgetAmount = projectDisbursedAmount,
            ProjectExpenseAmountCostBudgetAmount = projectExpenseAmount,
            BudgetVisibility = budgetVisibility
        };
    }

    public static IReadOnlyList<EngagementDetail> ParseEngagements(XDocument response) =>
        XmlNodeHelpers.LocalNodes(response, "PwsEngagementElement")
            .Select(ParseEngagementElement)
            .ToList();

    public static EngagementDetail? ParseEngagement(XDocument response)
    {
        var items = ParseEngagements(response);
        return items.Count == 0 ? null : items[0];
    }

    public static IReadOnlyList<ProjectSummary> ParseProjects(XDocument response)
    {
        var items = new List<ProjectSummary>();
        foreach (var element in XmlNodeHelpers.LocalNodes(response, "PwsProjectElement"))
        {
            var detail = element.Elements().FirstOrDefault(e =>
                    e.Name.LocalName is "ProjectDetail" or "PwsProjectDetail")
                ?? XmlNodeHelpers.LocalNodes(element, "ProjectDetail").FirstOrDefault()
                ?? XmlNodeHelpers.LocalNodes(element, "PwsProjectDetail").FirstOrDefault()
                ?? element;

            var managerNode = detail.Elements().FirstOrDefault(e => e.Name.LocalName == "ManagerIdentity")
                ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == "ManagerIdentity");
            var manager = ParseUserIdentity(managerNode);

            items.Add(new ProjectSummary
            {
                ProjectCode = XmlNodeHelpers.Value(detail, "ProjectCode"),
                ProjectName = XmlNodeHelpers.Value(detail, "ProjectName"),
                Stage = XmlNodeHelpers.NestedValue(detail, "ProjectStageIdentity", "ProjectStageName"),
                BeginDate = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(detail, "OpenDate")),
                EndDate = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(detail, "CloseDate")),
                ProjectManagerDisplayName = manager?.DisplayName
            });
        }

        return items;
    }

    public static IReadOnlyList<ProjectRoleAssignment> ParseProjectRoles(XDocument response)
    {
        var rows = new List<ProjectRoleAssignment>();
        foreach (var el in XmlNodeHelpers.LocalNodes(response, "PwsProjectRoleElement"))
        {
            var detail = el.Elements().FirstOrDefault(e => e.Name.LocalName == "ProjectRoleDetail") ?? el;
            var projectCode = XmlNodeHelpers.Value(el, "ProjectCode");
            var roleName = XmlNodeHelpers.Value(detail, "RoleName");

            var resourceNode = detail.Elements().FirstOrDefault(e => e.Name.LocalName == "ResourceIdentity")
                ?? XmlNodeHelpers.LocalNode(detail, "ResourceIdentity")
                ?? XmlNodeHelpers.LocalNode(
                    detail.Elements().FirstOrDefault(e => e.Name.LocalName == "CandidateIdentities"),
                    "PwsResourceRef");

            var displayName = XmlNodeHelpers.Value(resourceNode, "ResourceDisplayName");
            var resourceId = XmlNodeHelpers.Value(resourceNode, "ResourceReferenceSystemId");
            var email = XmlNodeHelpers.Value(resourceNode, "EmailAddress");

            if (string.IsNullOrWhiteSpace(roleName) && string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            rows.Add(new ProjectRoleAssignment
            {
                ProjectCode = projectCode,
                RoleName = roleName,
                ResourceId = resourceId,
                DisplayName = displayName,
                Email = email
            });
        }

        return rows;
    }

    public static IReadOnlyList<ProjectBookingRow> ParseResourceSchedulingRoleData(
        XDocument response,
        string startDate,
        string endDate)
    {
        var startKey = ProjectorDateHelpers.ToShortDate(startDate);
        var endKey = ProjectorDateHelpers.ToShortDate(endDate);

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

        var rows = new List<ProjectBookingRow>();
        foreach (var role in XmlNodeHelpers.LocalNodes(response, "PwsProjectRoleSchedule"))
        {
            var projectCode = XmlNodeHelpers.Value(role, "ProjectCode");
            var projectName = XmlNodeHelpers.Value(role, "ProjectName");
            var roleName = XmlNodeHelpers.Value(role, "ProjectRoleName");

            var resourceNode = SelectSchedulingRoleCandidate(role);
            var displayName = XmlNodeHelpers.Value(resourceNode, "ResourceDisplayName");
            var resourceId = XmlNodeHelpers.Value(resourceNode, "ResourceReferenceSystemId");
            var resourceUid = XmlNodeHelpers.Value(resourceNode, "ResourceUid");
            var email = XmlNodeHelpers.Value(resourceNode, "EmailAddress");
            if (string.IsNullOrWhiteSpace(email)
                && !string.IsNullOrWhiteSpace(resourceUid)
                && emailByUid.TryGetValue(resourceUid, out var mapped))
            {
                email = mapped;
            }

            var bookedBuckets = role.Elements().FirstOrDefault(e => e.Name.LocalName == "BookedBuckets");
            foreach (var bucket in XmlNodeHelpers.LocalNodes(bookedBuckets, "PwsProjectRoleHoursBucket"))
            {
                var bucketStart = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(bucket, "BucketStartDate"));
                if (string.IsNullOrWhiteSpace(bucketStart))
                {
                    continue;
                }

                if (string.CompareOrdinal(bucketStart, startKey) < 0
                    || string.CompareOrdinal(bucketStart, endKey) > 0)
                {
                    continue;
                }

                var mode = XmlNodeHelpers.Value(bucket, "SchedulingMode");
                var minutes = 0;
                var weeklyRaw = XmlNodeHelpers.Value(bucket, "WeeklyMinutes");
                if (!string.IsNullOrWhiteSpace(weeklyRaw) && int.TryParse(weeklyRaw, out var weeklyMins))
                {
                    minutes = weeklyMins;
                }
                else
                {
                    var dailyParent = bucket.Elements().FirstOrDefault(e => e.Name.LocalName == "DailyMinutes");
                    if (dailyParent is not null)
                    {
                        foreach (var day in dailyParent.Elements())
                        {
                            if (int.TryParse(day.Value, out var dayMins))
                            {
                                minutes += dayMins;
                            }
                        }
                    }
                }

                if (minutes <= 0)
                {
                    continue;
                }

                rows.Add(new ProjectBookingRow
                {
                    ProjectCode = projectCode,
                    ProjectName = projectName,
                    RoleName = roleName,
                    ResourceId = resourceId,
                    DisplayName = displayName,
                    Email = email,
                    Date = bucketStart,
                    DailyWeeklyFlag = mode,
                    SchedulingMode = MapDailyWeeklyFlag(mode),
                    ScheduledMinutes = minutes,
                    ScheduledHours = ProjectorDateHelpers.MinutesAsHours(minutes)
                });
            }
        }

        return rows;
    }

    private static XElement? SelectSchedulingRoleCandidate(XElement roleSchedule)
    {
        var candidatesParent = roleSchedule.Elements().FirstOrDefault(e => e.Name.LocalName == "Candidates");
        var candidates = XmlNodeHelpers.ChildLocalNodes(candidatesParent, "PwsProjectRoleCandidate").ToList();
        foreach (var candidate in candidates)
        {
            if (XmlNodeHelpers.Bool(candidate, "SelectedCandidateFlag")
                || XmlNodeHelpers.Bool(candidate, "AssignedCandidateFlag"))
            {
                return candidate.Elements().FirstOrDefault(e => e.Name.LocalName == "ResourceIdentity")
                    ?? XmlNodeHelpers.LocalNode(candidate, "ResourceIdentity");
            }
        }

        if (candidates.Count > 0)
        {
            return candidates[0].Elements().FirstOrDefault(e => e.Name.LocalName == "ResourceIdentity")
                ?? XmlNodeHelpers.LocalNode(candidates[0], "ResourceIdentity");
        }

        return roleSchedule.Elements().FirstOrDefault(e => e.Name.LocalName == "ResourceIdentity")
            ?? XmlNodeHelpers.LocalNode(roleSchedule, "ResourceIdentity");
    }

    public static IReadOnlyList<EngagementSummary> MergeEngagementManagerFields(
        IEnumerable<EngagementSummary> engagements,
        IEnumerable<EngagementDetail>? details = null,
        IEnumerable<ProjectSummary>? projects = null)
    {
        var detailByCode = (details ?? [])
            .Where(d => !string.IsNullOrWhiteSpace(d.EngagementCode))
            .ToDictionary(d => d.EngagementCode!, StringComparer.OrdinalIgnoreCase);
        var projectByCode = (projects ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.ProjectCode))
            .ToDictionary(p => p.ProjectCode!, StringComparer.OrdinalIgnoreCase);

        return engagements.Select(engagement =>
        {
            detailByCode.TryGetValue(engagement.EngagementCode ?? "", out var detail);
            var emName = detail?.EngagementManagerDisplayName ?? engagement.EngagementManagerDisplayName;
            var emEmail = detail?.EngagementManagerEmail ?? engagement.EngagementManagerEmail;
            var clientName = !string.IsNullOrWhiteSpace(detail?.ClientName) ? detail.ClientName : engagement.ClientName;
            var clientNumber = !string.IsNullOrWhiteSpace(detail?.ClientNumber) ? detail.ClientNumber : engagement.ClientNumber;

            var sourceProjects = detail?.Projects.Count > 0
                ? detail.Projects
                : engagement.Projects;

            var projectsOut = sourceProjects.Select(proj =>
            {
                projectByCode.TryGetValue(proj.ProjectCode ?? "", out var pm);
                return new EngagementProject
                {
                    ProjectCode = proj.ProjectCode,
                    ProjectName = proj.ProjectName,
                    ContractTypeName = proj.ContractTypeName,
                    Stage = pm?.Stage ?? proj.Stage,
                    BeginDate = pm?.BeginDate ?? proj.BeginDate,
                    EndDate = pm?.EndDate ?? proj.EndDate,
                    ProjectManagerDisplayName = pm?.ProjectManagerDisplayName ?? proj.ProjectManagerDisplayName
                };
            }).ToList();

            return new EngagementSummary
            {
                EngagementCode = engagement.EngagementCode,
                EngagementName = engagement.EngagementName,
                ClientName = clientName,
                ClientNumber = clientNumber,
                EngagementManagerDisplayName = emName,
                EngagementManagerEmail = emEmail,
                Projects = projectsOut
            };
        }).ToList();
    }

    public static bool EngagementMatches(EngagementSummary engagement, string query)
    {
        var needle = query.Trim();
        if (string.IsNullOrWhiteSpace(needle))
        {
            return true;
        }

        var parts = new List<string?>
        {
            engagement.EngagementCode,
            engagement.EngagementName,
            engagement.ClientName,
            engagement.ClientNumber,
            engagement.EngagementManagerDisplayName,
            engagement.EngagementManagerEmail
        };
        foreach (var proj in engagement.Projects)
        {
            parts.Add(proj.ProjectCode);
            parts.Add(proj.ProjectName);
            parts.Add(proj.ContractTypeName);
            parts.Add(proj.ProjectManagerDisplayName);
        }

        var haystack = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public static bool EngagementManagerMatches(
        EngagementSummary engagement,
        string query,
        string role = "Any")
    {
        var needle = query.Trim();
        if (string.IsNullOrWhiteSpace(needle))
        {
            return true;
        }

        var fields = new List<string?>();
        if (role is "Any" or "Engagement")
        {
            fields.Add(engagement.EngagementManagerDisplayName);
            fields.Add(engagement.EngagementManagerEmail);
        }

        if (role is "Any" or "Project")
        {
            foreach (var project in engagement.Projects)
            {
                fields.Add(project.ProjectManagerDisplayName);
            }
        }

        var haystack = string.Join(' ', fields.Where(f => !string.IsNullOrWhiteSpace(f)));
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public static bool GetEngagementListIncludeClosed(
        bool includeClosed,
        bool includeClosedSpecified,
        string? managerQuery = null)
    {
        if (!includeClosedSpecified && !string.IsNullOrWhiteSpace(managerQuery))
        {
            return false;
        }

        return includeClosed;
    }

    public static IReadOnlyList<UtilizationYear> ParseUtilization(XDocument response)
    {
        var pkg = XmlNodeHelpers.LocalNode(response, "UtilizationSummaryPackage");
        if (pkg is null)
        {
            return [];
        }

        var years = new List<UtilizationYear>();
        foreach (var sectionName in new[] { "ThisYearUtilization", "LastYearUtilization" })
        {
            var section = XmlNodeHelpers.LocalNode(pkg, sectionName);
            if (section is null)
            {
                continue;
            }

            var totalNode = section.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "TotalUtilizationAndTarget")
                ?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "ActualUtilizationPercent");
            if (totalNode is not null
                && totalNode.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "nil")?.Value == "true")
            {
                continue;
            }

            years.Add(new UtilizationYear
            {
                Year = XmlNodeHelpers.Value(section, "Year"),
                BillableUtilization = GetUtilizationPercent(section, "BillableUtilizationAndTarget"),
                ChargeableUtilization = GetUtilizationPercent(section, "ChargeableUtilizationAndTarget"),
                ProductiveUtilization = GetUtilizationPercent(section, "ProductiveUtilizationAndTarget"),
                TotalUtilization = GetUtilizationPercent(section, "TotalUtilizationAndTarget")
            });
        }

        return years;
    }

    public static IReadOnlyList<ScheduledTimeOffExportRow> ParseScheduledTimeOffAsmx(XDocument response)
    {
        var items = new List<ScheduledTimeOffExportRow>();
        foreach (var itm in XmlNodeHelpers.LocalNodes(response, "ScheduledTimeoff"))
        {
            var dateRaw = XmlNodeHelpers.Value(itm, "Date") ?? "";
            var mins = XmlNodeHelpers.Int(itm, "TimeoffMinutes");
            items.Add(new ScheduledTimeOffExportRow
            {
                UserReferenceSystemId = XmlNodeHelpers.Value(itm, "EmployeeId"),
                DisplayName = XmlNodeHelpers.Value(itm, "DisplayName") ?? "",
                ScheduledTimeOffDate = dateRaw.Length >= 10 ? dateRaw[..10] : dateRaw,
                TimeOffMinutes = mins,
                TimeOffHours = (mins % 60) == 0 ? mins / 60 : null,
                Narrative = XmlNodeHelpers.Value(itm, "Narrative"),
                TimeOffReasonName = XmlNodeHelpers.Value(itm, "TimeoffReasonName"),
                ApprovalStatus = XmlNodeHelpers.Value(itm, "ApprovalStatus")
            });
        }

        return items;
    }

    public static IReadOnlyList<ExportedResourceRow> ParseExportedResourcesAsmx(XDocument response) =>
        XmlNodeHelpers.LocalNodes(response, "Resource").Select(itm => new ExportedResourceRow
        {
            ResourceReferenceSystemId = XmlNodeHelpers.Value(itm, "EmployeeId"),
            ResourceId = XmlNodeHelpers.Value(itm, "ResourceId"),
            DisplayName = XmlNodeHelpers.Value(itm, "DisplayName"),
            LocationName = XmlNodeHelpers.Value(itm, "LocationName"),
            BeginDate = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(itm, "BeginDate")),
            EndDate = XmlNodeHelpers.ShortDate(XmlNodeHelpers.Value(itm, "EndDate")),
            Inactive = XmlNodeHelpers.Bool(itm, "ResourceInactiveFlag")
        }).ToList();

    public static UserSummary? FindUserByEmail(IEnumerable<UserSummary> users, string email)
    {
        var target = email.Trim();
        return users.FirstOrDefault(u =>
            !string.IsNullOrWhiteSpace(u.EmailAddress)
            && string.Equals(u.EmailAddress.Trim(), target, StringComparison.OrdinalIgnoreCase));
    }

    public static ResourceSummary? FindResourceByEmail(IEnumerable<ResourceSummary> resources, string email)
    {
        var target = email.Trim();
        return resources.FirstOrDefault(r =>
            !string.IsNullOrWhiteSpace(r.EmailAddress)
            && string.Equals(r.EmailAddress.Trim(), target, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPermissionDeniedMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && System.Text.RegularExpressions.Regex.IsMatch(
            message,
            @"(?i)ViewPermissionDenied|AccessPermissionDenied|do not have permission");

    public static bool IsUserLookupSoftFailMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (IsPermissionDeniedMessage(message))
        {
            return true;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(
            message,
            @"(?i)EntityRequired|AtLeastOneItemNotFound|User is required|UserNotFound|ItemNotFound");
    }

    private static Dictionary<string, string> BuildLookup(
        XElement project,
        string nodeName,
        string uidName,
        string valueName)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in XmlNodeHelpers.LocalNodes(project, nodeName))
        {
            var uid = XmlNodeHelpers.Value(node, uidName);
            var value = XmlNodeHelpers.Value(node, valueName);
            if (!string.IsNullOrWhiteSpace(uid) && !string.IsNullOrWhiteSpace(value))
            {
                map[uid] = value;
            }
        }

        return map;
    }

    private static string? ConvertTimeBudgetMetricLabel(string? code) =>
        code?.Trim().ToUpperInvariant() switch
        {
            // PwsProjectTimeBaselineSummary PrimaryMetric / engagement TimeBudgetMetric
            "B" => "Billing Adjusted Revenue",
            "C" => "Contract Revenue",
            "R" => "Resource Direct Cost",
            "G" => "Chargeable Hours",
            "H" => "Hours",
            _ => string.IsNullOrWhiteSpace(code) ? null : code
        };

    private static string? ConvertCostBudgetMetricLabel(string? code) =>
        code?.Trim().ToUpperInvariant() switch
        {
            // PwsEngagementDetail CostBudgetMetric
            "C" => "Client Amount",
            "D" => "Disbursed Amount",
            "E" => "Expense Amount",
            _ => string.IsNullOrWhiteSpace(code) ? null : code
        };

    private static UserIdentity? ParseUserIdentity(XElement? node)
    {
        if (node is null)
        {
            return null;
        }

        var displayName = XmlNodeHelpers.Value(node, "UserDisplayName")
            ?? XmlNodeHelpers.Value(node, "ResourceDisplayName");
        var emailAddress = XmlNodeHelpers.Value(node, "EmailAddress");
        if (string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(emailAddress))
        {
            return null;
        }

        return new UserIdentity(displayName, emailAddress);
    }

    private static (string? ClientName, string? ClientNumber) GetClientFields(XElement? parent)
    {
        if (parent is null)
        {
            return (null, null);
        }

        string? clientName = null;
        string? clientNumber = null;
        var nodes = new List<XElement>();
        var client = parent.Elements().FirstOrDefault(e => e.Name.LocalName == "Client");
        if (client is not null)
        {
            nodes.Add(client);
        }

        var clientDescriptor = parent.Elements().FirstOrDefault(e => e.Name.LocalName == "ClientDescriptor");
        if (clientDescriptor is not null)
        {
            nodes.Add(clientDescriptor);
        }

        var identity = parent.Elements().FirstOrDefault(e => e.Name.LocalName == "ClientIdentity");
        if (identity is null)
        {
            var detail = parent.Elements().FirstOrDefault(e => e.Name.LocalName == "EngagementDetail");
            identity = detail?.Elements().FirstOrDefault(e => e.Name.LocalName == "ClientIdentity");
        }

        if (identity is not null)
        {
            nodes.Add(identity);
        }

        foreach (var node in nodes)
        {
            clientName ??= XmlNodeHelpers.Value(node, "ClientName");
            clientNumber ??= XmlNodeHelpers.Value(node, "ClientNumber");
        }

        return (clientName, clientNumber);
    }

    private static IReadOnlyList<string>? GetScheduleBookingNotes(XElement booking)
    {
        var notes = new List<string>();
        var notesNode = booking.Elements().FirstOrDefault(e => e.Name.LocalName == "Notes");
        if (notesNode is not null)
        {
            foreach (var child in notesNode.Elements())
            {
                notes.Add(child.Value);
            }

            if (notes.Count == 0 && !string.IsNullOrWhiteSpace(notesNode.Value))
            {
                notes.Add(notesNode.Value.Trim());
            }
        }

        while (notes.Count < 7)
        {
            notes.Add("");
        }

        if (notes.Count > 7)
        {
            notes = notes.Take(7).ToList();
        }

        return notes.Any(n => !string.IsNullOrWhiteSpace(n)) ? notes : null;
    }

    private static double? GetUtilizationPercent(XElement parent, string group)
    {
        var raw = XmlNodeHelpers.NestedValue(parent, group, "ActualUtilizationPercent");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return Math.Round(v, 2);
        }

        return null;
    }

    private sealed record UserIdentity(string? DisplayName, string? EmailAddress);
}
