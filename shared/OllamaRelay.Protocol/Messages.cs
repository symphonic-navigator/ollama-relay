using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaRelay.Protocol;

public static class ProtocolConstants
{
    /// <summary>Current wire protocol version. Bumped on any incompatible change.</summary>
    public const int Version = 1;

    /// <summary>Largest payload a sender puts in one body frame.</summary>
    public const int MaxBodyChunk = 64 * 1024;

    /// <summary>Largest WebSocket message a receiver accepts.</summary>
    public const int MaxMessageSize = 4 * 1024 * 1024;

    /// <summary>WebSocket close status used when the relay refuses a protocol version.</summary>
    public const int CloseProtocolMismatch = 4001;

    /// <summary>WebSocket close status used when the token was replaced, rotated or revoked.</summary>
    public const int CloseReplaced = 4002;

    public const int CloseRevoked = 4003;

    public const string ConnectPath = "/node/connect";
}

public sealed record HelloMessage(int ProtocolVersion, string AgentVersion);

public sealed record HelloAckMessage(int ProtocolVersion, string NodeName, int HeartbeatIntervalSeconds);

public sealed record HeartbeatMessage(
    string? OllamaVersion,
    bool OllamaReachable,
    IReadOnlyList<LocalModel> Models,
    IReadOnlyList<LoadedModel> Loaded,
    int ActiveStreams,
    HostStats? Host);

public sealed record LocalModel(
    string Name,
    long Size,
    string? Digest,
    DateTimeOffset? ModifiedAt,
    string? Family,
    string? ParameterSize,
    string? QuantizationLevel);

public sealed record LoadedModel(
    string Name,
    long Size,
    long SizeVram,
    DateTimeOffset? ExpiresAt,
    int? ContextLength);

public sealed record HostStats(int CpuCount, double? LoadAverage1, long? MemoryTotalBytes, long? MemoryAvailableBytes);

public sealed record RequestHeadMessage(string Method, string Path, string? ContentType);

public sealed record ResponseHeadMessage(int Status, string? ContentType);

/// <summary>Reason codes are fixed identifiers. They never carry free text.</summary>
public static class CancelReasons
{
    public const string ClientGone = "client_gone";
    public const string Revoked = "revoked";
    public const string Overflow = "overflow";
    public const string Timeout = "timeout";
    public const string Shutdown = "shutdown";
}

public sealed record CancelMessage(string Reason);

/// <summary>Error codes are fixed identifiers. They never carry upstream text.</summary>
public static class ErrorCodes
{
    public const string NotAllowed = "not_allowed";
    public const string UpstreamUnreachable = "upstream_unreachable";
    public const string UpstreamFailed = "upstream_failed";
    public const string BadRequest = "bad_request";
    public const string Internal = "internal";
}

public sealed record ErrorMessage(string Code);

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Frame ToFrame<T>(FrameType type, uint streamId, T message) =>
        new(type, streamId, JsonSerializer.SerializeToUtf8Bytes(message, Options));

    public static T Read<T>(Frame frame)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(frame.Payload.Span, Options)
                   ?? throw new ProtocolException($"empty {frame.Type} payload");
        }
        catch (JsonException)
        {
            throw new ProtocolException($"malformed {frame.Type} payload");
        }
    }
}
