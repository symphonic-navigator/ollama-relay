using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using MongoDB.Driver;
using OllamaRelay.Protocol;
using OllamaRelay.Relay.Data;
using OllamaRelay.Relay.Nodes;

namespace OllamaRelay.Relay.Inference;

/// <summary>
/// Serves /api/* and /v1/*. A thin pass-through: resolve alias, pick a node, rewrite model names,
/// stream the answer back, meter metadata. Request and response bodies live only in memory.
/// </summary>
public sealed class InferenceHandler(
    RelayOptions options,
    ApiKeyAuth auth,
    Catalog catalog,
    Scheduler scheduler,
    NodeHub hub,
    ActiveStreams active,
    UsageService usage,
    MongoStore store,
    ILogger<InferenceHandler> logger)
{
    private const string FallbackOllamaVersion = "0.12.0";

    public static void Map(IEndpointRouteBuilder app)
    {
        foreach (var spec in InferenceEndpoints.Forwarded)
        {
            app.MapMethods(spec.Path, [spec.Method],
                    (HttpContext http, InferenceHandler h) => h.ForwardAsync(http, spec))
                .RequireCors("inference");
        }

        foreach (var spec in InferenceEndpoints.Local)
        {
            app.MapMethods(spec.Path, [spec.Method],
                (HttpContext http, InferenceHandler h) => h.LocalAsync(http, spec, null)).RequireCors("inference");
        }

        app.MapGet("/v1/models/{**model}",
            (HttpContext http, string model, InferenceHandler h) =>
                h.LocalAsync(http, new EndpointSpec("GET", "/v1/models/{model}", Dialect.OpenAi, null, Synthesized.Model),
                    model)).RequireCors("inference");

        // Everything else under the inference prefixes does not exist.
        app.Map("/api/{**rest}", (HttpContext http) => Errors.Write(http, Dialect.Ollama, 404, "not found"));
        app.Map("/v1/{**rest}", (HttpContext http) => Errors.Write(http, Dialect.OpenAi, 404, "not found"));
    }

    private async Task<ApiCaller?> AuthenticateAsync(HttpContext http, Dialect dialect)
    {
        var (status, caller) = await auth.AuthenticateAsync(http);
        switch (status)
        {
            case ApiAuthStatus.Ok:
                return caller;
            case ApiAuthStatus.Blocked:
                http.Response.Headers.RetryAfter = "300";
                await Errors.Write(http, dialect, 429, "too many failed authentication attempts");
                return null;
            default:
                http.Response.Headers.WWWAuthenticate = "Bearer";
                await Errors.Write(http, dialect, 401, "missing or invalid API key");
                return null;
        }
    }

    private async Task LocalAsync(HttpContext http, EndpointSpec spec, string? model)
    {
        var caller = await AuthenticateAsync(http, spec.Dialect);
        if (caller is null)
        {
            return;
        }

        var ct = http.RequestAborted;
        if (spec.Synthesized == Synthesized.Version)
        {
            var version = hub.All.Select(c => c.LastHeartbeat?.OllamaVersion).Where(v => v is not null)
                .Select(v => v!).OrderByDescending(v => Version.TryParse(v.Split('-')[0], out var p) ? p : new Version())
                .FirstOrDefault() ?? FallbackOllamaVersion;
            await http.Response.WriteAsJsonAsync(new { version }, ct);
            return;
        }

        var publications = await catalog.AllGrantedAsync(caller.User.Id, ct);
        var nodeIds = publications.Select(p => p.NodeId).Distinct().ToList();
        var nodes = (await store.Nodes.Find(n => nodeIds.Contains(n.Id)).ToListAsync(ct)).ToDictionary(n => n.Id);
        var byAlias = publications.GroupBy(p => p.Alias).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();

        switch (spec.Synthesized)
        {
            case Synthesized.Tags:
                await http.Response.WriteAsJsonAsync(new
                {
                    models = byAlias.Select(g =>
                    {
                        var (pub, reported) = BestReported(g, nodes);
                        return new
                        {
                            name = g.Key + ":latest",
                            model = g.Key + ":latest",
                            modified_at = (reported?.ModifiedAt ?? pub.CreatedAt).ToString("O"),
                            size = reported?.Size ?? 0,
                            digest = FakeDigest(g.Key),
                            details = Details(reported),
                        };
                    }),
                }, ct);
                return;

            case Synthesized.Ps:
                var loaded = new List<object>();
                foreach (var g in byAlias)
                {
                    foreach (var pub in g)
                    {
                        var conn = hub.Get(pub.NodeId);
                        var lm = conn?.LastHeartbeat?.Loaded.FirstOrDefault(m => m.Name == pub.UpstreamModel);
                        if (lm is null)
                        {
                            continue;
                        }

                        nodes.TryGetValue(pub.NodeId, out var node);
                        var reported = node?.ReportedModels.FirstOrDefault(r => r.Name == pub.UpstreamModel);
                        loaded.Add(new
                        {
                            name = g.Key + ":latest",
                            model = g.Key + ":latest",
                            size = lm.Size,
                            size_vram = lm.SizeVram,
                            digest = FakeDigest(g.Key),
                            details = Details(reported),
                            expires_at = lm.ExpiresAt?.ToString("O"),
                            context_length = lm.ContextLength,
                        });
                    }
                }

                await http.Response.WriteAsJsonAsync(new { models = loaded }, ct);
                return;

            case Synthesized.Models:
                await http.Response.WriteAsJsonAsync(new
                {
                    @object = "list",
                    data = byAlias.Select(g => OpenAiModel(g.Key, g.Min(p => p.CreatedAt))),
                }, ct);
                return;

            case Synthesized.Model:
                var alias = ModelNames.Normalize(model);
                var match = byAlias.FirstOrDefault(g => g.Key == alias);
                if (match is null)
                {
                    await Errors.ModelNotFound(http, Dialect.OpenAi, model ?? "");
                    return;
                }

                await http.Response.WriteAsJsonAsync(OpenAiModel(match.Key, match.Min(p => p.CreatedAt)), ct);
                return;
        }
    }

    private static object OpenAiModel(string alias, DateTime created) => new
    {
        id = alias,
        @object = "model",
        created = new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc)).ToUnixTimeSeconds(),
        owned_by = "ollama-relay",
    };

    private static (PublicationDoc, ReportedModel?) BestReported(IGrouping<string, PublicationDoc> g,
        Dictionary<MongoDB.Bson.ObjectId, NodeDoc> nodes)
    {
        foreach (var pub in g)
        {
            if (nodes.TryGetValue(pub.NodeId, out var node) &&
                node.ReportedModels.FirstOrDefault(r => r.Name == pub.UpstreamModel) is { } reported)
            {
                return (pub, reported);
            }
        }

        return (g.First(), null);
    }

    private static object Details(ReportedModel? r) => new
    {
        parent_model = "",
        format = "gguf",
        family = r?.Family ?? "",
        families = r?.Family is { } f ? new[] { f } : null,
        parameter_size = r?.ParameterSize ?? "",
        quantization_level = r?.QuantizationLevel ?? "",
    };

    private static string FakeDigest(string alias) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("ollama-relay:" + alias)));

    private async Task ForwardAsync(HttpContext http, EndpointSpec spec)
    {
        var caller = await AuthenticateAsync(http, spec.Dialect);
        if (caller is null)
        {
            return;
        }

        var bodyFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyFeature is { IsReadOnly: false })
        {
            bodyFeature.MaxRequestBodySize = options.MaxRequestBodyBytes;
        }

        byte[] raw;
        try
        {
            raw = await ReadBodyAsync(http.Request.BodyReader, options.MaxRequestBodyBytes, http.RequestAborted);
        }
        catch (BadHttpRequestException)
        {
            await Errors.Write(http, spec.Dialect, 413, "request body too large");
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        byte[]? forwarded = null;
        try
        {
            var json = RequestRewriter.TryParse(raw);
            if (json is null)
            {
                await Errors.Write(http, spec.Dialect, 400, "request body must be a JSON object");
                return;
            }

            var requested = RequestRewriter.RequestedModel(json);
            var alias = ModelNames.Normalize(requested);
            var publications = alias is null
                ? []
                : await catalog.GrantedPublicationsAsync(caller.User.Id, alias, http.RequestAborted);
            if (alias is null || publications.Count == 0)
            {
                await Errors.ModelNotFound(http, spec.Dialect, requested ?? "");
                return;
            }

            var entry = new ActiveStream
            {
                UserId = caller.User.Id,
                Username = caller.User.Username,
                ApiKeyId = caller.Key.Id,
                Alias = alias,
                Endpoint = spec.Path,
            };
            if (!active.TryAdd(entry, options.UserMaxConcurrentStreams))
            {
                http.Response.Headers.RetryAfter = "5";
                await Errors.Write(http, spec.Dialect, 429, "too many concurrent requests for this user");
                return;
            }

            try
            {
                await RunAsync(http, spec, caller, alias, requested!, json, publications, entry,
                    b => forwarded = b);
            }
            finally
            {
                active.Remove(entry);
            }
        }
        finally
        {
            Array.Clear(raw);
            if (forwarded is not null)
            {
                Array.Clear(forwarded);
            }
        }
    }

    private async Task RunAsync(HttpContext http, EndpointSpec spec, ApiCaller caller, string alias, string requested,
        System.Text.Json.Nodes.JsonObject json, IReadOnlyList<PublicationDoc> publications, ActiveStream entry,
        Action<byte[]> keepForClearing)
    {
        var started = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted,
            entry.Cancellation.Token);
        var ct = linked.Token;

        AcquireResult acquired;
        try
        {
            acquired = await scheduler.AcquireAsync(() => publications
                    .Select(p => (p, c: hub.Get(p.NodeId)))
                    .Where(x => x.c is not null)
                    .Select(x => new Candidate(x.p, x.c!))
                    .ToList(),
                options.QueueMaxLength, options.QueueTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        switch (acquired.Status)
        {
            case AcquireStatus.NoCandidate:
                await Errors.Write(http, spec.Dialect, 503, "no node serving this model is online");
                return;
            case AcquireStatus.QueueFull:
            case AcquireStatus.Timeout:
                http.Response.Headers.RetryAfter = "10";
                await Errors.Write(http, spec.Dialect, 503, "all nodes serving this model are busy");
                return;
        }

        using var lease = acquired.Lease!;
        var candidate = lease.Candidate;
        var connection = candidate.Node;
        entry.NodeId = connection.NodeId;
        entry.NodeName = connection.NodeName;
        entry.Queued = false;

        var body = RequestRewriter.Rewrite(json, candidate.Publication.UpstreamModel, spec, options.StripKeepAlive);
        keepForClearing(body);

        var outcome = "ok";
        var status = 0;
        long? ttfb = null;
        ResponseRewriter? rewriter = null;
        var stream = connection.OpenStream();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Closed);
        total.CancelAfter(options.StreamTotalTimeout);
        try
        {
            await stream.SendRequestAsync(new RequestHeadMessage(spec.Method, spec.Path, "application/json"), body,
                total.Token);

            var first = await ReadWithIdleTimeout(stream, total.Token);
            if (first.Kind != StreamEventKind.Head)
            {
                outcome = "error";
                status = first.Code == ErrorCodes.NotAllowed ? 404 : 502;
                await Errors.Write(http, spec.Dialect, status, status == 404 ? "not found" : "upstream node failed");
                return;
            }

            status = first.Status;
            var mode = ResponseRewriter.ModeFor(first.Status, first.ContentType);
            rewriter = new ResponseRewriter(mode, requested, options.MaxRewriteBytes);
            http.Response.StatusCode = first.Status;
            if (first.ContentType is not null)
            {
                http.Response.ContentType = first.ContentType;
            }

            if (mode is RewriteMode.Ndjson or RewriteMode.Sse)
            {
                http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                http.Response.Headers.CacheControl = "no-cache";
                http.Response.Headers["X-Accel-Buffering"] = "no";
            }

            var output = http.Response.BodyWriter;
            while (true)
            {
                var ev = await ReadWithIdleTimeout(stream, total.Token);
                if (ev.Kind == StreamEventKind.Body)
                {
                    rewriter.Process(ev.Data.Span, output);
                    ttfb ??= clock.ElapsedMilliseconds;
                    await output.FlushAsync(total.Token);
                }
                else if (ev.Kind == StreamEventKind.End)
                {
                    rewriter.Complete(output);
                    await output.FlushAsync(total.Token);
                    break;
                }
                else
                {
                    outcome = ev.Code == CancelReasons.Overflow ? "cancelled" : "error";
                    await EndWithErrorAsync(http, spec.Dialect, mode, "upstream node failed during the response");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (http.RequestAborted.IsCancellationRequested)
            {
                outcome = "cancelled";
                stream.Cancel(CancelReasons.ClientGone);
            }
            else if (entry.Cancellation.IsCancellationRequested)
            {
                outcome = "cancelled";
                stream.Cancel(entry.CancelReason ?? CancelReasons.Revoked);
                http.Abort();
            }
            else if (connection.Closed.IsCancellationRequested)
            {
                outcome = "error";
                await TryEndAsync(http, spec.Dialect, rewriter, 502, "upstream node disconnected");
            }
            else
            {
                outcome = "timeout";
                stream.Cancel(CancelReasons.Timeout);
                await TryEndAsync(http, spec.Dialect, rewriter, 504, "upstream timed out");
            }
        }
        catch (Exception ex) when (ex is IOException or System.Threading.Channels.ChannelClosedException
                                       or InvalidOperationException)
        {
            outcome = "error";
            stream.Cancel(CancelReasons.ClientGone);
        }
        finally
        {
            if (!stream.IsClosed)
            {
                stream.Cancel(CancelReasons.ClientGone);
            }

            var duration = clock.ElapsedMilliseconds;
            // Metadata only. Never request or response content.
            logger.LogInformation(
                "{Endpoint} user={User} alias={Alias} node={Node} status={Status} outcome={Outcome} wait={WaitMs}ms ttfb={TtfbMs}ms total={TotalMs}ms",
                spec.Path, caller.User.Username, alias, connection.NodeName, status, outcome,
                (long)acquired.Waited.TotalMilliseconds, ttfb, duration);

            if (spec.Kind is not null)
            {
                await usage.RecordAsync(new UsageRecordDoc
                {
                    UserId = caller.User.Id,
                    Username = caller.User.Username,
                    ApiKeyId = caller.Key.Id,
                    NodeId = connection.NodeId,
                    NodeName = connection.NodeName,
                    Alias = alias,
                    UpstreamModel = candidate.Publication.UpstreamModel,
                    Endpoint = spec.Path,
                    Kind = spec.Kind,
                    StartedAt = started,
                    QueueWaitMs = (long)acquired.Waited.TotalMilliseconds,
                    TimeToFirstByteMs = ttfb,
                    DurationMs = duration,
                    PromptTokens = rewriter?.PromptTokens,
                    CompletionTokens = rewriter?.CompletionTokens,
                    Outcome = outcome == "ok" && status >= 400 ? "error" : outcome,
                    Status = status,
                });
            }
        }
    }

    private async Task<StreamEvent> ReadWithIdleTimeout(RelayStream stream, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(options.StreamIdleTimeout);
        return await stream.ReadAsync(idle.Token);
    }

    private static async Task TryEndAsync(HttpContext http, Dialect dialect, ResponseRewriter? rewriter, int status,
        string message)
    {
        try
        {
            if (!http.Response.HasStarted)
            {
                await Errors.Write(http, dialect, status, message);
            }
            else
            {
                await EndWithErrorAsync(http, dialect, rewriter is null ? RewriteMode.Passthrough : RewriteMode.Ndjson,
                    message);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            http.Abort();
        }
    }

    /// <summary>Ends a response that already started: an error line for streams, a dropped connection otherwise.</summary>
    private static async Task EndWithErrorAsync(HttpContext http, Dialect dialect, RewriteMode mode, string message)
    {
        if (!http.Response.HasStarted)
        {
            await Errors.Write(http, dialect, 502, message);
            return;
        }

        var contentType = http.Response.ContentType ?? "";
        if (contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var payload = JsonSerializer.Serialize(new { error = new { message, type = "server_error" } });
            await http.Response.WriteAsync("data: " + payload + "\n\n");
            await http.Response.CompleteAsync();
        }
        else if (contentType.Contains("ndjson", StringComparison.OrdinalIgnoreCase))
        {
            await http.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }) + "\n");
            await http.Response.CompleteAsync();
        }
        else
        {
            http.Abort();
        }

        _ = mode;
    }

    private static async Task<byte[]> ReadBodyAsync(PipeReader reader, long limit, CancellationToken ct)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(ct);
                foreach (var segment in result.Buffer)
                {
                    if (buffer.WrittenCount + segment.Length > limit)
                    {
                        throw new BadHttpRequestException("too large", 413);
                    }

                    buffer.Write(segment.Span);
                }

                reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    return buffer.WrittenSpan.ToArray();
                }
            }
        }
        finally
        {
            buffer.Clear();
        }
    }
}

public static class Errors
{
    public static async Task Write(HttpContext http, Dialect dialect, int status, string message)
    {
        if (http.Response.HasStarted)
        {
            return;
        }

        http.Response.StatusCode = status;
        if (dialect == Dialect.OpenAi)
        {
            var type = status switch
            {
                400 or 413 => "invalid_request_error",
                401 => "authentication_error",
                404 => "invalid_request_error",
                429 => "rate_limit_error",
                _ => "server_error",
            };
            await http.Response.WriteAsJsonAsync(new { error = new { message, type, code = (string?)null } });
        }
        else
        {
            await http.Response.WriteAsJsonAsync(new { error = message });
        }
    }

    public static async Task ModelNotFound(HttpContext http, Dialect dialect, string requested)
    {
        var shown = requested.Length > 100 ? requested[..100] : requested;
        if (http.Response.HasStarted)
        {
            return;
        }

        http.Response.StatusCode = 404;
        if (dialect == Dialect.OpenAi)
        {
            await http.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message = $"The model '{shown}' does not exist",
                    type = "invalid_request_error",
                    param = "model",
                    code = "model_not_found",
                },
            });
        }
        else
        {
            await http.Response.WriteAsJsonAsync(new { error = $"model '{shown}' not found" });
        }
    }
}
