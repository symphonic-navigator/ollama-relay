namespace OllamaRelay.Protocol;

/// <summary>
/// Frame kinds of the relay/node wire protocol. See docs/PROTOCOL.md.
/// </summary>
public enum FrameType : byte
{
    /// <summary>Node to relay, first frame on a connection. JSON <see cref="HelloMessage"/>.</summary>
    Hello = 0x01,

    /// <summary>Relay to node, answer to <see cref="Hello"/>. JSON <see cref="HelloAckMessage"/>.</summary>
    HelloAck = 0x02,

    /// <summary>Node to relay, periodic status. JSON <see cref="HeartbeatMessage"/>.</summary>
    Heartbeat = 0x03,

    /// <summary>Relay to node, opens a stream. JSON <see cref="RequestHeadMessage"/>.</summary>
    RequestHead = 0x10,

    /// <summary>Relay to node, opaque request body bytes.</summary>
    RequestBody = 0x11,

    /// <summary>Relay to node, request body complete. Empty payload.</summary>
    RequestEnd = 0x12,

    /// <summary>Node to relay, upstream status and content type. JSON <see cref="ResponseHeadMessage"/>.</summary>
    ResponseHead = 0x20,

    /// <summary>Node to relay, opaque response body bytes.</summary>
    ResponseBody = 0x21,

    /// <summary>Node to relay, response complete. Empty payload.</summary>
    ResponseEnd = 0x22,

    /// <summary>Either direction, abort a stream. JSON <see cref="CancelMessage"/>.</summary>
    Cancel = 0x30,

    /// <summary>Node to relay, stream failed before or during the response. JSON <see cref="ErrorMessage"/>.</summary>
    Error = 0x31,
}
