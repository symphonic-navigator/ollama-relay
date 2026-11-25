using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using OllamaRelay.Protocol;

namespace OllamaRelay.Node;

/// <summary>
/// Keeps one outbound WebSocket to the relay alive and serves multiplexed inference streams over it.
/// </summary>
public sealed class NodeAgent(NodeOptions options, OllamaClient ollama, ILogger<NodeAgent> logger)
{
    public static readonly string AgentVersion =
        typeof(NodeAgent).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    private readonly ConcurrentDictionary<uint, NodeStream> _streams = new();

    /// <summary>Raised after each successful handshake. Used by tests.</summary>
    public event Action? Connected;

    public int ActiveStreams => _streams.Count;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var sessionStarted = DateTimeOffset.UtcNow;
            try
            {
                await RunSessionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (FatalRelayException ex)
            {
                logger.LogError("relay refused the connection permanently: {Reason}", ex.Message);
                // Keep retrying slowly; the admin may fix the token without restarting the node.
                attempt = Math.Max(attempt, 6);
            }
            catch (Exception ex) when (ex is WebSocketException or ProtocolException or HttpRequestException
                                           or IOException or OperationCanceledException)
            {
                logger.LogWarning("relay connection lost: {Error}", ex.GetType().Name + ": " + SafeMessage(ex));
            }

            // A session that stayed up for a while resets the backoff.
            if (DateTimeOffset.UtcNow - sessionStarted > TimeSpan.FromMinutes(1))
            {
                attempt = 0;
            }

            var delay = Backoff.Compute(attempt++, options.MinBackoff, options.MaxBackoff);
            logger.LogInformation("reconnecting in {DelayMs} ms", (long)delay.TotalMilliseconds);
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static string SafeMessage(Exception ex) =>
        // Transport exceptions never contain body content, but keep it short anyway.
        ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Authorization", "Bearer " + options.NodeToken);
        client.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        client.Options.CollectHttpResponseDetails = true;

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await client.ConnectAsync(options.RelayUrl, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (WebSocketException) when (client.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized
                                              or System.Net.HttpStatusCode.Forbidden)
        {
            throw new FatalRelayException("node token rejected (HTTP " + (int)client.HttpStatusCode + ")");
        }

        await using var socket = new FrameSocket(client);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        await socket.SendAsync(ProtocolJson.ToFrame(FrameType.Hello, 0,
            new HelloMessage(ProtocolConstants.Version, AgentVersion)), stoppingToken).ConfigureAwait(false);

        var first = await socket.ReceiveAsync(connectTimeout.Token).ConfigureAwait(false);
        if (first is null)
        {
            if (client.CloseStatus == (WebSocketCloseStatus)ProtocolConstants.CloseProtocolMismatch)
            {
                throw new FatalRelayException("protocol version mismatch: " + client.CloseStatusDescription);
            }

            throw new WebSocketException("relay closed during handshake");
        }

        if (first.Value.Type != FrameType.HelloAck)
        {
            throw new ProtocolException("expected hello ack");
        }

        var ack = ProtocolJson.Read<HelloAckMessage>(first.Value);
        logger.LogInformation("connected to relay as node {NodeName} (protocol {Version})", ack.NodeName,
            ack.ProtocolVersion);
        Connected?.Invoke();

        var interval = TimeSpan.FromSeconds(Math.Clamp(ack.HeartbeatIntervalSeconds, 1, 60));
        var heartbeat = HeartbeatLoopAsync(socket, interval, sessionCts.Token);

        try
        {
            await ReceiveLoopAsync(socket, sessionCts.Token).ConfigureAwait(false);
        }
        finally
        {
            await sessionCts.CancelAsync().ConfigureAwait(false);
            foreach (var stream in _streams.Values)
            {
                stream.Cancel(CancelReasons.Shutdown);
            }

            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Heartbeat ends with the session.
            }

            if (stoppingToken.IsCancellationRequested)
            {
                await socket.CloseAsync((int)WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (client.CloseStatus is { } status)
            {
                logger.LogWarning("relay closed the connection: {Status} {Description}", (int)status,
                    client.CloseStatusDescription);
                if ((int)status is ProtocolConstants.CloseRevoked or ProtocolConstants.CloseProtocolMismatch)
                {
                    throw new FatalRelayException(client.CloseStatusDescription ?? "closed by relay");
                }
            }
        }
    }

    private async Task HeartbeatLoopAsync(FrameSocket socket, TimeSpan interval, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var status = await ollama.GetStatusAsync(_streams.Count, token).ConfigureAwait(false);
            await socket.SendAsync(ProtocolJson.ToFrame(FrameType.Heartbeat, 0, status), token).ConfigureAwait(false);
            await Task.Delay(interval, token).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(FrameSocket socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var maybe = await socket.ReceiveAsync(token).ConfigureAwait(false);
            if (maybe is not { } frame)
            {
                return;
            }

            switch (frame.Type)
            {
                case FrameType.RequestHead:
                    OpenStream(socket, frame, token);
                    break;

                case FrameType.RequestBody:
                    if (_streams.TryGetValue(frame.StreamId, out var s) && !s.AppendBody(frame.Payload.Span))
                    {
                        FailStream(socket, frame.StreamId, ErrorCodes.BadRequest);
                    }

                    break;

                case FrameType.RequestEnd:
                    if (_streams.TryGetValue(frame.StreamId, out var ready))
                    {
                        _ = RunStreamAsync(frame.StreamId, ready);
                    }

                    break;

                case FrameType.Cancel:
                    if (_streams.TryGetValue(frame.StreamId, out var cancelled))
                    {
                        var reason = ProtocolJson.Read<CancelMessage>(frame).Reason;
                        cancelled.Cancel(reason);
                    }

                    break;

                default:
                    throw new ProtocolException($"unexpected frame {frame.Type} from relay");
            }
        }
    }

    private void OpenStream(FrameSocket socket, Frame frame, CancellationToken token)
    {
        var head = ProtocolJson.Read<RequestHeadMessage>(frame);
        if (!NodeAllowlist.IsAllowed(head.Method, head.Path))
        {
            logger.LogWarning("stream {StreamId} refused: {Method} {Path} is not on the node allowlist",
                frame.StreamId, head.Method, SanitizePath(head.Path));
            socket.TrySend(ProtocolJson.ToFrame(FrameType.Error, frame.StreamId, new ErrorMessage(ErrorCodes.NotAllowed)));
            return;
        }

        var stream = new NodeStream(frame.StreamId, head, socket, ollama, logger, options.MaxRequestBodyBytes, token);
        if (!_streams.TryAdd(frame.StreamId, stream))
        {
            stream.Dispose();
            throw new ProtocolException("duplicate stream id");
        }
    }

    private void FailStream(FrameSocket socket, uint id, string code)
    {
        if (_streams.TryRemove(id, out var stream))
        {
            stream.Dispose();
        }

        socket.TrySend(ProtocolJson.ToFrame(FrameType.Error, id, new ErrorMessage(code)));
    }

    private async Task RunStreamAsync(uint id, NodeStream stream)
    {
        try
        {
            await stream.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError("stream {StreamId} failed: {Error}", id, ex.GetType().Name);
        }
        finally
        {
            _streams.TryRemove(id, out _);
            stream.Dispose();
        }
    }

    private static string SanitizePath(string path) =>
        path.Length > 100 ? path[..100] : new string(path.Where(c => !char.IsControl(c)).ToArray());
}

public sealed class FatalRelayException(string message) : Exception(message);

public static class Backoff
{
    /// <summary>Exponential backoff with full jitter.</summary>
    public static TimeSpan Compute(int attempt, TimeSpan min, TimeSpan max)
    {
        var ceiling = Math.Min(max.TotalMilliseconds, min.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 20)));
        var jittered = Random.Shared.NextDouble() * ceiling;
        return TimeSpan.FromMilliseconds(Math.Max(min.TotalMilliseconds, jittered));
    }
}
