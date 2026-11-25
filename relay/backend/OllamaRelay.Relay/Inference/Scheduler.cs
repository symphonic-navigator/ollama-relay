using OllamaRelay.Relay.Data;
using OllamaRelay.Relay.Nodes;

namespace OllamaRelay.Relay.Inference;

public sealed record Candidate(PublicationDoc Publication, NodeConnection Node);

public enum AcquireStatus
{
    Acquired,
    NoCandidate,
    QueueFull,
    Timeout,
}

public sealed class Lease : IDisposable
{
    private readonly Scheduler _scheduler;
    private int _disposed;

    internal Lease(Scheduler scheduler, Candidate candidate)
    {
        _scheduler = scheduler;
        Candidate = candidate;
    }

    public Candidate Candidate { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Candidate.Node.DecrementLeases();
            _scheduler.Signal();
        }
    }
}

public readonly record struct AcquireResult(AcquireStatus Status, Lease? Lease, TimeSpan Waited);

/// <summary>
/// Hands out node slots. Prefers a node that already has the model loaded, then the least busy one.
/// When every candidate is full, callers wait in a bounded queue until a slot frees up or they time out.
/// </summary>
public sealed class Scheduler
{
    private readonly Lock _lock = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _waiting;

    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>Wakes all waiters so they re-evaluate (slot freed, node joined, settings changed).</summary>
    public void Signal()
    {
        TaskCompletionSource previous;
        lock (_lock)
        {
            previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }

    public async Task<AcquireResult> AcquireAsync(Func<IReadOnlyList<Candidate>> candidates, int maxQueue,
        TimeSpan timeout, CancellationToken ct)
    {
        var started = TimeProvider.System.GetTimestamp();
        var queued = false;
        try
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    var list = candidates();
                    if (list.Count == 0)
                    {
                        return new AcquireResult(AcquireStatus.NoCandidate, null, Elapsed());
                    }

                    var pick = Pick(list);
                    if (pick is not null)
                    {
                        pick.Node.IncrementLeases();
                        return new AcquireResult(AcquireStatus.Acquired, new Lease(this, pick), Elapsed());
                    }

                    if (!queued)
                    {
                        if (_waiting >= maxQueue)
                        {
                            return new AcquireResult(AcquireStatus.QueueFull, null, Elapsed());
                        }

                        _waiting++;
                        queued = true;
                    }

                    changed = _changed.Task;
                }

                var remaining = timeout - Elapsed();
                if (remaining <= TimeSpan.Zero)
                {
                    return new AcquireResult(AcquireStatus.Timeout, null, Elapsed());
                }

                try
                {
                    await changed.WaitAsync(remaining, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return new AcquireResult(AcquireStatus.Timeout, null, Elapsed());
                }
            }
        }
        finally
        {
            if (queued)
            {
                lock (_lock)
                {
                    _waiting--;
                }
            }
        }

        TimeSpan Elapsed() => TimeProvider.System.GetElapsedTime(started);
    }

    internal static Candidate? Pick(IReadOnlyList<Candidate> list) =>
        list.Where(c => !c.Node.IsClosed && c.Node.ActiveLeases < c.Node.MaxConcurrency)
            .OrderByDescending(c => c.Node.HasLoaded(c.Publication.UpstreamModel))
            .ThenBy(c => c.Node.ActiveLeases)
            .ThenBy(c => c.Node.NodeName, StringComparer.Ordinal)
            .FirstOrDefault();
}
