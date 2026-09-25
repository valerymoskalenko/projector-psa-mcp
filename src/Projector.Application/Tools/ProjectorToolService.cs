using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using Projector.ApiClient.Xml;
using Projector.Application.Auth;
using Projector.Application.Resources;
using Projector.Contracts.Common;
using Projector.Contracts.Engagements;
using Projector.Contracts.Holidays;
using Projector.Contracts.Resources;
using Projector.Contracts.Timecards;
using Projector.Contracts.TimeOff;
using Projector.Domain.Auth;
using Projector.Domain.Availability;
using Projector.Domain.Common;
using Projector.Domain.Engagements;
using Projector.Domain.Exceptions;
using Projector.Domain.Pagination;
using Projector.Domain.Resources;
using Projector.Domain.Schedule;
using Projector.Domain.Timecards;
using Projector.Domain.TimeOff;

namespace Projector.Application.Tools;

/// <summary>
/// Shared use-cases for agent tools (OAuth session ticket only).
/// </summary>
public sealed class ProjectorToolService
{
    private readonly ProjectorConnectionService _connections;
    private readonly IProjectorSoapClient _soap;

    public ProjectorToolService(
        ProjectorConnectionService connections,
        IProjectorSoapClient soap)
    {
        _connections = connections;
        _soap = soap;
    }

    public async Task<object> ListTimecardsAsync(
        string connectionId,
        string resourceId,
        string startDate,
        string endDate,
        string? status,
        string? projectCode,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var connection = await RequireAsync(connectionId, ct);
        var listed = await WithRefreshAsync(connection, c =>
            _soap.ListTimecardsAsync(c, resourceId, startDate, endDate, projectCode, status, ct), ct);
        var start = Short(startDate);
        var end = Short(endDate);
        var filterBits = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectCode))
        {
            filterBits.Add($"project_code '{projectCode.Trim()}'");
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            filterBits.Add($"status '{status.Trim()}'");
        }

        var filterSuffix = filterBits.Count == 0 ? string.Empty : $"; filtered to {string.Join(" and ", filterBits)}";
        var searchedScope =
            $"work timecards for resource {resourceId} from {start} through {end}{filterSuffix}";
        var coverage = SearchCoverage.FromTruncation(
            listed.ServerTruncated,
            searchedScope,
            truncationReason:
            "Projector hit its row cap while listing timecards, so cards beyond that cap were never examined.",
            truncationSuggestion: "Narrow the date window or project_code filter so the full card set fits under the Projector row cap.",
            returned: listed.Timecards.Count);

        return AttachDuration(new
        {
            resource_id = resourceId,
            start_date = start,
            end_date = end,
            count = listed.Timecards.Count,
            timecards = listed.Timecards.Select(MapTimecard).ToList(),
            searchCoverage = SearchCoverageDto.From(coverage)
        }, sw);
    }

    public async Task<object> ListTimeOffAsync(
        string connectionId,
        string resourceId,
        string startDate,
        string endDate,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var connection = await RequireAsync(connectionId, ct);
        var listed = await WithRefreshAsync(connection, c =>
            _soap.ListTimeOffCardsAsync(c, resourceId, startDate, endDate, ct), ct);
        var start = Short(startDate);
        var end = Short(endDate);
        var searchedScope = $"time-off cards for resource {resourceId} from {start} through {end}";
        var coverage = SearchCoverage.FromTruncation(
            listed.ServerTruncated,
            searchedScope,
            truncationReason:
            "Projector hit its row cap while listing time-off cards, so cards beyond that cap were never examined.",
            truncationSuggestion: "Narrow the date window so the full time-off card set fits under the Projector row cap.",
            returned: listed.Cards.Count);

        return AttachDuration(new
        {
            resource_id = resourceId,
            start_date = start,
            end_date = end,
            count = listed.Cards.Count,
            time_off = listed.Cards.Select(MapTimeOff).ToList(),
            searchCoverage = SearchCoverageDto.From(coverage)
        }, sw);
    }

    public async Task<object> GetResourceScheduleAsync(
        string connectionId,
        string resourceId,
        string startDate,
        string endDate,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var connection = await RequireAsync(connectionId, ct);
        var schedule = await WithRefreshAsync(connection, c =>
            _soap.GetResourceScheduleAsync(c, resourceId, startDate, endDate, ct), ct);
        var availability = AvailabilityCalculatorShim.Summarize(schedule, resourceId, requiredMinutesPerWeek: 0);
        return AttachDuration(new
        {
            resource_id = resourceId,
            start_date = Short(startDate),
            end_date = Short(endDate),
            state = availability.State,
            capacityBasis = "utilization",
            days = availability.Days,
            weeks = availability.Weeks,
            schedule = MapSchedule(schedule)
        }, sw);
    }

    public async Task<object> CheckAvailabilityAsync(
        string connectionId,
        IReadOnlyList<string> people,
        string startDate,
        string endDate,
        double? requiredHoursPerWeek,
        double? requiredMinutesPerWeek,
        bool showAvailabilityDays,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (people.Count is < 1 or > 20)
        {
            throw new ArgumentException("people must contain 1–20 entries.");
        }

        if (requiredHoursPerWeek is not null && requiredMinutesPerWeek is not null)
        {
            throw new ArgumentException("Provide required_hours_per_week or required_minutes_per_week, not both.");
        }

        if (requiredHoursPerWeek is null && requiredMinutesPerWeek is null)
        {
            throw new ArgumentException("Provide required_hours_per_week or required_minutes_per_week.");
        }

        var requiredMinutes = requiredMinutesPerWeek
            ?? requiredHoursPerWeek!.Value * 60;

        var connection = await RequireAsync(connectionId, ct);
        var results = new List<object>();
        var errors = new List<object>();

        foreach (var person in people)
        {
            try
            {
                var resolved = await ResolvePersonAsync(connection, person, includeHistory: false, ct);
                var summary = await WithRefreshAsync(connection, c =>
                    _soap.CheckAvailabilityAsync(
                        c,
                        resolved.ResourceId,
                        startDate,
                        endDate,
                        resolved.Detail?.DisplayName ?? resolved.DisplayName,
                        resolved.Detail?.EmailAddress ?? resolved.Email,
                        requiredMinutes,
                        ct), ct);

                results.Add(new
                {
                    input = person,
                    user = (object?)null,
                    resource = resolved.Detail is null
                        ? (object)new
                        {
                            resourceReferenceSystemId = resolved.ResourceId,
                            displayName = resolved.DisplayName,
                            emailAddress = resolved.Email
                        }
                        : ResourceService.Map(resolved.Detail, includeHistory: false, includeUdfs: false),
                    availability = showAvailabilityDays ? summary : summary.WithoutDays()
                });
            }
            catch (ProjectorApiException ex)
            {
                errors.Add(new
                {
                    input = person,
                    code = ex.ErrorCode ?? "error",
                    message = ex.Message
                });
            }
            catch (Exception ex)
            {
                errors.Add(new { input = person, code = "error", message = ex.Message });
            }
        }

        return AttachDuration(new
        {
            start_date = Short(startDate),
            end_date = Short(endDate),
            required_minutes_per_week = requiredMinutes,
            people = results,
            errors,
            searchCoverage = SearchCoverageDto.From(BuildAvailabilityCoverage(
                Short(startDate), Short(endDate), results.Count, errors.Count, people.Count))
        }, sw);
    }

    public async Task<object> ListEngagementsAsync(
        string connectionId,
        string? query,
        string? managerQuery,
        string? managerRole,
        bool? includeClosed,
        int maxRows,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var connection = await RequireAsync(connectionId, ct);
        var includeClosedSpecified = includeClosed is not null;
        var closed = includeClosed ?? true;
        if (!includeClosedSpecified && !string.IsNullOrWhiteSpace(managerQuery))
        {
            closed = false;
        }

        var listTruncated = false;
        var listRowCap = maxRows;
        string? listServerQuery = null;

        async Task<IReadOnlyList<EngagementSummary>> FetchAsync(string? serverQuery, int rows)
        {
            var page = await WithRefreshAsync(connection, c =>
                _soap.ListEngagementsAsync(c, serverQuery, closed, rows, ct), ct);
            listRowCap = rows;
            listServerQuery = serverQuery;
            if (page.ServerTruncated)
            {
                listTruncated = true;
            }

            return page.Engagements;
        }

        IReadOnlyList<EngagementSummary> candidates;
        var effectiveManagerQuery = managerQuery;
        if (!string.IsNullOrWhiteSpace(managerQuery))
        {
            candidates = await FetchAsync(query, 200);
        }
        else if (!string.IsNullOrWhiteSpace(query))
        {
            candidates = await FetchAsync(query, maxRows);
            if (candidates.Count == 0)
            {
                effectiveManagerQuery = query;
                candidates = await FetchAsync(null, 200);
            }
        }
        else
        {
            candidates = await FetchAsync(null, maxRows);
        }

        var enriched = await EnrichEngagementsAsync(connection, candidates, detailsAlreadyLoaded: false, ct);

        if (!string.IsNullOrWhiteSpace(effectiveManagerQuery))
        {
            var role = string.IsNullOrWhiteSpace(managerRole) ? "Any" : managerRole.Trim();
            enriched = enriched
                .Where(e => MatchesEngagementManager(e, effectiveManagerQuery, role))
                .ToList();
        }

        var stageScope = closed ? "open and closed engagements" : "open engagements only";
        if (!closed && !includeClosedSpecified && !string.IsNullOrWhiteSpace(managerQuery))
        {
            stageScope += " (closed excluded by default for manager searches; pass include_closed=true to widen)";
        }

        var queryScope = string.IsNullOrWhiteSpace(listServerQuery)
            ? "no server query, so Projector returned an unfiltered list"
            : $"server query '{listServerQuery}'";
        var searchedScope = $"{stageScope}; asked Projector for up to {listRowCap} engagements using {queryScope}";
        if (!string.IsNullOrWhiteSpace(effectiveManagerQuery))
        {
            searchedScope += $", then matched manager '{effectiveManagerQuery}' locally";
        }

        var truncationReason =
            $"Projector hit its {listRowCap}-engagement row cap and returned a partial list, so engagements beyond that cap were never examined.";
        var truncationSuggestion = !string.IsNullOrWhiteSpace(effectiveManagerQuery)
            ? "Manager names are not server-searchable, so add query='<client, engagement, or project code>' to shrink the candidate set before local manager matching"
              + (closed ? "." : ", or set include_closed=true to also search closed history.")
            : "Add or narrow query to shrink the result set below the Projector row cap.";

        var pageResult = PageResultFactory.Create(
            enriched,
            maxRows: maxRows,
            serverTruncated: listTruncated,
            searchedScope: searchedScope,
            truncationReason: truncationReason,
            truncationSuggestion: truncationSuggestion);

        var dtos = pageResult.Items.Select(MapEngagementSummary).ToList();
        return AttachDuration(new
        {
            engagements = dtos,
            count = pageResult.Count,
            has_more = pageResult.HasMore,
            searchCoverage = SearchCoverageDto.From(pageResult.SearchCoverage),
            resource_links = dtos.Select(e => new
            {
                uri = $"projector://engagements/{e.EngagementCode}",
                name = e.EngagementName
            }).ToList()
        }, sw);
    }

    public async Task<object> GetEngagementAsync(string connectionId, string code, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var connection = await RequireAsync(connectionId, ct);
        var engagement = await WithRefreshAsync(connection, c =>
            _soap.GetEngagementAsync(c, code, ct), ct)
            ?? throw new ProjectorApiException($"Engagement '{code}' was not found.", "AtLeastOneItemNotFound");

        var enriched = (await EnrichEngagementDetailsAsync(connection, [engagement], ct))[0];

        return AttachDuration(new
        {
            uri = $"projector://engagements/{code}",
            engagement = MapEngagementDetail(enriched),
            resource_links = new[]
            {
                new { uri = $"projector://engagements/{code}/projects", name = "projects" }
            }
        }, sw);
    }

    public async Task<object> ListUpcomingPtoAsync(
        string connectionId,
        string resourceId,
        string? startDate,
        string? endDate,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var start = startDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = endDate ?? ThirdFridayAfter(DateTime.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        ProjectorDateHelpersAssert(start, end, ProjectorDateWindows.TimecardsDays);

        var connection = await RequireAsync(connectionId, ct);
        var scheduleChunks = await FetchScheduleChunksAsync(connection, resourceId, start, end, ct);
        var listed = await WithRefreshAsync(connection, c =>
            _soap.ListTimeOffCardsAsync(c, resourceId, start, end, ct), ct);
        var cards = listed.Cards;

        var merged = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var schedule in scheduleChunks)
        {
            foreach (var h in schedule.Holidays)
            {
                var key = $"{h.Date}|{h.HolidayName}";
                merged[key] = MapUpcomingPtoItem(h.Date, h.HolidayName, h.TimeOffMinutes, "holiday");
            }

            foreach (var t in schedule.TimeOff)
            {
                var key = $"{t.Date}|{t.TimeOffReason}";
                merged[key] = MapUpcomingPtoItem(t.Date, t.TimeOffReason, t.TimeOffMinutes, "schedule_timeoff");
            }
        }

        foreach (var c in cards)
        {
            var key = $"{c.TimeOffDate}|{c.TimeOffReason}";
            if (!merged.ContainsKey(key))
            {
                merged[key] = MapUpcomingPtoItem(c.TimeOffDate, c.TimeOffReason, c.TimeOffMinutes, "timecard");
            }
        }

        var pto = merged.Values.ToList();
        var searchedScope =
            $"upcoming PTO for resource {resourceId} from {Short(start)} through {Short(end)} " +
            "(schedule holidays/PTO plus time-off cards)";
        var coverage = SearchCoverage.FromTruncation(
            listed.ServerTruncated,
            searchedScope,
            truncationReason:
            "Projector hit its row cap while listing time-off cards used for upcoming PTO, so cards beyond that cap were never examined.",
            truncationSuggestion: "Narrow the date window so the full upcoming PTO set fits under the Projector row cap.",
            returned: pto.Count);

        return AttachDuration(new
        {
            resource_id = resourceId,
            start_date = Short(start),
            end_date = Short(end),
            count = pto.Count,
            pto,
            searchCoverage = SearchCoverageDto.From(coverage)
        }, sw);
    }

    public async Task<object> GetResourceOverviewAsync(
        string connectionId,
        string resourceId,
        string startDate,
        string endDate,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // Schedule is fetched in 56-day chunks (stricter than a single PS PwsGetResourceSchedule call).
        ProjectorDateHelpersAssert(startDate, endDate, ProjectorDateWindows.OverviewDays);
        var connection = await RequireAsync(connectionId, ct);

        var resource = await WithRefreshAsync(connection, c =>
            _soap.GetResourceAsync(c, resourceId, includeHistory: false, includeUdfs: true, ct), ct)
            ?? throw new ProjectorApiException($"Resource '{resourceId}' was not found.", "AtLeastOneItemNotFound");
        var timecardsListed = await WithRefreshAsync(connection, c =>
            _soap.ListTimecardsAsync(c, resourceId, startDate, endDate, null, null, ct), ct);
        var scheduleChunks = await FetchScheduleChunksAsync(connection, resourceId, startDate, endDate, ct);
        var timeOffListed = await WithRefreshAsync(connection, c =>
            _soap.ListTimeOffCardsAsync(c, resourceId, startDate, endDate, ct), ct);

        var schedule = MergeSchedules(scheduleChunks);
        var resourceDto = ResourceService.Map(resource, includeHistory: false, includeUdfs: true);

        return AttachDuration(new
        {
            resource = resourceDto,
            timecards = timecardsListed.Timecards.Select(MapTimecard).ToList(),
            schedule = MapSchedule(schedule),
            timeOff = timeOffListed.Cards.Select(MapTimeOff).ToList()
        }, sw);
    }

    public async Task<object> ListHolidaysAsync(
        string connectionId,
        string startDate,
        string? endDate,
        string? location,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var end = endDate ?? DateTime.ParseExact(Short(startDate), "yyyy-MM-dd", CultureInfo.InvariantCulture)
            .AddMonths(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var connection = await RequireAsync(connectionId, ct);
        var result = await WithRefreshAsync(connection, c =>
            _soap.GetCompanyHolidayCalendarsAsync(c, startDate, end, location, cancellationToken: ct), ct);

        var locationOut = string.IsNullOrWhiteSpace(location) ? null : result.Location;
        var dto = new ListHolidaysResponse(
            result.StartDate,
            result.EndDate,
            locationOut,
            result.Calendars.Select(c => new HolidayCalendarDto(
                c.Location,
                c.Holidays.Select(h => new HolidayEntryDto(
                    h.Date, h.HolidayName, h.TimeOffMinutes, h.TimeOffHours)).ToList())).ToList(),
            result.Count,
            result.ActiveResourceCount,
            SearchCoverageDto.From(result.SearchCoverage));

        return AttachDuration(dto, sw);
    }

    public async Task<object> ListProjectRolesAsync(
        string connectionId,
        IReadOnlyList<string> projectCodes,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var connection = await RequireAsync(connectionId, ct);
        var listed = await WithRefreshAsync(connection, c =>
            _soap.ListProjectRolesAsync(c, projectCodes, ct), ct);
        var codeCount = projectCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var searchedScope =
            $"assigned project roles for {codeCount} project code(s) via PwsGetProjectRoles Mode=A; not date-window booked hours";
        var coverage = SearchCoverage.FromTruncation(
            listed.ServerTruncated,
            searchedScope,
            truncationReason:
            "Projector hit its row cap while listing project roles, so role rows beyond that cap were never examined.",
            truncationSuggestion: "Pass fewer project_codes so the full role set fits under the Projector row cap.",
            returned: listed.Roles.Count);

        return AttachDuration(new
        {
            roles = listed.Roles.Select(r => new
            {
                projectCode = r.ProjectCode,
                roleName = r.RoleName,
                resourceId = r.ResourceId,
                displayName = r.DisplayName,
                email = r.Email
            }).ToList(),
            count = listed.Roles.Count,
            searchCoverage = SearchCoverageDto.From(coverage)
        }, sw);
    }

    public async Task<object> ListProjectBookingsAsync(
        string connectionId,
        IReadOnlyList<string> projectCodes,
        string startDate,
        string endDate,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var start = Short(startDate);
        var end = Short(endDate);
        var connection = await RequireAsync(connectionId, ct);
        var listed = await WithRefreshAsync(connection, c =>
            _soap.ListProjectBookingsAsync(c, projectCodes, start, end, ct), ct);
        var codeCount = projectCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var failed = listed.FailedProjectCodes;
        var searchedScope =
            $"nonzero booked-hour rows for {codeCount} project code(s) from {start} through {end}; zero-hour roles omitted";
        if (failed.Count > 0)
        {
            searchedScope += $"; {failed.Count} project code(s) failed and were skipped";
        }

        SearchCoverage coverage;
        if (listed.ServerTruncated || failed.Count > 0)
        {
            var reasonParts = new List<string>();
            if (listed.ServerTruncated)
            {
                reasonParts.Add("Projector hit a row cap on at least one project booking response");
            }

            if (failed.Count > 0)
            {
                reasonParts.Add($"SOAP failed for project code(s): {string.Join(", ", failed)}");
            }

            coverage = SearchCoverage.Partial(
                searchedScope,
                string.Join("; ", reasonParts) + ", so the booking list is incomplete.",
                failed.Count > 0
                    ? "Retry failed project_codes individually, or remove them from the request."
                    : "Pass fewer project_codes or a narrower date window so each project response fits under the Projector row cap.",
                returned: listed.Bookings.Count);
        }
        else
        {
            coverage = SearchCoverage.Full(searchedScope, returned: listed.Bookings.Count);
        }

        return AttachDuration(new
        {
            bookings = listed.Bookings.Select(b => new
            {
                projectCode = b.ProjectCode,
                projectName = b.ProjectName,
                roleName = b.RoleName,
                resourceId = b.ResourceId,
                displayName = b.DisplayName,
                email = b.Email,
                date = b.Date,
                dailyWeeklyFlag = b.DailyWeeklyFlag,
                schedulingMode = b.SchedulingMode,
                scheduledMinutes = b.ScheduledMinutes,
                scheduledHours = b.ScheduledHours
            }).ToList(),
            count = listed.Bookings.Count,
            startDate = start,
            endDate = end,
            failed_project_codes = failed,
            searchCoverage = SearchCoverageDto.From(coverage)
        }, sw);
    }

    private static SearchCoverage BuildAvailabilityCoverage(
        string start,
        string end,
        int succeeded,
        int failed,
        int requested)
    {
        var searchedScope =
            $"availability for {requested} requested people from {start} through {end} " +
            $"({succeeded} succeeded, {failed} failed)";
        if (failed > 0)
        {
            return SearchCoverage.Partial(
                searchedScope,
                $"{failed} of {requested} people failed to resolve or check, so availability is incomplete.",
                "Retry failed people individually (resolve with get_resource first) or remove them from the request.",
                returned: succeeded);
        }

        return SearchCoverage.Full(searchedScope, returned: succeeded);
    }

    private async Task<List<ResourceSchedule>> FetchScheduleChunksAsync(
        ProjectorConnection connection,
        string resourceId,
        string startDate,
        string endDate,
        CancellationToken ct)
    {
        var start = DateTime.ParseExact(Short(startDate), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateTime.ParseExact(Short(endDate), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var chunks = new List<ResourceSchedule>();
        var cursor = start;
        while (cursor <= end)
        {
            var chunkEnd = cursor.AddDays(ProjectorDateWindows.ScheduleDays - 1);
            if (chunkEnd > end)
            {
                chunkEnd = end;
            }

            var schedule = await WithRefreshAsync(connection, c =>
                _soap.GetResourceScheduleAsync(
                    c,
                    resourceId,
                    cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    chunkEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ct), ct);
            chunks.Add(schedule);
            cursor = chunkEnd.AddDays(1);
        }

        return chunks;
    }

    private async Task<(string ResourceId, string? DisplayName, string? Email, ResourceDetail? Detail)> ResolvePersonAsync(
        ProjectorConnection connection,
        string input,
        bool includeHistory,
        CancellationToken ct)
    {
        var id = input.Trim();
        if (ResourceEmailResolver.LooksLikeEmail(id))
        {
            var match = await WithRefreshAsync(connection, c =>
                ResourceEmailResolver.FindAsync(_soap, c, id, ct), ct);
            if (match?.ResourceReferenceSystemId is null)
            {
                throw new ProjectorApiException($"Resource not found for email '{id}'.", "AtLeastOneItemNotFound");
            }

            var detail = await WithRefreshAsync(connection, c =>
                _soap.GetResourceAsync(c, match.ResourceReferenceSystemId, includeHistory, includeUdfs: false, ct), ct);
            return (
                match.ResourceReferenceSystemId,
                detail?.DisplayName ?? match.DisplayName,
                detail?.EmailAddress ?? match.EmailAddress,
                detail);
        }

        if (!id.Contains(' ', StringComparison.Ordinal) && LooksLikeSystemId(id))
        {
            try
            {
                var byId = await WithRefreshAsync(connection, c =>
                    _soap.GetResourceAsync(c, id, includeHistory, includeUdfs: false, ct), ct);
                if (byId?.ResourceReferenceSystemId is not null)
                {
                    return (byId.ResourceReferenceSystemId, byId.DisplayName, byId.EmailAddress, byId);
                }
            }
            catch (ProjectorApiException ex) when (
                !string.Equals(ex.ErrorCode, "AccessPermissionDenied", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(ex.ErrorCode, "ViewPermissionDenied", StringComparison.OrdinalIgnoreCase))
            {
                // Fall through to name / list resolution.
            }
        }

        try
        {
            var byName = await WithRefreshAsync(connection, c =>
                _soap.GetResourceAsync(c, id, includeHistory, includeUdfs: false, ct), ct);
            if (byName?.ResourceReferenceSystemId is not null)
            {
                return (byName.ResourceReferenceSystemId, byName.DisplayName, byName.EmailAddress, byName);
            }
        }
        catch (ProjectorApiException ex) when (
            !string.Equals(ex.ErrorCode, "AccessPermissionDenied", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ex.ErrorCode, "ViewPermissionDenied", StringComparison.OrdinalIgnoreCase))
        {
            // Fall through to list search.
        }

        var byList = await WithRefreshAsync(connection, c =>
            _soap.ListResourcesAsync(c, id, includeInactive: true, maxRows: 100, ct), ct);
        ResourceSummary? summary = null;
        if (byList.Resources.Count == 1)
        {
            summary = byList.Resources[0];
        }
        else
        {
            var exact = byList.Resources
                .Where(r => string.Equals(r.DisplayName?.Trim(), id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exact.Count == 1)
            {
                summary = exact[0];
            }
            else if (exact.Count == 0)
            {
                var contains = byList.Resources
                    .Where(r => r.DisplayName is not null
                        && r.DisplayName.Contains(id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (contains.Count == 1)
                {
                    summary = contains[0];
                }
            }
        }

        if (summary?.ResourceReferenceSystemId is null)
        {
            throw new ProjectorApiException($"Resource not found for '{id}'.", "AtLeastOneItemNotFound");
        }

        var resolved = await WithRefreshAsync(connection, c =>
            _soap.GetResourceAsync(c, summary.ResourceReferenceSystemId, includeHistory, includeUdfs: false, ct), ct);
        return (
            summary.ResourceReferenceSystemId,
            resolved?.DisplayName ?? summary.DisplayName,
            resolved?.EmailAddress ?? summary.EmailAddress,
            resolved);
    }

    private async Task<(string ResourceId, string? DisplayName, string? Email, ResourceDetail? Detail)> ResolvePersonAsync(
        ProjectorConnection connection,
        string input,
        CancellationToken ct) =>
        await ResolvePersonAsync(connection, input, includeHistory: false, ct);

    private async Task<IReadOnlyList<EngagementSummary>> EnrichEngagementsAsync(
        ProjectorConnection connection,
        IReadOnlyList<EngagementSummary> engagements,
        bool detailsAlreadyLoaded,
        CancellationToken ct)
    {
        if (engagements.Count == 0)
        {
            return [];
        }

        IReadOnlyList<EngagementDetail> details;
        if (detailsAlreadyLoaded)
        {
            details = engagements
                .Select(e => new EngagementDetail
                {
                    EngagementCode = e.EngagementCode,
                    EngagementName = e.EngagementName,
                    ClientName = e.ClientName,
                    ClientNumber = e.ClientNumber,
                    EngagementManagerDisplayName = e.EngagementManagerDisplayName,
                    EngagementManagerEmail = e.EngagementManagerEmail,
                    Projects = e.Projects
                }).ToList();
        }
        else
        {
            var codes = engagements
                .Select(e => e.EngagementCode)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Cast<string>()
                .ToList();
            details = await WithRefreshAsync(connection, c =>
                _soap.GetEngagementsByCodeAsync(c, codes, ct), ct);
        }

        var projects = await LoadProjectsAsync(connection, details, ct);
        return ApiClient.Xml.ProjectorResponseParsers.MergeEngagementManagerFields(engagements, details, projects);
    }

    private async Task<IReadOnlyList<EngagementDetail>> EnrichEngagementDetailsAsync(
        ProjectorConnection connection,
        IReadOnlyList<EngagementDetail> details,
        CancellationToken ct)
    {
        if (details.Count == 0)
        {
            return [];
        }

        var projects = await LoadProjectsAsync(connection, details, ct);
        var merged = ApiClient.Xml.ProjectorResponseParsers.MergeEngagementManagerFields(
            details.Select(d => new EngagementSummary
            {
                EngagementCode = d.EngagementCode,
                EngagementName = d.EngagementName,
                ClientName = d.ClientName,
                ClientNumber = d.ClientNumber,
                EngagementManagerDisplayName = d.EngagementManagerDisplayName,
                EngagementManagerEmail = d.EngagementManagerEmail,
                Projects = d.Projects
            }).ToList(),
            details,
            projects);

        return merged.Select(s =>
        {
            var detail = details.FirstOrDefault(d =>
                string.Equals(d.EngagementCode, s.EngagementCode, StringComparison.OrdinalIgnoreCase));
            return new EngagementDetail
            {
                EngagementCode = s.EngagementCode,
                EngagementName = s.EngagementName,
                Billable = detail?.Billable ?? false,
                Productive = detail?.Productive ?? false,
                CostCenterName = detail?.CostCenterName,
                ClientName = s.ClientName,
                ClientNumber = s.ClientNumber,
                EngagementManagerDisplayName = s.EngagementManagerDisplayName,
                EngagementManagerEmail = s.EngagementManagerEmail,
                Contracts = detail?.Contracts ?? [],
                Projects = s.Projects,
                BudgetVisibility = detail?.BudgetVisibility,
                WorkMinutesTimeBudgetAmount = detail?.WorkMinutesTimeBudgetAmount,
                WorkHoursTimeBudgetAmount = detail?.WorkHoursTimeBudgetAmount,
                ChargeableMinutesTimeBudgetAmount = detail?.ChargeableMinutesTimeBudgetAmount,
                ChargeableHoursTimeBudgetAmount = detail?.ChargeableHoursTimeBudgetAmount,
                CurrencyCode = detail?.CurrencyCode,
                TimeBudgetMetric = detail?.TimeBudgetMetric,
                TimeBudgetMetricLabel = detail?.TimeBudgetMetricLabel,
                ContractRevenueTimeBudgetAmount = detail?.ContractRevenueTimeBudgetAmount,
                BillingAdjustedRevenueTimeBudgetAmount = detail?.BillingAdjustedRevenueTimeBudgetAmount,
                ResourceDirectCostTimeBudgetAmount = detail?.ResourceDirectCostTimeBudgetAmount,
                CostBudgetMetric = detail?.CostBudgetMetric,
                CostBudgetMetricLabel = detail?.CostBudgetMetricLabel,
                ClientAmountCostBudgetAmount = detail?.ClientAmountCostBudgetAmount,
                DisbursedAmountCostBudgetAmount = detail?.DisbursedAmountCostBudgetAmount,
                ExpenseAmountCostBudgetAmount = detail?.ExpenseAmountCostBudgetAmount,
                ProjectContractRevenueTimeBudgetAmount = detail?.ProjectContractRevenueTimeBudgetAmount,
                ProjectBillingAdjustedRevenueTimeBudgetAmount = detail?.ProjectBillingAdjustedRevenueTimeBudgetAmount,
                ProjectResourceDirectCostTimeBudgetAmount = detail?.ProjectResourceDirectCostTimeBudgetAmount,
                ProjectWorkMinutesTimeBudgetAmount = detail?.ProjectWorkMinutesTimeBudgetAmount,
                ProjectChargeableMinutesTimeBudgetAmount = detail?.ProjectChargeableMinutesTimeBudgetAmount,
                ProjectClientAmountCostBudgetAmount = detail?.ProjectClientAmountCostBudgetAmount,
                ProjectDisbursedAmountCostBudgetAmount = detail?.ProjectDisbursedAmountCostBudgetAmount,
                ProjectExpenseAmountCostBudgetAmount = detail?.ProjectExpenseAmountCostBudgetAmount
            };
        }).ToList();
    }

    private async Task<IReadOnlyList<ProjectSummary>> LoadProjectsAsync(
        ProjectorConnection connection,
        IReadOnlyList<EngagementDetail> details,
        CancellationToken ct)
    {
        var projectCodes = details
            .SelectMany(d => d.Projects)
            .Select(p => p.ProjectCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (projectCodes.Count == 0)
        {
            return [];
        }

        return await WithRefreshAsync(connection, c =>
            _soap.GetProjectsByCodeAsync(c, projectCodes, ct), ct);
    }

    private static bool MatchesEngagementManager(EngagementSummary engagement, string query, string role)
    {
        var needle = query.Trim();
        if (string.IsNullOrWhiteSpace(needle))
        {
            return true;
        }

        var fields = new List<string>();
        var normalized = role.Trim();
        if (normalized is "Any" or "Engagement" || string.IsNullOrWhiteSpace(normalized))
        {
            if (!string.IsNullOrWhiteSpace(engagement.EngagementManagerDisplayName))
            {
                fields.Add(engagement.EngagementManagerDisplayName);
            }

            if (!string.IsNullOrWhiteSpace(engagement.EngagementManagerEmail))
            {
                fields.Add(engagement.EngagementManagerEmail);
            }
        }

        if (normalized is "Any" or "Project" || string.IsNullOrWhiteSpace(normalized))
        {
            foreach (var project in engagement.Projects)
            {
                if (!string.IsNullOrWhiteSpace(project.ProjectManagerDisplayName))
                {
                    fields.Add(project.ProjectManagerDisplayName);
                }
            }
        }

        var haystack = string.Join(' ', fields);
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static EngagementSummaryDto MapEngagementSummary(EngagementSummary e) =>
        new(
            e.EngagementCode,
            e.EngagementName,
            e.ClientName,
            e.ClientNumber,
            e.EngagementManagerDisplayName,
            e.EngagementManagerEmail,
            e.Projects.Select(p => new EngagementProjectDto(
                p.ProjectCode, p.ProjectName, p.ContractTypeName, p.Stage, p.BeginDate, p.EndDate,
                p.ProjectManagerDisplayName)).ToList());

    private static EngagementDetailDto MapEngagementDetail(EngagementDetail e) =>
        new(
            e.EngagementCode,
            e.EngagementName,
            e.Billable,
            e.Productive,
            e.CostCenterName,
            e.ClientName,
            e.ClientNumber,
            e.EngagementManagerDisplayName,
            e.EngagementManagerEmail,
            e.Contracts.Select(c => new EngagementContractDto(c.ContractTypeName)).ToList(),
            e.Projects.Select(p => new EngagementProjectDto(
                p.ProjectCode, p.ProjectName, p.ContractTypeName, p.Stage, p.BeginDate, p.EndDate,
                p.ProjectManagerDisplayName)).ToList(),
            e.BudgetVisibility,
            e.WorkMinutesTimeBudgetAmount,
            e.WorkHoursTimeBudgetAmount,
            e.ChargeableMinutesTimeBudgetAmount,
            e.ChargeableHoursTimeBudgetAmount,
            e.CurrencyCode,
            e.TimeBudgetMetric,
            e.TimeBudgetMetricLabel,
            e.ContractRevenueTimeBudgetAmount,
            e.BillingAdjustedRevenueTimeBudgetAmount,
            e.ResourceDirectCostTimeBudgetAmount,
            e.CostBudgetMetric,
            e.CostBudgetMetricLabel,
            e.ClientAmountCostBudgetAmount,
            e.DisbursedAmountCostBudgetAmount,
            e.ExpenseAmountCostBudgetAmount,
            e.ProjectContractRevenueTimeBudgetAmount,
            e.ProjectBillingAdjustedRevenueTimeBudgetAmount,
            e.ProjectResourceDirectCostTimeBudgetAmount,
            e.ProjectWorkMinutesTimeBudgetAmount,
            e.ProjectChargeableMinutesTimeBudgetAmount,
            e.ProjectClientAmountCostBudgetAmount,
            e.ProjectDisbursedAmountCostBudgetAmount,
            e.ProjectExpenseAmountCostBudgetAmount);

    private Task<ProjectorConnection> RequireAsync(string connectionId, CancellationToken ct) =>
        // Login requests allowFullPermissions; tag-based legacy cache entries still work for live tools.
        _connections.RequireConnectionAsync(connectionId, requiredScope: null, ct);

    private async Task<T> WithRefreshAsync<T>(
        ProjectorConnection connection,
        Func<ProjectorConnection, Task<T>> action,
        CancellationToken ct)
    {
        try
        {
            return await action(connection);
        }
        catch (ProjectorApiException ex) when (IsAuthFailure(ex))
        {
            connection = await _connections.RefreshConnectionAsync(connection, ct);
            return await action(connection);
        }
    }

    private static bool IsAuthFailure(ProjectorApiException ex) =>
        string.Equals(ex.ErrorCode, "InvalidSessionTicket", StringComparison.OrdinalIgnoreCase)
        || (ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
            && ex.Message.Contains("invalid", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeSystemId(string id) =>
        id.Length <= 64 && !id.Contains(' ', StringComparison.Ordinal);

    private static string Short(string date) =>
        date.Length >= 10 ? date[..10] : date;

    private static DateTime ThirdFridayAfter(DateTime from)
    {
        var d = from.Date;
        var fridays = 0;
        while (fridays < 3)
        {
            d = d.AddDays(1);
            if (d.DayOfWeek == DayOfWeek.Friday)
            {
                fridays++;
            }
        }

        return d;
    }

    private static void ProjectorDateHelpersAssert(string start, string end, int maxDays)
    {
        var s = DateTime.ParseExact(Short(start), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var e = DateTime.ParseExact(Short(end), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (e < s)
        {
            throw new ArgumentException("End date must be on or after start date.");
        }

        if ((e - s).TotalDays + 1 > maxDays)
        {
            throw new ArgumentException($"Date window exceeds {maxDays} days.");
        }
    }

    private static ResourceSchedule MergeSchedules(IEnumerable<ResourceSchedule> chunks)
    {
        var list = chunks.ToList();
        if (list.Count == 0)
        {
            return new ResourceSchedule();
        }

        if (list.Count == 1)
        {
            return list[0];
        }

        return new ResourceSchedule
        {
            Dates = list.SelectMany(s => s.Dates).ToList(),
            Holidays = list.SelectMany(s => s.Holidays).ToList(),
            TimeOff = list.SelectMany(s => s.TimeOff).ToList(),
            Roles = list.SelectMany(s => s.Roles).GroupBy(r => r.ProjectCode + "|" + r.RoleName)
                .Select(g => g.First()).ToList(),
            Bookings = list.SelectMany(s => s.Bookings).ToList()
        };
    }

    private static object MapUpcomingPtoItem(string? date, string? reason, int minutes, string source) => new
    {
        date,
        reason,
        minutes,
        hours = ProjectorDateHelpers.MinutesAsHours(minutes),
        source
    };

    private static object MapSchedule(ResourceSchedule schedule) => schedule;

    private static TimecardDto MapTimecard(Timecard t) =>
        new(t.ProjectCode, t.ProjectName, t.EngagementCode, t.EngagementName, t.ClientName, t.ClientNumber,
            t.Billable, t.ProjectStageName, t.WorkDate, t.WorkMinutes, t.WorkHours, t.Status, t.CardStatusCode,
            t.RateTypeName, t.TaskName, t.RoleName, t.Description, t.LocationName,
            t.RejectedByDisplayName, t.RejectedByEmail, t.RejectedByUserReferenceSystemId,
            t.RejectedReason, t.RejectedTimestamp);

    private static TimeOffCardDto MapTimeOff(TimeOffCard t) =>
        new(t.TimeOffReason, t.TimeOffDate, t.TimeOffMinutes, t.TimeOffHours, t.Narrative,
            t.CardStatus, t.CardStatusCode, t.RejectedByDisplayName, t.RejectedByEmail,
            t.RejectedByUserReferenceSystemId, t.RejectedReason, t.RejectedTimestamp);

    private static object AttachDuration(object payload, Stopwatch sw)
    {
        sw.Stop();
        var dict = new Dictionary<string, object?>
        {
            ["executionDurationMs"] = sw.ElapsedMilliseconds,
            ["executionDurationSeconds"] = Math.Round(sw.Elapsed.TotalSeconds, 3)
        };

        foreach (var prop in payload.GetType().GetProperties())
        {
            var value = prop.GetValue(payload);
            var ignore = prop.GetCustomAttribute<JsonIgnoreAttribute>();
            if (value is null
                && ignore?.Condition == JsonIgnoreCondition.WhenWritingNull)
            {
                continue;
            }

            var name = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? ToCamel(prop.Name);
            dict[name] = value;
        }

        return dict;
    }

    private static string ToCamel(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>Local shim so Application does not take a hard Xml project dependency for schedule summaries.</summary>
internal static class AvailabilityCalculatorShim
{
    public static AvailabilitySummary Summarize(
        ResourceSchedule schedule,
        string resourceId,
        double requiredMinutesPerWeek)
    {
        // Application references ApiClient — call the real calculator.
        return ApiClient.Xml.AvailabilityCalculator.ToAvailabilitySummary(
            schedule, resourceId, null, null, requiredMinutesPerWeek);
    }
}
