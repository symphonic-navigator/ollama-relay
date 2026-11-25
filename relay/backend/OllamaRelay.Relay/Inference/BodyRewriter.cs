using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllamaRelay.Relay.Inference;

/// <summary>
/// The relay touches bodies in exactly two ways: it rewrites model names and it reads usage counters.
/// Nothing here logs, stores or inspects any other field.
/// </summary>
public static class RequestRewriter
{
    private static readonly JsonSerializerOptions Out = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonObject? TryParse(ReadOnlySpan<byte> body)
    {
        try
        {
            return JsonNode.Parse(body, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions { MaxDepth = 256 }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? RequestedModel(JsonObject body) =>
        body["model"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static byte[] Rewrite(JsonObject body, string upstreamModel, EndpointSpec spec, bool stripKeepAlive)
    {
        body["model"] = upstreamModel;
        if (stripKeepAlive)
        {
            body.Remove("keep_alive");
        }

        if (spec.OpenAiStreamUsage && body["stream"] is JsonValue stream && stream.TryGetValue<bool>(out var s) && s)
        {
            if (body["stream_options"] is not JsonObject options)
            {
                options = new JsonObject();
                body["stream_options"] = options;
            }

            options["include_usage"] = true;
        }

        return JsonSerializer.SerializeToUtf8Bytes(body, Out);
    }

    internal static JsonSerializerOptions OutputOptions => Out;
}

public enum RewriteMode
{
    Passthrough,
    Ndjson,
    Sse,
    Json,
}

/// <summary>
/// Rewrites the <c>model</c> field in every response chunk back to the alias and collects token counts.
/// Works incrementally so streamed tokens reach the client without delay.
/// </summary>
public sealed class ResponseRewriter
{
    private static readonly byte[] DataPrefix = "data:"u8.ToArray();
    private readonly string _alias;
    private readonly int _maxBuffer;
    private readonly ArrayBufferWriter<byte> _pending = new();
    private RewriteMode _mode;

    public ResponseRewriter(RewriteMode mode, string alias, int maxBuffer)
    {
        _mode = mode;
        _alias = alias;
        _maxBuffer = maxBuffer;
    }

    public long? PromptTokens { get; private set; }

    public long? CompletionTokens { get; private set; }

    public static RewriteMode ModeFor(int status, string? contentType)
    {
        if (status >= 400 || contentType is null)
        {
            return RewriteMode.Passthrough;
        }

        if (contentType.Contains("ndjson", StringComparison.OrdinalIgnoreCase))
        {
            return RewriteMode.Ndjson;
        }

        if (contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return RewriteMode.Sse;
        }

        return contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ? RewriteMode.Json : RewriteMode.Passthrough;
    }

    public void Process(ReadOnlySpan<byte> chunk, IBufferWriter<byte> output)
    {
        switch (_mode)
        {
            case RewriteMode.Passthrough:
                output.Write(chunk);
                return;

            case RewriteMode.Json:
                if (_pending.WrittenCount + chunk.Length > _maxBuffer)
                {
                    // Too large to rewrite in memory: give up on rewriting, never on delivering.
                    output.Write(_pending.WrittenSpan);
                    output.Write(chunk);
                    ClearPending();
                    _mode = RewriteMode.Passthrough;
                    return;
                }

                _pending.Write(chunk);
                return;

            default:
                ProcessLines(chunk, output);
                return;
        }
    }

    public void Complete(IBufferWriter<byte> output)
    {
        if (_pending.WrittenCount == 0)
        {
            return;
        }

        switch (_mode)
        {
            case RewriteMode.Json:
                WriteJson(_pending.WrittenSpan, output, rawOnFailure: true);
                break;
            case RewriteMode.Ndjson:
            case RewriteMode.Sse:
                ProcessLine(_pending.WrittenSpan, output, newline: false);
                break;
            default:
                output.Write(_pending.WrittenSpan);
                break;
        }

        ClearPending();
    }

    private void ProcessLines(ReadOnlySpan<byte> chunk, IBufferWriter<byte> output)
    {
        while (!chunk.IsEmpty)
        {
            var newline = chunk.IndexOf((byte)'\n');
            if (newline < 0)
            {
                if (_pending.WrittenCount + chunk.Length > _maxBuffer)
                {
                    output.Write(_pending.WrittenSpan);
                    output.Write(chunk);
                    ClearPending();
                    _mode = RewriteMode.Passthrough;
                    return;
                }

                _pending.Write(chunk);
                return;
            }

            var part = chunk[..newline];
            if (_pending.WrittenCount > 0)
            {
                _pending.Write(part);
                ProcessLine(_pending.WrittenSpan, output, newline: true);
                ClearPending();
            }
            else
            {
                ProcessLine(part, output, newline: true);
            }

            chunk = chunk[(newline + 1)..];
        }
    }

    private void ProcessLine(ReadOnlySpan<byte> line, IBufferWriter<byte> output, bool newline)
    {
        var carriage = line.Length > 0 && line[^1] == '\r';
        var content = carriage ? line[..^1] : line;

        if (_mode == RewriteMode.Ndjson)
        {
            if (content.TrimStart(" \t"u8).StartsWith("{"u8))
            {
                WriteJson(content, output, rawOnFailure: true);
            }
            else
            {
                output.Write(content);
            }
        }
        else if (content.StartsWith(DataPrefix))
        {
            var payload = content[DataPrefix.Length..];
            if (payload.StartsWith(" "u8))
            {
                payload = payload[1..];
            }

            if (payload.StartsWith("{"u8))
            {
                output.Write("data: "u8);
                WriteJson(payload, output, rawOnFailure: true);
            }
            else
            {
                output.Write(content);
            }
        }
        else
        {
            output.Write(content);
        }

        if (carriage)
        {
            output.Write("\r"u8);
        }

        if (newline)
        {
            output.Write("\n"u8);
        }
    }

    private void WriteJson(ReadOnlySpan<byte> json, IBufferWriter<byte> output, bool rawOnFailure)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            node = null;
        }

        if (node is not JsonObject obj)
        {
            if (rawOnFailure)
            {
                output.Write(json);
            }

            return;
        }

        RewriteModel(obj);
        ReadUsage(obj);
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            SkipValidation = true,
        });
        obj.WriteTo(writer, RequestRewriter.OutputOptions);
    }

    private void RewriteModel(JsonObject obj)
    {
        if (obj["model"] is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            obj["model"] = _alias;
        }

        // OpenAI Responses API events nest the response object.
        if (obj["response"] is JsonObject response && response["model"] is JsonValue rv
                                                    && rv.GetValueKind() == JsonValueKind.String)
        {
            response["model"] = _alias;
        }
    }

    private void ReadUsage(JsonObject obj)
    {
        // Ollama native: final chunk of chat/generate, and embed responses.
        if (Long(obj["prompt_eval_count"]) is { } p)
        {
            PromptTokens = p;
        }

        if (Long(obj["eval_count"]) is { } e)
        {
            CompletionTokens = e;
        }

        var usage = obj["usage"] as JsonObject ?? (obj["response"] as JsonObject)?["usage"] as JsonObject;
        if (usage is not null)
        {
            if ((Long(usage["prompt_tokens"]) ?? Long(usage["input_tokens"])) is { } pt)
            {
                PromptTokens = pt;
            }

            if ((Long(usage["completion_tokens"]) ?? Long(usage["output_tokens"])) is { } ct)
            {
                CompletionTokens = ct;
            }
        }
    }

    private static long? Long(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<long>(out var l) ? l : null;

    private void ClearPending()
    {
        _pending.Clear();
        if (_pending.Capacity > 1024 * 1024)
        {
            _pending.ResetWrittenCount();
        }
    }
}
