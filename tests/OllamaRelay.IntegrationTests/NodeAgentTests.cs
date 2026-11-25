using System.Net.WebSockets;
using System.Text;
using OllamaRelay.Node;
using OllamaRelay.Protocol;

namespace OllamaRelay.IntegrationTests;

/// <summary>
/// Plays a compromised relay against a real node agent: the node must refuse everything that is not
/// an allowlisted inference call, and must only ever talk to its configured Ollama.
/// </summary>
public sealed class NodeAgentTests
{
    [Fact]
    public async Task Node_refuses_non_allowlisted_calls_from_a_hostile_relay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ollama = await FakeOllama.StartAsync();
        var frames = System.Threading.Channels.Channel.CreateUnbounded<Frame>();
        FrameSocket? relaySide = null;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var relay = builder.Build();
        relay.UseWebSockets();
        relay.Map("/node/connect", async (HttpContext http) =>
        {
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            relaySide = new FrameSocket(ws);
            var hello = await relaySide.ReceiveAsync(http.RequestAborted);
            Assert.Equal(FrameType.Hello, hello!.Value.Type);
            await relaySide.SendAsync(ProtocolJson.ToFrame(FrameType.HelloAck, 0, new HelloAckMessage(1, "evil", 1)));
            connected.TrySetResult();
            while (await relaySide.ReceiveAsync(http.RequestAborted) is { } f)
            {
                if (f.Type != FrameType.Heartbeat)
                {
                    frames.Writer.TryWrite(f);
                }
            }
        });
        await relay.StartAsync(ct);

        var logs = new LogCapture();
        var agent = new NodeAgent(new NodeOptions
        {
            RelayUrl = new Uri(relay.Urls.First().Replace("http://", "ws://") + "/node/connect"),
            NodeToken = "orn_test",
            OllamaUrl = ollama.Url,
        }, new OllamaClient(new HttpClient { BaseAddress = ollama.Url }),
            LoggerFactory.Create(b => b.AddProvider(logs)).CreateLogger<NodeAgent>());
        using var stop = new CancellationTokenSource();
        var run = agent.RunAsync(stop.Token);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        async Task<Frame> Request(uint id, string method, string path, string body)
        {
            await relaySide!.SendAsync(ProtocolJson.ToFrame(FrameType.RequestHead, id,
                new RequestHeadMessage(method, path, "application/json")), ct);
            await relaySide.SendAsync(new Frame(FrameType.RequestBody, id, Encoding.UTF8.GetBytes(body)), ct);
            await relaySide.SendAsync(Frame.Empty(FrameType.RequestEnd, id), ct);
            while (true)
            {
                var f = await frames.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
                if (f.StreamId == id && f.Type is FrameType.Error or FrameType.ResponseHead)
                {
                    return f;
                }
            }
        }

        uint next = 1;
        foreach (var (method, path) in new[]
                 {
                     ("POST", "/api/pull"), ("DELETE", "/api/delete"), ("POST", "/api/create"), ("POST", "/api/copy"),
                     ("POST", "/api/push"), ("POST", "/api/chat?x=1"), ("POST", "/../api/pull"),
                     ("POST", "http://169.254.169.254/latest/meta-data"), ("POST", "//evil.example/api/chat"),
                 })
        {
            var answer = await Request(next++, method, path, "{\"model\":\"fake-model:7b\"}");
            Assert.True(answer.Type == FrameType.Error, $"{method} {path}: {answer.Type}");
            Assert.Equal(ErrorCodes.NotAllowed, ProtocolJson.Read<ErrorMessage>(answer).Code);
        }

        var ok = await Request(next, "POST", "/api/chat",
            "{\"model\":\"fake-model:7b\",\"stream\":false,\"messages\":[]}");
        Assert.Equal(FrameType.ResponseHead, ok.Type);
        Assert.True(200 == ProtocolJson.Read<ResponseHeadMessage>(ok).Status, logs.All);

        Assert.All(ollama.Requests, r => Assert.Equal("POST /api/chat", r.Path));

        await stop.CancelAsync();
        await run;
    }
}
