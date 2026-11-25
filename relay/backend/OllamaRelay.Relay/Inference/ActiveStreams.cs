using System.Collections.Concurrent;
using MongoDB.Bson;

namespace OllamaRelay.Relay.Inference;

public sealed class ActiveStream
{
    public Guid Id { get; } = Guid.NewGuid();
    public required ObjectId UserId { get; init; }
    public required string Username { get; init; }
    public required ObjectId ApiKeyId { get; init; }
    public required string Alias { get; init; }
    public required string Endpoint { get; init; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public ObjectId? NodeId { get; set; }
    public string? NodeName { get; set; }
    public bool Queued { get; set; } = true;
    public CancellationTokenSource Cancellation { get; } = new();
    public string? CancelReason { get; private set; }

    public void Cancel(string reason)
    {
        CancelReason ??= reason;
        try
        {
            Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Finished meanwhile.
        }
    }
}

/// <summary>Registry of in-flight inference calls: per-user limits, live view and immediate revocation.</summary>
public sealed class ActiveStreams
{
    private readonly ConcurrentDictionary<Guid, ActiveStream> _streams = new();
    private readonly Lock _lock = new();

    public bool TryAdd(ActiveStream stream, int perUserMax)
    {
        lock (_lock)
        {
            if (_streams.Values.Count(s => s.UserId == stream.UserId) >= perUserMax)
            {
                return false;
            }

            _streams[stream.Id] = stream;
            return true;
        }
    }

    public void Remove(ActiveStream stream)
    {
        _streams.TryRemove(stream.Id, out _);
        stream.Cancellation.Dispose();
    }

    public IReadOnlyList<ActiveStream> Snapshot() => _streams.Values.ToList();

    public int CancelWhere(Func<ActiveStream, bool> predicate, string reason)
    {
        var count = 0;
        foreach (var s in _streams.Values.Where(predicate))
        {
            s.Cancel(reason);
            count++;
        }

        return count;
    }
}
