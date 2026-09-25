using Projector.Domain.Auth;
using Projector.Domain.Resources;

namespace Projector.Application.Resources;

/// <summary>
/// Finds a resource by email. Projector's resource search matches display names, not emails,
/// so the search runs with the name parts of the email's local part ("jane.doe" -> "jane", "doe")
/// and keeps only an exact (case-insensitive) email match; as a last resort it scans one wide page.
/// </summary>
public static class ResourceEmailResolver
{
    private const int NameSearchRows = 200;
    private const int FallbackRows = 2000;

    public static bool LooksLikeEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains('@', StringComparison.Ordinal);

    public static async Task<ResourceSummary?> FindAsync(
        IProjectorResourceClient client,
        ProjectorConnection connection,
        string email,
        CancellationToken cancellationToken = default)
    {
        var target = email.Trim();
        foreach (var query in SearchTerms(target).Append(null))
        {
            var listed = await client.ListResourcesAsync(
                connection,
                query,
                includeInactive: true,
                maxRows: query is null ? FallbackRows : NameSearchRows,
                cancellationToken);
            var match = listed.Resources.FirstOrDefault(r =>
                string.Equals(r.EmailAddress?.Trim(), target, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>Name tokens from the local part, longest first, at least 2 characters.</summary>
    internal static IEnumerable<string> SearchTerms(string email)
    {
        var local = email.Split('@', 2)[0];
        return local
            .Split(['.', '_', '-', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2 && !t.All(char.IsDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(t => t.Length)
            .ToList();
    }
}
