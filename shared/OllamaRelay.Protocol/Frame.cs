using System.Buffers.Binary;

namespace OllamaRelay.Protocol;

/// <summary>
/// One protocol frame. A frame is carried in exactly one binary WebSocket message:
/// <code>
/// byte 0      frame type
/// bytes 1..4  stream id, unsigned 32 bit, big endian (0 = connection level)
/// bytes 5..   payload
/// </code>
/// </summary>
public readonly record struct Frame(FrameType Type, uint StreamId, ReadOnlyMemory<byte> Payload)
{
    public const int HeaderSize = 5;

    public static Frame Empty(FrameType type, uint streamId) => new(type, streamId, ReadOnlyMemory<byte>.Empty);

    public byte[] Encode()
    {
        var buffer = new byte[HeaderSize + Payload.Length];
        buffer[0] = (byte)Type;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(1, 4), StreamId);
        Payload.Span.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    /// <summary>Decodes a frame. The payload references <paramref name="message"/> without copying.</summary>
    public static Frame Decode(ReadOnlyMemory<byte> message)
    {
        if (message.Length < HeaderSize)
        {
            throw new ProtocolException("frame shorter than header");
        }

        var span = message.Span;
        var type = (FrameType)span[0];
        if (!Enum.IsDefined(type))
        {
            throw new ProtocolException($"unknown frame type 0x{span[0]:x2}");
        }

        var streamId = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(1, 4));
        return new Frame(type, streamId, message[HeaderSize..]);
    }
}

public sealed class ProtocolException(string message) : Exception(message);
