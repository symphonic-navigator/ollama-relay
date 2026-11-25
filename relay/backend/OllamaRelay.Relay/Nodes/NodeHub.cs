using System.Collections.Concurrent;
using System.Net.WebSockets;
using MongoDB.Bson;
using MongoDB.Driver;
using OllamaRelay.Protocol;
using OllamaRelay.Relay.Data;
using OllamaRelay.Relay.Inference;
using OllamaRelay.Relay.Mgmt;
using OllamaRelay.Relay.Security;

namespace OllamaRelay.Relay.Nodes;

/// <summary>Accepts node WebSockets and keeps the set of live connections.</summary>
public sealed class NodeHub(
    MongoStore store,
    RelayOptions options,
    Scheduler scheduler,
    AuditService audit,
    Limiters limiters,
    ILogger<NodeHub> logger)
{
    private readonly ConcurrentDictionary<ObjectId, NodeConnection> _connections = new();

    public NodeConnection? Get(ObjectId nodeId) =>
        _connections.TryGetValue(nodeId, out var c) && !c.IsClosed ? c : null;

    public IReadOnlyCollection<NodeConnection> All => _connections.Values.Where(c => !c.IsClosed).ToList();

    /// <summary>Closes the live connection of a node, for token rotation, revocation and deletion.</summary>
    public async Task DisconnectAsync(ObjectId nodeId, int status, string reason)
    {
        if (_connections.TryRemove(nodeId, out var connection))
        {
            connection.MarkClosed();
            scheduler.Signal();
            await connection.Socket.CloseAsync(status, reason).ConfigureAwait(false);
            connection.Socket.Abort();
        }
    }

    public void UpdateSettings(ObjectId nodeId, string name, int maxConcurrency)
    {
        if (Get(nodeId) is { } c)
        {
            c.NodeName = name;
            c.MaxConcurrency = maxConcurrency;
            scheduler.Signal();
        }
    }

    public async Task HandleAsync(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (limiters.NodeTokenByIp.IsBlocked(ip))
        {
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        var token = BearerToken(http);
        NodeDoc? node = null;
        if (SecretTokens.LooksLike(token, SecretTokens.NodeTokenPrefix))
        {
            var hash = SecretTokens.Hash(token!);
            node = await store.Nodes.Find(n => n.TokenHash == hash).FirstOrDefaultAsync(http.RequestAborted);
        }

        if (node is null)
        {
            limiters.NodeTokenByIp.RecordFailure(ip);
            logger.LogWarning("security: node connect with invalid token from {Ip}", ip);
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var ws = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
        });
        var socket = new FrameSocket(ws);
        await using var socketScope = socket;

        HelloMessage hello;
        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var first = await socket.ReceiveAsync(helloTimeout.Token);
            if (first is not { Type: FrameType.Hello } helloFrame)
            {
                await socket.CloseAsync((int)WebSocketCloseStatus.ProtocolError, "expected hello");
                return;
            }

            hello = ProtocolJson.Read<HelloMessage>(helloFrame);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ProtocolException or WebSocketException)
        {
            logger.LogWarning("node {Node} handshake failed: {Error}", node.Name, ex.GetType().Name);
            return;
        }

        if (hello.ProtocolVersion != ProtocolConstants.Version)
        {
            logger.LogWarning("node {Node} refused: protocol version {Theirs}, relay speaks {Ours}", node.Name,
                hello.ProtocolVersion, ProtocolConstants.Version);
            await socket.CloseAsync(ProtocolConstants.CloseProtocolMismatch,
                $"protocol version mismatch: relay speaks {ProtocolConstants.Version}");
            return;
        }

        var connection = new NodeConnection(node.Id, node.Name, node.MaxConcurrency, socket,
            Truncate(hello.AgentVersion, 40), options.PerStreamBufferBytes);

        if (_connections.TryGetValue(node.Id, out var previous))
        {
            logger.LogWarning("node {Node} connected again; replacing the previous connection", node.Name);
            await audit.WriteAsync(null, "node.connection_replaced", "node", node.Id.ToString(), node.Name);
            await DisconnectAsync(node.Id, ProtocolConstants.CloseReplaced, "replaced by a newer connection");
            _ = previous.NodeId;
        }

        _connections[node.Id] = connection;
        await socket.SendAsync(ProtocolJson.ToFrame(FrameType.HelloAck, 0,
            new HelloAckMessage(ProtocolConstants.Version, node.Name, options.HeartbeatIntervalSeconds)));
        logger.LogInformation("node {Node} online from {Ip} (agent {Agent})", node.Name, ip, connection.AgentVersion);
        await store.Nodes.UpdateOneAsync(n => n.Id == node.Id, Builders<NodeDoc>.Update
            .Set(n => n.LastSeenAt, DateTime.UtcNow)
            .Set(n => n.LastAgentVersion, connection.AgentVersion));
        scheduler.Signal();

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, connection.Closed);
        var watch = WatchHeartbeatsAsync(connection, watchdog);
        try
        {
            await ReceiveLoopAsync(connection, watchdog.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ProtocolException
                                       or IOException)
        {
            if (ex is ProtocolException)
            {
                logger.LogWarning("node {Node} protocol error: {Error}", node.Name, ex.Message);
            }
        }
        finally
        {
            if (_connections.TryGetValue(node.Id, out var current) && ReferenceEquals(current, connection))
            {
                _connections.TryRemove(node.Id, out _);
            }

            connection.MarkClosed();
            scheduler.Signal();
            await watchdog.CancelAsync();
            try
            {
                await watch;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            logger.LogInformation("node {Node} offline", node.Name);
        }
    }

    private async Task WatchHeartbeatsAsync(NodeConnection connection, CancellationTokenSource session)
    {
        while (!session.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), session.Token);
            if (DateTimeOffset.UtcNow - connection.LastHeartbeatAt > options.NodeOfflineAfter)
            {
                logger.LogWarning("node {Node} missed heartbeats; dropping the connection", connection.NodeName);
                connection.Socket.Abort();
                await session.CancelAsync();
                return;
            }
        }
    }

    private async Task ReceiveLoopAsync(NodeConnection connection, CancellationToken ct)
    {
        var lastPersist = DateTimeOffset.MinValue;
        string? lastModelsKey = null;
        while (!ct.IsCancellationRequested)
        {
            var maybe = await connection.Socket.ReceiveAsync(ct);
            if (maybe is not { } frame)
            {
                return;
            }

            switch (frame.Type)
            {
                case FrameType.Heartbeat:
                    var heartbeat = ProtocolJson.Read<HeartbeatMessage>(frame);
                    connection.OnHeartbeat(heartbeat);
                    scheduler.Signal();
                    var modelsKey = string.Join('\n', heartbeat.Models.Select(m => m.Name + "@" + m.Digest));
                    if (modelsKey != lastModelsKey || DateTimeOffset.UtcNow - lastPersist > TimeSpan.FromMinutes(1))
                    {
                        await PersistHeartbeatAsync(connection.NodeId, heartbeat, modelsKey != lastModelsKey, ct);
                        lastModelsKey = modelsKey;
                        lastPersist = DateTimeOffset.UtcNow;
                    }

                    break;

                case FrameType.ResponseHead:
                case FrameType.ResponseBody:
                case FrameType.ResponseEnd:
                case FrameType.Error:
                case FrameType.Cancel:
                    connection.Dispatch(frame);
                    break;

                default:
                    throw new ProtocolException($"unexpected frame {frame.Type} from node");
            }
        }
    }

    private async Task PersistHeartbeatAsync(ObjectId nodeId, HeartbeatMessage heartbeat, bool modelsChanged,
        CancellationToken ct)
    {
        var update = Builders<NodeDoc>.Update
            .Set(n => n.LastSeenAt, DateTime.UtcNow)
            .Set(n => n.LastOllamaVersion, heartbeat.OllamaVersion);
        if (modelsChanged && heartbeat.OllamaReachable)
        {
            update = update.Set(n => n.ReportedModels, heartbeat.Models.Take(500).Select(m => new ReportedModel
            {
                Name = Truncate(m.Name, 300),
                Size = m.Size,
                Family = Truncate(m.Family, 60),
                ParameterSize = Truncate(m.ParameterSize, 30),
                QuantizationLevel = Truncate(m.QuantizationLevel, 30),
                ModifiedAt = m.ModifiedAt?.UtcDateTime,
            }).ToList());
        }

        await store.Nodes.UpdateOneAsync(n => n.Id == nodeId, update, cancellationToken: ct);
    }

    private static string? BearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(s))]
    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
