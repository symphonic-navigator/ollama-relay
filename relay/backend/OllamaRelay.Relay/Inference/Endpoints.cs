namespace OllamaRelay.Relay.Inference;

public enum Dialect
{
    Ollama,
    OpenAi,
}

public enum Synthesized
{
    None,
    Tags,
    Ps,
    Version,
    Models,
    Model,
}

public sealed record EndpointSpec(string Method, string Path, Dialect Dialect, string? Kind, Synthesized Synthesized,
    bool OpenAiStreamUsage = false);

/// <summary>The relay's allowlist of inference endpoints. Anything else under /api and /v1 is 404.</summary>
public static class InferenceEndpoints
{
    public static readonly IReadOnlyList<EndpointSpec> Forwarded =
    [
        new("POST", "/api/chat", Dialect.Ollama, "chat", Synthesized.None),
        new("POST", "/api/generate", Dialect.Ollama, "generate", Synthesized.None),
        new("POST", "/api/embed", Dialect.Ollama, "embed", Synthesized.None),
        new("POST", "/api/embeddings", Dialect.Ollama, "embed", Synthesized.None),
        new("POST", "/api/show", Dialect.Ollama, null, Synthesized.None),
        new("POST", "/v1/chat/completions", Dialect.OpenAi, "chat", Synthesized.None, OpenAiStreamUsage: true),
        new("POST", "/v1/completions", Dialect.OpenAi, "generate", Synthesized.None, OpenAiStreamUsage: true),
        new("POST", "/v1/embeddings", Dialect.OpenAi, "embed", Synthesized.None),
        new("POST", "/v1/responses", Dialect.OpenAi, "chat", Synthesized.None),
    ];

    public static readonly IReadOnlyList<EndpointSpec> Local =
    [
        new("GET", "/api/tags", Dialect.Ollama, null, Synthesized.Tags),
        new("GET", "/api/ps", Dialect.Ollama, null, Synthesized.Ps),
        new("GET", "/api/version", Dialect.Ollama, null, Synthesized.Version),
        new("GET", "/v1/models", Dialect.OpenAi, null, Synthesized.Models),
    ];

    public static Dialect DialectOf(PathString path) =>
        path.StartsWithSegments("/v1") ? Dialect.OpenAi : Dialect.Ollama;
}
