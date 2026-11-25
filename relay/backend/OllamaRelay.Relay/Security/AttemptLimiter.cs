using System.Collections.Concurrent;

namespace OllamaRelay.Relay.Security;

/// <summary>
/// In-memory sliding counter of failed attempts per key (IP address, username).
/// Enough for a single relay instance.
/// </summary>
public sealed class AttemptLimiter(int maxFailures, TimeSpan window, TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool IsBlocked(string key)
    {
        if (!_failures.TryGetValue(key, out var queue))
        {
            return false;
        }

        lock (queue)
        {
            Trim(queue);
            return queue.Count >= maxFailures;
        }
    }

    public void RecordFailure(string key)
    {
        var queue = _failures.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            Trim(queue);
            queue.Enqueue(_time.GetUtcNow());
        }

        if (_failures.Count > 10_000)
        {
            Sweep();
        }
    }

    public void Reset(string key) => _failures.TryRemove(key, out _);

    private void Trim(Queue<DateTimeOffset> queue)
    {
        var cutoff = _time.GetUtcNow() - window;
        while (queue.Count > 0 && queue.Peek() < cutoff)
        {
            queue.Dequeue();
        }
    }

    private void Sweep()
    {
        foreach (var (key, queue) in _failures)
        {
            lock (queue)
            {
                Trim(queue);
                if (queue.Count == 0)
                {
                    _failures.TryRemove(key, out _);
                }
            }
        }
    }
}

/// <summary>Named limiters used by the relay.</summary>
public sealed class Limiters
{
    public AttemptLimiter LoginByIp { get; } = new(20, TimeSpan.FromMinutes(10));
    public AttemptLimiter LoginByUser { get; } = new(10, TimeSpan.FromMinutes(10));
    public AttemptLimiter RedeemByIp { get; } = new(10, TimeSpan.FromMinutes(10));
    public AttemptLimiter ApiKeyByIp { get; } = new(30, TimeSpan.FromMinutes(5));
    public AttemptLimiter NodeTokenByIp { get; } = new(20, TimeSpan.FromMinutes(5));
}
