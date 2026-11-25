namespace OllamaRelay.Relay;

/// <summary>
/// Relay settings. Read from plain environment variables (see deploy/relay/.env.example).
/// </summary>
public sealed class RelayOptions
{
    public string PublicBaseUrl { get; init; } = "http://localhost:8080";

    public string MongoUrl { get; init; } = "mongodb://localhost:27017";

    public string MongoDatabase { get; init; } = "ollama_relay";

    public required string JwtSigningKey { get; init; }

    public string? AdminUsername { get; init; }

    public string? AdminInitialPassword { get; init; }

    public bool StripKeepAlive { get; init; } = true;

    public long MaxRequestBodyBytes { get; init; } = 32L * 1024 * 1024;

    public TimeSpan QueueTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int QueueMaxLength { get; init; } = 64;

    public int UserMaxConcurrentStreams { get; init; } = 4;

    public string[] CorsAllowedOrigins { get; init; } = [];

    public bool CookieSecure { get; init; } = true;

    public bool TrustForwardedHeaders { get; init; }

    public int HeartbeatIntervalSeconds { get; init; } = 5;

    public TimeSpan NodeOfflineAfter { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Longest time without any byte from the node, including model load time.</summary>
    public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan StreamTotalTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Bytes buffered for one stream before that stream is cancelled as a slow consumer.</summary>
    public int PerStreamBufferBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Largest non-streamed JSON response the relay rewrites. Larger ones pass through unmodified.</summary>
    public int MaxRewriteBytes { get; init; } = 64 * 1024 * 1024;

    public int Argon2MemoryKib { get; init; } = 65536;

    public int Argon2Iterations { get; init; } = 3;

    public static RelayOptions FromConfiguration(IConfiguration c)
    {
        var key = c["JWT_SIGNING_KEY"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
        {
            throw new InvalidOperationException(
                "JWT_SIGNING_KEY is required and must be at least 32 characters (for example: openssl rand -base64 48)");
        }

        return new RelayOptions
        {
            PublicBaseUrl = (c["PUBLIC_BASE_URL"] ?? "http://localhost:8080").TrimEnd('/'),
            MongoUrl = c["MONGO_URL"] ?? "mongodb://localhost:27017",
            MongoDatabase = c["MONGO_DATABASE"] ?? "ollama_relay",
            JwtSigningKey = key,
            AdminUsername = c["ADMIN_USERNAME"],
            AdminInitialPassword = c["ADMIN_INITIAL_PASSWORD"],
            StripKeepAlive = Bool(c, "STRIP_KEEP_ALIVE", true),
            MaxRequestBodyBytes = Int(c, "MAX_REQUEST_BODY_MB", 32) * 1024L * 1024,
            QueueTimeout = TimeSpan.FromSeconds(Int(c, "QUEUE_TIMEOUT_SECONDS", 30)),
            QueueMaxLength = Int(c, "QUEUE_MAX_LENGTH", 64),
            UserMaxConcurrentStreams = Int(c, "USER_MAX_CONCURRENT_STREAMS", 4),
            CorsAllowedOrigins = (c["CORS_ALLOWED_ORIGINS"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            CookieSecure = Bool(c, "COOKIE_SECURE", true),
            TrustForwardedHeaders = Bool(c, "TRUST_FORWARDED_HEADERS", false),
            HeartbeatIntervalSeconds = Int(c, "NODE_HEARTBEAT_SECONDS", 5),
            NodeOfflineAfter = TimeSpan.FromSeconds(Int(c, "NODE_OFFLINE_AFTER_SECONDS", 20)),
            StreamIdleTimeout = TimeSpan.FromSeconds(Int(c, "STREAM_IDLE_TIMEOUT_SECONDS", 600)),
            StreamTotalTimeout = TimeSpan.FromSeconds(Int(c, "STREAM_TOTAL_TIMEOUT_SECONDS", 7200)),
            PerStreamBufferBytes = Int(c, "STREAM_BUFFER_KB", 8192) * 1024,
            Argon2MemoryKib = Int(c, "ARGON2_MEMORY_KIB", 65536),
            Argon2Iterations = Int(c, "ARGON2_ITERATIONS", 3),
        };
    }

    private static bool Bool(IConfiguration c, string key, bool fallback) =>
        c[key] is { } v ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1" : fallback;

    private static int Int(IConfiguration c, string key, int fallback) =>
        int.TryParse(c[key], out var v) ? v : fallback;
}
