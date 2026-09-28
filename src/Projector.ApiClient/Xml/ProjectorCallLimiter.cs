using System.Collections.Concurrent;
using Projector.Domain.Exceptions;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Caps concurrent Projector calls per user. Projector refuses a user's 5th active request ("You currently have 5
/// active requests, exceeding the threshold of 4") and agents send tool calls in parallel, so extra calls wait here
/// instead of failing. One slot is left for the user's own Projector session. Per process (per App Service instance).
/// </summary>
public sealed class ProjectorCallLimiter
{
    public const int MaxConcurrentPerUser = 3;

    /// <summary>Error code for a call Projector refused because the user has too many active requests.</summary>
    public const string BusyErrorCode = "projector_busy";

    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);

    /// <summary>Waits for a free slot for this user; dispose the result to release it.</summary>
    public async Task<IDisposable> EnterAsync(string userKey, CancellationToken cancellationToken)
    {
        Gate gate;
        while (true)
        {
            gate = _gates.GetOrAdd(userKey, _ => new Gate());
            lock (gate)
            {
                if (gate.Removed)
                {
                    continue;
                }

                gate.Users++;
                break;
            }
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(userKey, gate);
            throw;
        }

        return new Slot(this, userKey, gate);
    }

    /// <summary>Users with a gate (waiting or running calls); for tests.</summary>
    internal int ActiveUsers => _gates.Count;

    /// <summary>
    /// True when Projector refused the call for too many active requests: HTTP 429, or a fault or message whose
    /// code or text says so.
    /// </summary>
    public static bool IsBusy(Exception ex) =>
        ex is ProjectorApiException api
        && (string.Equals(api.ErrorCode, "RateLimited", StringComparison.OrdinalIgnoreCase)
            || string.Equals(api.ErrorCode, "TooManyRequests", StringComparison.OrdinalIgnoreCase)
            || string.Equals(api.ErrorCode, BusyErrorCode, StringComparison.OrdinalIgnoreCase)
            || IsBusyText(api.Message));

    public static bool IsBusyText(string? text) =>
        text is not null
        && (text.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
            || text.Contains("active requests", StringComparison.OrdinalIgnoreCase));

    private void Leave(string userKey, Gate gate)
    {
        lock (gate)
        {
            gate.Users--;
            if (gate.Users == 0)
            {
                gate.Removed = true;
                _gates.TryRemove(new KeyValuePair<string, Gate>(userKey, gate));
            }
        }
    }

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(MaxConcurrentPerUser, MaxConcurrentPerUser);

        public int Users { get; set; }

        public bool Removed { get; set; }
    }

    private sealed class Slot(ProjectorCallLimiter owner, string userKey, Gate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Semaphore.Release();
                owner.Leave(userKey, gate);
            }
        }
    }
}
