using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Projector.Domain.Auth;

namespace Projector.Application.Tools;

/// <summary>
/// Short-lived, per-user cache of Projector time-entry lookups, so filtering and paging in
/// list_time_projects / get_timecard_options and the checks in save_timecard don't call Projector again.
/// Every key starts with the Projector user behind the connection: two users never share an entry.
/// Only reads are cached; a save is never cached or replayed. Per process (per App Service instance).
/// </summary>
public sealed class TimeEntryCache : IDisposable
{
    /// <summary>Account-wide rules (time increment, UDFs); they change rarely.</summary>
    public static readonly TimeSpan RulesTtl = TimeSpan.FromMinutes(30);

    /// <summary>The user's projects/roles and one project's tasks for a date.</summary>
    public static readonly TimeSpan LookupTtl = TimeSpan.FromMinutes(10);

    /// <summary>The user's own cards for a date; short because the user may also edit in Projector.</summary>
    public static readonly TimeSpan DayCardsTtl = TimeSpan.FromMinutes(2);

    /// <summary>Entry count cap for the whole process (each entry has size 1).</summary>
    private const int MaxEntries = 5000;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxEntries });
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _userTokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Tenant + Entra object id + Projector account when known (hosted), else the connection id (local session).
    /// A connection belongs to exactly one user, so either identifies the user.
    /// </summary>
    public static string UserKey(ProjectorConnection connection) =>
        !string.IsNullOrWhiteSpace(connection.TenantId) && !string.IsNullOrWhiteSpace(connection.EntraObjectId)
            ? $"{connection.TenantId}|{connection.EntraObjectId}|{connection.ProjectorAccountCode}"
            : $"conn|{connection.ConnectionId}";

    public async Task<T> GetOrLoadAsync<T>(
        ProjectorConnection connection,
        string kind,
        string args,
        TimeSpan ttl,
        Func<Task<T>> load)
        where T : class
    {
        var key = Key(connection, kind, args);
        if (_cache.TryGetValue(key, out T? cached) && cached is not null)
        {
            Count("hit");
            return cached;
        }

        Count("miss");
        var value = await load();
        Set(connection, kind, args, value, ttl);
        return value;
    }

    public bool TryGet<T>(ProjectorConnection connection, string kind, string args, out T? value)
        where T : class =>
        _cache.TryGetValue(Key(connection, kind, args), out value) && value is not null;

    public void Set<T>(ProjectorConnection connection, string kind, string args, T value, TimeSpan ttl)
        where T : class
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = 1
        };
        options.AddExpirationToken(new CancellationChangeToken(UserToken(UserKey(connection)).Token));
        _cache.Set(Key(connection, kind, args), value, options);
    }

    public void Remove(ProjectorConnection connection, string kind, string args) =>
        _cache.Remove(Key(connection, kind, args));

    /// <summary>Drops every entry of this user (e.g. after the Projector session was refreshed).</summary>
    public void ClearUser(ProjectorConnection connection)
    {
        if (_userTokens.TryRemove(UserKey(connection), out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        _cache.Dispose();
        foreach (var cts in _userTokens.Values)
        {
            cts.Dispose();
        }
    }

    private CancellationTokenSource UserToken(string userKey) =>
        _userTokens.GetOrAdd(userKey, _ => new CancellationTokenSource());

    private static string Key(ProjectorConnection connection, string kind, string args) =>
        $"{UserKey(connection)}::{kind}::{args}";

    // Tag counts on the current request, so App Insights shows cache use per tool call.
    private static void Count(string outcome)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        var tag = $"projector.timeentry_cache.{outcome}";
        var current = activity.GetTagItem(tag) is int n ? n : 0;
        activity.SetTag(tag, current + 1);
    }
}
