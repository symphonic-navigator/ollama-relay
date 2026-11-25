using System.Collections.Concurrent;
using MongoDB.Bson;
using OllamaRelay.Protocol;

namespace OllamaRelay.Relay.Nodes;

/// <summary>A live, authenticated node WebSocket.</summary>
public sealed class NodeConnection
{
    private readonly ConcurrentDictionary<uint, RelayStream> _streams = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly int _perStreamBuffer;
    private int _nextStreamId;
    private int _activeLeases;

    public NodeConnection(ObjectId nodeId, string nodeName, int maxConcurrency, FrameSocket socket, string agentVersion,
        int perStreamBuffer)
    {
        NodeId = nodeId;
        NodeName = nodeName;
        MaxConcurrency = maxConcurrency;
        Socket = socket;
        AgentVersion = agentVersion;
        _perStreamBuffer = perStreamBuffer;
        ConnectedAt = DateTimeOffset.UtcNow;
        LastHeartbeatAt = ConnectedAt;
    }

    public ObjectId NodeId { get; }
    public string NodeName { get; set; }
    public int MaxConcurrency { get; set; }
    public FrameSocket Socket { get; }
    public string AgentVersion { get; }
    public DateTimeOffset ConnectedAt { get; }
    public DateTimeOffset LastHeartbeatAt { get; private set; }
    public HeartbeatMessage? LastHeartbeat { get; private set; }
    public CancellationToken Closed => _closed.Token;
    public bool IsClosed => _closed.IsCancellationRequested;

    /// <summary>Slots in use, as counted by the scheduler.</summary>
    public int ActiveLeases => Volatile.Read(ref _activeLeases);

    internal int IncrementLeases() => Interlocked.Increment(ref _activeLeases);

    internal int DecrementLeases() => Interlocked.Decrement(ref _activeLeases);

    public bool HasLoaded(string upstreamModel) =>
        LastHeartbeat?.Loaded.Any(m => string.Equals(m.Name, upstreamModel, StringComparison.Ordinal)) == true;

    public RelayStream OpenStream()
    {
        var id = (uint)Interlocked.Increment(ref _nextStreamId);
        var stream = new RelayStream(id, this, _perStreamBuffer);
        _streams[id] = stream;
        if (IsClosed)
        {
            _streams.TryRemove(id, out _);
            stream.Fail();
        }

        return stream;
    }

    internal void Forget(uint id) => _streams.TryRemove(id, out _);

    internal void OnHeartbeat(HeartbeatMessage heartbeat)
    {
        LastHeartbeat = heartbeat;
        LastHeartbeatAt = DateTimeOffset.UtcNow;
    }

    internal void Dispatch(Frame frame)
    {
        if (_streams.TryGetValue(frame.StreamId, out var stream))
        {
            stream.Deliver(frame);
        }
    }

    /// <summary>Fails every open stream. Called when the socket goes away.</summary>
    internal void MarkClosed()
    {
        _closed.Cancel();
        foreach (var stream in _streams.Values)
        {
            stream.MarkClosed();
            stream.Fail();
        }

        _streams.Clear();
    }
}
