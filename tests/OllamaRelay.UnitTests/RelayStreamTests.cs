using System.Net.WebSockets;
using MongoDB.Bson;
using OllamaRelay.Protocol;
using OllamaRelay.Relay.Nodes;

namespace OllamaRelay.UnitTests;

public class RelayStreamTests
{
    private static NodeConnection Connection(int perStreamBuffer) =>
        new(ObjectId.GenerateNewId(), "n", 2,
            new FrameSocket(WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions())), "t",
            perStreamBuffer);

    [Fact]
    public async Task Slow_consumer_overflow_cancels_only_that_stream()
    {
        var connection = Connection(perStreamBuffer: 100);
        var slow = connection.OpenStream();
        var other = connection.OpenStream();
        var ct = TestContext.Current.CancellationToken;

        connection.Dispatch(ProtocolJson.ToFrame(FrameType.ResponseHead, slow.Id, new ResponseHeadMessage(200, "x")));
        for (var i = 0; i < 5; i++)
        {
            connection.Dispatch(new Frame(FrameType.ResponseBody, slow.Id, new byte[40]));
        }

        connection.Dispatch(new Frame(FrameType.ResponseBody, other.Id, new byte[40]));
        connection.Dispatch(Frame.Empty(FrameType.ResponseEnd, other.Id));

        Assert.Equal(StreamEventKind.Head, (await slow.ReadAsync(ct)).Kind);
        Assert.Equal(StreamEventKind.Body, (await slow.ReadAsync(ct)).Kind);
        Assert.Equal(StreamEventKind.Body, (await slow.ReadAsync(ct)).Kind);
        var end = await slow.ReadAsync(ct);
        Assert.Equal(StreamEventKind.Error, end.Kind);
        Assert.Equal(CancelReasons.Overflow, end.Code);
        Assert.True(slow.Overflowed);

        Assert.Equal(StreamEventKind.Body, (await other.ReadAsync(ct)).Kind);
        Assert.Equal(StreamEventKind.End, (await other.ReadAsync(ct)).Kind);
    }

    [Fact]
    public async Task Node_loss_fails_open_streams()
    {
        var connection = Connection(1024);
        var stream = connection.OpenStream();
        connection.MarkClosed();
        var ev = await stream.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(StreamEventKind.Error, ev.Kind);
        Assert.True(connection.Closed.IsCancellationRequested);
    }
}
