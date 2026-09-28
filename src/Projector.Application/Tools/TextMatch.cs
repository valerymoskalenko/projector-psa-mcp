using System.Text.RegularExpressions;

namespace Projector.Application.Tools;

/// <summary>
/// Query matching for in-memory filters: whole words or word starts, not any substring, so "ACE" finds
/// "ACE Consulting" but not "Workplace". A dotted number (a WBS code such as 3.4.2) is one word, so "3.4"
/// finds 3.4 and 3.4.2 but not 4.3.
/// </summary>
public static partial class TextMatch
{
    /// <summary>0 = no match; 1 = every query word starts a word in the values; 2 = a value starts with the query; 3 = a value equals it.</summary>
    public static int Score(string query, params string?[] values)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            return 1;
        }

        var present = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        if (present.Any(v => string.Equals(v, q, StringComparison.OrdinalIgnoreCase)))
        {
            return 3;
        }

        if (present.Any(v => v.StartsWith(q, StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }

        var words = present.SelectMany(Words).ToList();
        var wanted = Words(q).ToList();
        return wanted.Count > 0 && wanted.All(w => words.Any(v => v.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
            ? 1
            : 0;
    }

    public static bool Matches(string query, params string?[] values) => Score(query, values) > 0;

    private static IEnumerable<string> Words(string text) =>
        WordPattern().Matches(text).Select(m => m.Value);

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:\.\p{N}+)*")]
    private static partial Regex WordPattern();
}
