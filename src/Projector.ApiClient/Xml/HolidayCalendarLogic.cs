using Projector.Domain.Holidays;
using Projector.Domain.Pagination;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Pure holiday-calendar grouping logic (ExportResources → location calendars).
/// Network fan-out lives in <see cref="ProjectorHolidayCalendarClient"/>.
/// </summary>
public static class HolidayCalendarLogic
{
    public static IReadOnlyList<HolidayCalendarResource> GroupResourcesByHolidayCalendar(
        IEnumerable<ExportedResourceRow> exportedResources)
    {
        var active = exportedResources
            .Where(r => !string.IsNullOrWhiteSpace(r.ResourceReferenceSystemId) && !r.Inactive)
            .ToList();

        return active
            .GroupBy(r => r.ResourceReferenceSystemId!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var history = group.OrderBy(r => r.BeginDate, StringComparer.Ordinal).ToList();
                var latest = history[^1];
                var locations = history
                    .Select(h => h.LocationName)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                return new HolidayCalendarResource
                {
                    ResourceReferenceSystemId = group.Key,
                    DisplayName = latest.DisplayName,
                    LocationName = latest.LocationName,
                    LocationChanged = locations.Count > 1
                };
            })
            .OrderBy(r => r.ResourceReferenceSystemId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Assembles company holiday calendars from already-fetched location→holiday maps (pure).
    /// </summary>
    public static CompanyHolidayCalendarsResult BuildCompanyHolidayCalendarsResult(
        string startDate,
        string endDate,
        string? locationFilter,
        IReadOnlyList<HolidayCalendarResource> allResources,
        IReadOnlyList<HolidayCalendarResource> matchedResources,
        IReadOnlyDictionary<string, IReadOnlyList<HolidayEntry>> holidaysByLocation,
        bool resourceListTruncated,
        int maxRows,
        int holidayCalls)
    {
        var start = ProjectorDateHelpers.ToShortDate(startDate);
        var end = ProjectorDateHelpers.ToShortDate(endDate);
        var filter = string.IsNullOrWhiteSpace(locationFilter) ? null : locationFilter.Trim();

        var knownLocations = allResources
            .Select(r => r.LocationName)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();

        var searchedScope = $"location holiday calendars from {start} through {end}, inclusive";
        if (filter is not null)
        {
            searchedScope = $"{searchedScope}; location filter '{filter}'";
        }

        if (matchedResources.Count == 0)
        {
            string? suggestion = null;
            if (filter is not null)
            {
                suggestion = knownLocations.Count == 0
                    ? $"No active resource locations were found for {start} through {end}."
                    : $"No location matched '{filter}'. Known locations: {string.Join(", ", knownLocations)}.";
            }

            return new CompanyHolidayCalendarsResult
            {
                StartDate = start,
                EndDate = end,
                Location = filter,
                Calendars = [],
                Count = 0,
                ActiveResourceCount = allResources.Count,
                SearchCoverage = new SearchCoverage
                {
                    Complete = !resourceListTruncated,
                    CountIsLowerBound = resourceListTruncated,
                    SearchedScope = searchedScope,
                    LocationProbes = 0,
                    HolidayCalls = 0,
                    Returned = allResources.Count,
                    Limit = maxRows,
                    Suggestion = suggestion,
                    Reason = resourceListTruncated
                        ? "Projector hit its resource-export row cap while building holiday calendars, so some locations may be missing."
                        : null
                }
            };
        }

        var calendars = holidaysByLocation
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new HolidayCalendar
            {
                Location = kv.Key,
                Holidays = kv.Value
                    .Where(h => h.Date is not null && string.CompareOrdinal(h.Date, start) >= 0
                        && string.CompareOrdinal(h.Date, end) <= 0)
                    .OrderBy(h => h.Date, StringComparer.Ordinal)
                    .ThenBy(h => h.HolidayName, StringComparer.Ordinal)
                    .ToList()
            })
            .ToList();

        return new CompanyHolidayCalendarsResult
        {
            StartDate = start,
            EndDate = end,
            Location = filter,
            Calendars = calendars,
            Count = calendars.Count,
            ActiveResourceCount = allResources.Count,
            SearchCoverage = new SearchCoverage
            {
                Complete = !resourceListTruncated,
                CountIsLowerBound = resourceListTruncated,
                SearchedScope = searchedScope,
                LocationProbes = holidaysByLocation.Count,
                HolidayCalls = holidayCalls,
                Returned = allResources.Count,
                Limit = maxRows,
                Reason = resourceListTruncated
                    ? "Projector hit its resource-export row cap while building holiday calendars, so some locations may be missing."
                    : null,
                Suggestion = resourceListTruncated
                    ? "Narrow the location filter or date range so the active resource export fits under the Projector row cap."
                    : null
            }
        };
    }

    /// <summary>
    /// One probe resource id per location (skip movers first; fall back if all are movers).
    /// </summary>
    public static IReadOnlyDictionary<string, string> SelectLocationProbeTargets(
        IEnumerable<HolidayCalendarResource> matchedResources)
    {
        var calendarTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in matchedResources)
        {
            if (resource.LocationChanged)
            {
                continue;
            }

            var locationKey = resource.LocationName ?? "";
            if (!calendarTargets.ContainsKey(locationKey)
                && !string.IsNullOrWhiteSpace(resource.ResourceReferenceSystemId))
            {
                calendarTargets[locationKey] = resource.ResourceReferenceSystemId!;
            }
        }

        foreach (var resource in matchedResources)
        {
            var locationKey = resource.LocationName ?? "";
            if (!calendarTargets.ContainsKey(locationKey)
                && !string.IsNullOrWhiteSpace(resource.ResourceReferenceSystemId))
            {
                calendarTargets[locationKey] = resource.ResourceReferenceSystemId!;
            }
        }

        return calendarTargets;
    }

    public static IReadOnlyList<HolidayCalendarResource> FilterByLocation(
        IEnumerable<HolidayCalendarResource> resources,
        string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return resources.ToList();
        }

        var filter = location.Trim();
        return resources
            .Where(r => !string.IsNullOrWhiteSpace(r.LocationName)
                && r.LocationName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
