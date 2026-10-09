using System.Collections.Concurrent;

namespace Projector.Application.Tools;

/// <summary>
/// One write at a time per project code. Projector locks the project during a booking write
/// (<c>EntityAlreadyLocked</c>), and agents send calls in parallel, sometimes the same call twice: a second save on
/// the same project waits here instead of failing or creating a second role. Per process (per App Service instance),
/// the same as <see cref="Projector.ApiClient.Xml.ProjectorCallLimiter"/>.
/// </summary>
public sealed class ProjectSaveGate
{
    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Waits up to <paramref name="maxWait"/> for the project; returns null when it is still busy. Dispose the result
    /// to release the project.
    /// </summary>
    public async Task<IDisposable?> TryEnterAsync(string projectCode, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        Gate gate;
        while (true)
        {
            gate = _gates.GetOrAdd(projectCode, _ => new Gate());
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

        bool entered;
        try
        {
            entered = await gate.Semaphore.WaitAsync(maxWait, cancellationToken);
        }
        catch
        {
            Leave(projectCode, gate);
            throw;
        }

        if (!entered)
        {
            Leave(projectCode, gate);
            return null;
        }

        return new Slot(this, projectCode, gate);
    }

    /// <summary>Projects with a gate (waiting or running saves); for tests.</summary>
    internal int ActiveProjects => _gates.Count;

    private void Leave(string projectCode, Gate gate)
    {
        lock (gate)
        {
            gate.Users--;
            if (gate.Users == 0)
            {
                gate.Removed = true;
                _gates.TryRemove(new KeyValuePair<string, Gate>(projectCode, gate));
            }
        }
    }

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }

        public bool Removed { get; set; }
    }

    private sealed class Slot(ProjectSaveGate owner, string projectCode, Gate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Semaphore.Release();
                owner.Leave(projectCode, gate);
            }
        }
    }
}
