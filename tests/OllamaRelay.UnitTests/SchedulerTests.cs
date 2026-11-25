using System.Net.WebSockets;
using MongoDB.Bson;
using OllamaRelay.Protocol;
using OllamaRelay.Relay.Data;
using OllamaRelay.Relay.Inference;
using OllamaRelay.Relay.Nodes;

namespace OllamaRelay.UnitTests;

public class SchedulerTests
{
    private static NodeConnection Node(string name, int max, params string[] loaded)
    {
        var socket = new FrameSocket(WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions()));
        var c = new NodeConnection(ObjectId.GenerateNewId(), name, max, socket, "test", 1024);
        typeof(NodeConnection).GetMethod("OnHeartbeat", System.Reflection.BindingFlags.NonPublic |
                                                         System.Reflection.BindingFlags.Instance)!
            .Invoke(c, [new HeartbeatMessage("0.12.0", true, [], loaded.Select(l => new LoadedModel(l, 1, 1, null, null)).ToList(), 0, null)]);
        return c;
    }

    private static PublicationDoc Pub(string upstream) =>
        new() { Id = ObjectId.GenerateNewId(), Alias = "a", UpstreamModel = upstream };

    [Fact]
    public void Prefers_node_with_model_loaded_then_least_busy()
    {
        var a = Node("a", 2);
        var b = Node("b", 2, "m");
        var c = Node("c", 2);
        var list = new List<Candidate> { new(Pub("m"), a), new(Pub("m"), b), new(Pub("m"), c) };

        Assert.Same(b, Scheduler.Pick(list)!.Node);

        b.IncrementLeases();
        b.IncrementLeases();
        a.IncrementLeases();
        Assert.Same(c, Scheduler.Pick(list)!.Node);
    }

    [Fact]
    public async Task Waits_for_a_slot_then_times_out()
    {
        var scheduler = new Scheduler();
        var node = Node("n", 1);
        var list = new List<Candidate> { new(Pub("m"), node) };

        var first = await scheduler.AcquireAsync(() => list, 10, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(AcquireStatus.Acquired, first.Status);

        var waiting = scheduler.AcquireAsync(() => list, 10, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, scheduler.Waiting);
        first.Lease!.Dispose();
        var second = await waiting;
        Assert.Equal(AcquireStatus.Acquired, second.Status);
        Assert.Equal(0, scheduler.Waiting);

        var timedOut = await scheduler.AcquireAsync(() => list, 10, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.Equal(AcquireStatus.Timeout, timedOut.Status);

        var full = await scheduler.AcquireAsync(() => list, 0, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(AcquireStatus.QueueFull, full.Status);

        var none = await scheduler.AcquireAsync(() => [], 10, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(AcquireStatus.NoCandidate, none.Status);
        second.Lease!.Dispose();
    }

    [Fact]
    public void Per_user_stream_limit()
    {
        var active = new ActiveStreams();
        var user = ObjectId.GenerateNewId();
        ActiveStream S() => new() { UserId = user, Username = "u", ApiKeyId = ObjectId.Empty, Alias = "a", Endpoint = "/" };
        Assert.True(active.TryAdd(S(), 2));
        var second = S();
        Assert.True(active.TryAdd(second, 2));
        Assert.False(active.TryAdd(S(), 2));
        active.Remove(second);
        Assert.True(active.TryAdd(S(), 2));
    }
}
