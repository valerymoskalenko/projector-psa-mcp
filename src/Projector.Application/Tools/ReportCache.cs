using Microsoft.Extensions.Caching.Memory;
using Projector.Domain.Auth;
using Projector.Domain.Reports;

namespace Projector.Application.Tools;

/// <summary>A table kept for the next pages of one get_report result, with the time its data was produced.</summary>
public sealed record CachedReport(ReportTable Table, DateTimeOffset? DataAsOf);

/// <summary>
/// Ten-minute, per-user memory of get_report results that Projector cannot page: a saved report's output (Projector
/// returns it whole) and a cleaned Ginsu batch (the clean-up needs every row). The next page is cut from here
/// instead of a new download. Every key starts with the Projector user behind the connection: two users never share
/// an entry. Per process; after a restart or on another instance the data is read from Projector again.
/// </summary>
public sealed class ReportCache : IDisposable
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    /// <summary>A larger table is not kept: its first page is served and the answer says to narrow.</summary>
    public const long MaxTableBytes = 20L * 1024 * 1024;

    /// <summary>Cap for the whole process; the cache evicts when it is reached.</summary>
    public const long MaxTotalBytes = 200L * 1024 * 1024;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxTotalBytes });

    public bool TryGet<T>(ProjectorConnection connection, string key, out T? value)
        where T : class =>
        _cache.TryGetValue(Key(connection, key), out value) && value is not null;

    /// <summary>False when the value is larger than <see cref="MaxTableBytes"/> and was not kept.</summary>
    public bool Set<T>(ProjectorConnection connection, string key, T value, long sizeBytes)
        where T : class
    {
        if (sizeBytes > MaxTableBytes)
        {
            return false;
        }

        _cache.Set(Key(connection, key), value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Ttl,
            Size = Math.Max(1, sizeBytes)
        });
        return true;
    }

    /// <summary>Memory a table takes, roughly: two bytes per character plus the cost of each string and row.</summary>
    public static long EstimateBytes(ReportTable table)
    {
        long bytes = 0;
        foreach (var row in table.Rows)
        {
            bytes += 32 + 8L * row.Length;
            foreach (var cell in row)
            {
                if (cell is not null)
                {
                    bytes += 24 + 2L * cell.Length;
                }
            }
        }

        return bytes;
    }

    public void Dispose() => _cache.Dispose();

    private static string Key(ProjectorConnection connection, string key) => $"{connection.UserKey}::{key}";
}
