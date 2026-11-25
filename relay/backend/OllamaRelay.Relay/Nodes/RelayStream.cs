using System.Threading.Channels;
using OllamaRelay.Protocol;

namespace OllamaRelay.Relay.Nodes;

public enum StreamEventKind
{
    Head,
    Body,
    End,
    Error,
}

public readonly record struct StreamEvent(StreamEventKind Kind, int Status = 0, string? ContentType = null,
    ReadOnlyMemory<byte> Data = default, string? Code = null);

/// <summary>
/// Relay side of one multiplexed stream. Frames from the node land in a per-stream buffer.
/// If the client reads too slowly and the buffer exceeds its byte limit, only this stream is cancelled;
/// the node connection keeps serving everyone else.
/// </summary>
public sealed class RelayStream
{
    private readonly Channel<StreamEvent> _events = Channel.CreateUnbounded<StreamEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly NodeConnection _connection;
    private readonly int _maxBuffered;
    private long _buffered;
    private int _closed;

    internal RelayStream(uint id, NodeConnection connection, int maxBuffered)
    {
        Id = id;
        _connection = connection;
        _maxBuffered = maxBuffered;
    }

    public uint Id { get; }

    public NodeConnection Connection => _connection;

    public bool Overflowed { get; private set; }

    public async Task SendRequestAsync(RequestHeadMessage head, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var socket = _connection.Socket;
        await socket.SendAsync(ProtocolJson.ToFrame(FrameType.RequestHead, Id, head), ct).ConfigureAwait(false);
        for (var offset = 0; offset < body.Length; offset += ProtocolConstants.MaxBodyChunk)
        {
            var size = Math.Min(ProtocolConstants.MaxBodyChunk, body.Length - offset);
            await socket.SendAsync(new Frame(FrameType.RequestBody, Id, body.Slice(offset, size)), ct)
                .ConfigureAwait(false);
        }

        await socket.SendAsync(Frame.Empty(FrameType.RequestEnd, Id), ct).ConfigureAwait(false);
    }

    public async ValueTask<StreamEvent> ReadAsync(CancellationToken ct)
    {
        try
        {
            var ev = await _events.Reader.ReadAsync(ct).ConfigureAwait(false);
            if (ev.Kind == StreamEventKind.Body)
            {
                Interlocked.Add(ref _buffered, -ev.Data.Length);
            }

            return ev;
        }
        catch (ChannelClosedException)
        {
            return new StreamEvent(StreamEventKind.Error, Code: Overflowed ? CancelReasons.Overflow : "node_gone");
        }
    }

    /// <summary>Asks the node to stop working on this stream.</summary>
    public void Cancel(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _connection.Socket.TrySend(ProtocolJson.ToFrame(FrameType.Cancel, Id, new CancelMessage(reason)));
        }

        _connection.Forget(Id);
    }

    /// <summary>Marks the stream finished normally so disposing does not send a cancel.</summary>
    internal void MarkClosed() => Interlocked.Exchange(ref _closed, 1);

    internal bool IsClosed => Volatile.Read(ref _closed) == 1;

    internal void Deliver(Frame frame)
    {
        switch (frame.Type)
        {
            case FrameType.ResponseHead:
                var head = ProtocolJson.Read<ResponseHeadMessage>(frame);
                _events.Writer.TryWrite(new StreamEvent(StreamEventKind.Head, head.Status, head.ContentType));
                break;

            case FrameType.ResponseBody:
                if (Interlocked.Add(ref _buffered, frame.Payload.Length) > _maxBuffered)
                {
                    Overflowed = true;
                    Cancel(CancelReasons.Overflow);
                    _events.Writer.TryComplete();
                    return;
                }

                _events.Writer.TryWrite(new StreamEvent(StreamEventKind.Body, Data: frame.Payload));
                break;

            case FrameType.ResponseEnd:
                MarkClosed();
                _events.Writer.TryWrite(new StreamEvent(StreamEventKind.End));
                _events.Writer.TryComplete();
                _connection.Forget(Id);
                break;

            case FrameType.Error:
                MarkClosed();
                var error = ProtocolJson.Read<ErrorMessage>(frame);
                _events.Writer.TryWrite(new StreamEvent(StreamEventKind.Error, Code: error.Code));
                _events.Writer.TryComplete();
                _connection.Forget(Id);
                break;

            case FrameType.Cancel:
                MarkClosed();
                _events.Writer.TryWrite(new StreamEvent(StreamEventKind.Error, Code: "node_cancelled"));
                _events.Writer.TryComplete();
                _connection.Forget(Id);
                break;
        }
    }

    internal void Fail() => _events.Writer.TryComplete();
}
