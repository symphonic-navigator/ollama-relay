using Microsoft.Extensions.Configuration;

namespace OllamaRelay.Node;

public sealed class NodeOptions
{
    public required Uri RelayUrl { get; init; }

    public required string NodeToken { get; init; }

    public Uri OllamaUrl { get; init; } = new("http://127.0.0.1:11434");

    /// <summary>Upper bound for a buffered request body. The relay enforces its own, smaller limit.</summary>
    public int MaxRequestBodyBytes { get; init; } = 64 * 1024 * 1024;

    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    public static NodeOptions FromConfiguration(IConfiguration config)
    {
        var relay = config["RELAY_URL"];
        var token = config["NODE_TOKEN"];
        if (string.IsNullOrWhiteSpace(relay))
        {
            throw new InvalidOperationException("RELAY_URL is required, for example wss://relay.example.net/node/connect");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("NODE_TOKEN is required");
        }

        var relayUri = new Uri(relay);
        var insecure = string.Equals(config["RELAY_ALLOW_INSECURE"], "true", StringComparison.OrdinalIgnoreCase);
        Validate(relayUri, insecure);

        var ollama = config["OLLAMA_URL"];
        return new NodeOptions
        {
            RelayUrl = NormalizeRelayUrl(relayUri),
            NodeToken = token.Trim(),
            OllamaUrl = string.IsNullOrWhiteSpace(ollama) ? new Uri("http://127.0.0.1:11434") : new Uri(ollama),
        };
    }

    public static void Validate(Uri relay, bool allowInsecure)
    {
        if (relay.Scheme == "wss")
        {
            return;
        }

        if (relay.Scheme == "ws" && allowInsecure)
        {
            return;
        }

        throw new InvalidOperationException(
            "RELAY_URL must use wss://. Plain ws:// is only accepted with RELAY_ALLOW_INSECURE=true (development only).");
    }

    /// <summary>Accepts both the bare relay URL and the full connect URL.</summary>
    public static Uri NormalizeRelayUrl(Uri relay)
    {
        if (relay.AbsolutePath.TrimEnd('/').EndsWith(Protocol.ProtocolConstants.ConnectPath, StringComparison.Ordinal))
        {
            return relay;
        }

        var builder = new UriBuilder(relay)
        {
            Path = relay.AbsolutePath.TrimEnd('/') + Protocol.ProtocolConstants.ConnectPath,
        };
        return builder.Uri;
    }
}
