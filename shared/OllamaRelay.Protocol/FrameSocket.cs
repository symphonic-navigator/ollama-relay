using System.Buffers;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace OllamaRelay.Protocol;

/// <summary>
/// Frame transport over a WebSocket. Sends go through a single writer loop, so any number
/// of streams may send concurrently. Receives are exposed as an async sequence of frames.
/// </summary>
public sealed class FrameSocket : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Channel<(byte[] Data, TaskCompletionSource? Done)> _outgoing;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private readonly int _maxMessageSize;
    private volatile bool _disposed;

    public FrameSocket(WebSocket socket, int maxMessageSize = ProtocolConstants.MaxMessageSize, int sendQueue = 256)
    {
        _socket = socket;
        _maxMessageSize = maxMessageSize;
        _outgoing = Channel.CreateBounded<(byte[], TaskCompletionSource?)>(new BoundedChannelOptions(sendQueue)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    public WebSocketState State => _socket.State;

    /// <summary>Completes when the frame is queued. Applies back-pressure when the queue is full.</summary>
    public async ValueTask SendAsync(Frame frame, CancellationToken cancellationToken = default)
    {
        await _outgoing.Writer.WriteAsync((frame.Encode(), null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues a frame without waiting. Used for control frames such as cancel.</summary>
    public bool TrySend(Frame frame) => _outgoing.Writer.TryWrite((frame.Encode(), null));

    /// <summary>Completes when the frame has been handed to the socket.</summary>
    public async Task SendAndFlushAsync(Frame frame, CancellationToken cancellationToken = default)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _outgoing.Writer.WriteAsync((frame.Encode(), done), cancellationToken).ConfigureAwait(false);
        await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var (data, done) in _outgoing.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await _socket.SendAsync(data, WebSocketMessageType.Binary, true, _cts.Token).ConfigureAwait(false);
                done?.TrySetResult();
            }
        }
        catch (Exception)
        {
            // The connection is gone; readers notice through ReceiveAsync.
        }
        finally
        {
            _outgoing.Writer.TryComplete();
            while (_outgoing.Reader.TryRead(out var item))
            {
                item.Done?.TrySetCanceled();
            }
        }
    }

    /// <summary>Reads the next frame, or returns null when the peer closed the connection.</summary>
    public async Task<Frame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var length = 0;
            while (true)
            {
                if (length == rented.Length)
                {
                    if (rented.Length >= _maxMessageSize)
                    {
                        throw new ProtocolException("message too large");
                    }

                    var bigger = ArrayPool<byte>.Shared.Rent(Math.Min(rented.Length * 2, _maxMessageSize));
                    rented.AsSpan(0, length).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = bigger;
                }

                var result = await _socket.ReceiveAsync(rented.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    throw new ProtocolException("text messages are not part of the protocol");
                }

                length += result.Count;
                if (length > _maxMessageSize)
                {
                    throw new ProtocolException("message too large");
                }

                if (result.EndOfMessage)
                {
                    // Copy out so the frame owns its memory independent of the pool.
                    return Frame.Decode(rented.AsSpan(0, length).ToArray());
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    public async Task CloseAsync(int status, string description, CancellationToken cancellationToken = default)
    {
        _outgoing.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort flush.
        }

        try
        {
            if (!_disposed && _socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync((WebSocketCloseStatus)status, description, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Peer already gone.
        }
    }

    public void Abort()
    {
        try
        {
            _cts.Cancel();
            _socket.Abort();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _outgoing.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignored on dispose.
        }

        _socket.Dispose();
        _cts.Dispose();
    }
}
