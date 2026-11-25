using OllamaRelay.Protocol;

namespace OllamaRelay.UnitTests;

public class ProtocolTests
{
    [Fact]
    public void Frame_round_trips_type_stream_and_payload()
    {
        var frame = new Frame(FrameType.ResponseBody, 0xDEADBEEF, new byte[] { 1, 2, 3 });
        var encoded = frame.Encode();

        Assert.Equal(Frame.HeaderSize + 3, encoded.Length);
        Assert.Equal((byte)FrameType.ResponseBody, encoded[0]);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, encoded[1..5]);

        var decoded = Frame.Decode(encoded);
        Assert.Equal(FrameType.ResponseBody, decoded.Type);
        Assert.Equal(0xDEADBEEFu, decoded.StreamId);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Payload.ToArray());
    }

    [Fact]
    public void Empty_frame_has_header_only()
    {
        var encoded = Frame.Empty(FrameType.RequestEnd, 7).Encode();
        Assert.Equal(Frame.HeaderSize, encoded.Length);
        Assert.True(Frame.Decode(encoded).Payload.IsEmpty);
    }

    [Fact]
    public void Decode_rejects_short_and_unknown_frames()
    {
        Assert.Throws<ProtocolException>(() => Frame.Decode(new byte[] { 0x01, 0, 0 }));
        Assert.Throws<ProtocolException>(() => Frame.Decode(new byte[] { 0x7F, 0, 0, 0, 1 }));
    }

    [Fact]
    public void Json_messages_round_trip()
    {
        var frame = ProtocolJson.ToFrame(FrameType.Hello, 0, new HelloMessage(ProtocolConstants.Version, "1.2.3"));
        var hello = ProtocolJson.Read<HelloMessage>(Frame.Decode(frame.Encode()));
        Assert.Equal(ProtocolConstants.Version, hello.ProtocolVersion);
        Assert.Equal("1.2.3", hello.AgentVersion);
    }

    [Fact]
    public void Malformed_json_is_a_protocol_error()
    {
        var frame = new Frame(FrameType.Hello, 0, "{nope"u8.ToArray());
        Assert.Throws<ProtocolException>(() => ProtocolJson.Read<HelloMessage>(frame));
    }
}
