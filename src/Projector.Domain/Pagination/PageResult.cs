namespace Projector.Domain.Pagination;

/// <summary>
/// Search completeness metadata. When Projector caps its own response,
/// coverage is incomplete and the returned count is a lower bound.
/// </summary>
public sealed class SearchCoverage
{
    public const string StatusFull = "full";

    public const string StatusPartial = "partial";

    public bool Complete { get; init; } = true;

    public bool CountIsLowerBound { get; init; }

    /// <summary>Agent-facing completeness: "full" or "partial".</summary>
    public string Status => Complete ? StatusFull : StatusPartial;

    public string? SearchedScope { get; init; }

    public string? Reason { get; init; }

    public string? Suggestion { get; init; }

    public int? LocationProbes { get; init; }

    public int? HolidayCalls { get; init; }

    public int? Returned { get; init; }

    public int? Limit { get; init; }

    public static SearchCoverage Full(
        string searchedScope,
        int? returned = null,
        int? limit = null,
        int? locationProbes = null,
        int? holidayCalls = null) =>
        new()
        {
            Complete = true,
            CountIsLowerBound = false,
            SearchedScope = NullIfBlank(searchedScope),
            Returned = returned,
            Limit = limit,
            LocationProbes = locationProbes,
            HolidayCalls = holidayCalls
        };

    public static SearchCoverage Partial(
        string searchedScope,
        string reason,
        string suggestion,
        int? returned = null,
        int? limit = null,
        int? locationProbes = null,
        int? holidayCalls = null) =>
        new()
        {
            Complete = false,
            CountIsLowerBound = true,
            SearchedScope = NullIfBlank(searchedScope),
            Reason = NullIfBlank(reason) ?? PageResultFactory.DefaultTruncationReason,
            Suggestion = NullIfBlank(suggestion) ?? PageResultFactory.DefaultTruncationSuggestion,
            Returned = returned,
            Limit = limit,
            LocationProbes = locationProbes,
            HolidayCalls = holidayCalls
        };

    public static SearchCoverage FromTruncation(
        bool serverTruncated,
        string searchedScope,
        string? truncationReason = null,
        string? truncationSuggestion = null,
        int? returned = null,
        int? limit = null) =>
        serverTruncated
            ? Partial(
                searchedScope,
                truncationReason ?? PageResultFactory.DefaultTruncationReason,
                truncationSuggestion ?? PageResultFactory.DefaultTruncationSuggestion,
                returned,
                limit)
            : Full(searchedScope, returned, limit);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Client-side page over an already-fetched item set, plus searchCoverage.
/// <see cref="HasMore"/> describes only this result set (call again with next offset).
/// </summary>
public sealed class PageResult<T>
{
    public int Count { get; init; }

    public int Offset { get; init; }

    public bool HasMore { get; init; }

    public int? NextOffset { get; init; }

    public required SearchCoverage SearchCoverage { get; init; }

    public required IReadOnlyList<T> Items { get; init; }
}

public static class PageResultFactory
{
    public const string DefaultTruncationReason =
        "Projector returned its maximum row count and reported RowCountExceeded, so records beyond that cap were never examined.";

    public const string DefaultTruncationSuggestion =
        "Narrow the search with a more specific query so the full match set fits under the Projector row cap.";

    public static PageResult<T> Create<T>(
        IReadOnlyList<T> items,
        int maxRows = 50,
        int offset = 0,
        bool serverTruncated = false,
        string? searchedScope = null,
        string? truncationReason = null,
        string? truncationSuggestion = null)
    {
        var slice = items.Skip(offset).Take(maxRows).ToList();
        var localHasMore = (offset + slice.Count) < items.Count;

        var coverage = SearchCoverage.FromTruncation(
            serverTruncated,
            searchedScope ?? string.Empty,
            truncationReason,
            truncationSuggestion,
            returned: slice.Count,
            limit: maxRows);

        return new PageResult<T>
        {
            Count = slice.Count,
            Offset = offset,
            HasMore = localHasMore,
            NextOffset = localHasMore ? offset + slice.Count : null,
            SearchCoverage = coverage,
            Items = slice
        };
    }
}
