using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllamaRelay.IntegrationTests;

/// <summary>
/// Stand-in for Ollama. Echoes the prompt back like a model would, so the canary test has content to leak.
/// Model "fake-slow" streams until the caller goes away; model "fake-fail" returns a 500 that quotes the prompt.
/// </summary>
public sealed class FakeOllama : IAsyncDisposable
{
    private readonly WebApplication _app;

    public ConcurrentQueue<(string Path, JsonObject? Body)> Requests { get; } = new();

    public TaskCompletionSource SlowAborted { get; private set; } = NewTcs();

    public TaskCompletionSource SlowStarted { get; private set; } = NewTcs();

    public List<string> Models { get; } = ["fake-model:7b", "fake-slow:1b", "fake-fail:1b", "hf.co/other/hidden:Q4"];

    public List<string> Loaded { get; } = ["fake-model:7b"];

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ResetSlow()
    {
        SlowAborted = NewTcs();
        SlowStarted = NewTcs();
    }

    private FakeOllama(WebApplication app) => _app = app;

    public Uri Url => new(_app.Urls.First());

    public static async Task<FakeOllama> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeOllama(app);
        fake.Map(app);
        await app.StartAsync();
        return fake;
    }

    private void Map(WebApplication app)
    {
        app.MapGet("/api/version", () => Results.Ok(new { version = "0.12.6" }));
        app.MapGet("/api/tags", () => Results.Ok(new
        {
            models = Models.Select(m => new
            {
                name = m,
                model = m,
                size = 1234,
                digest = "abc",
                modified_at = DateTimeOffset.UtcNow,
                details = new { family = "llama", parameter_size = "7B", quantization_level = "Q4_K_M" },
            }),
        }));
        app.MapGet("/api/ps", () => Results.Ok(new
        {
            models = Loaded.Select(m => new
            {
                name = m, model = m, size = 1000, size_vram = 900, expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                context_length = 4096,
            }),
        }));

        app.MapPost("/api/chat", async (HttpContext http) => await Chat(http, openAi: false));
        app.MapPost("/api/generate", async (HttpContext http) => await Chat(http, openAi: false));
        app.MapPost("/v1/chat/completions", async (HttpContext http) => await Chat(http, openAi: true));
        app.MapPost("/api/embed", async (HttpContext http) =>
        {
            var body = await Record(http);
            return Results.Ok(new { model = (string?)body?["model"], embeddings = new[] { new[] { 0.1, 0.2 } }, prompt_eval_count = 3 });
        });
        app.MapPost("/v1/embeddings", async (HttpContext http) =>
        {
            var body = await Record(http);
            return Results.Ok(new
            {
                @object = "list", model = (string?)body?["model"],
                data = new[] { new { @object = "embedding", index = 0, embedding = new[] { 0.1 } } },
                usage = new { prompt_tokens = 3, total_tokens = 3 },
            });
        });

        // Management endpoints exist on a real Ollama. The node must never forward to them.
        app.MapFallback(async (HttpContext http) =>
        {
            await Record(http);
            return Results.Ok(new { status = "should never be reached" });
        });
    }

    private async Task<JsonObject?> Record(HttpContext http)
    {
        JsonObject? body = null;
        try
        {
            body = await JsonNode.ParseAsync(http.Request.Body) as JsonObject;
        }
        catch (JsonException)
        {
            // Not JSON.
        }

        Requests.Enqueue((http.Request.Method + " " + http.Request.Path, body));
        return body;
    }

    private static string PromptOf(JsonObject? body) =>
        body?["messages"] is JsonArray { Count: > 0 } messages
            ? (string?)messages[0]?["content"] ?? ""
            : (string?)body?["prompt"] ?? "";

    private async Task Chat(HttpContext http, bool openAi)
    {
        var body = await Record(http);
        var model = (string?)body?["model"] ?? "";
        var prompt = PromptOf(body);
        var stream = body?["stream"] is JsonValue v ? v.GetValue<bool>() : !openAi;

        if (model.StartsWith("fake-fail", StringComparison.Ordinal))
        {
            http.Response.StatusCode = 500;
            await http.Response.WriteAsJsonAsync(new { error = "model crashed while reading: " + prompt });
            return;
        }

        if (model.StartsWith("fake-slow", StringComparison.Ordinal))
        {
            http.Response.ContentType = openAi ? "text/event-stream" : "application/x-ndjson";
            SlowStarted.TrySetResult();
            try
            {
                while (!http.RequestAborted.IsCancellationRequested)
                {
                    var line = openAi
                        ? $"data: {JsonSerializer.Serialize(new { model, choices = new[] { new { delta = new { content = prompt } } } })}\n\n"
                        : JsonSerializer.Serialize(new { model, message = new { role = "assistant", content = prompt }, done = false }) + "\n";
                    await http.Response.WriteAsync(line, http.RequestAborted);
                    await http.Response.Body.FlushAsync(http.RequestAborted);
                    await Task.Delay(50, http.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }

            SlowAborted.TrySetResult();
            return;
        }

        var words = new[] { "echo:", prompt };
        if (openAi)
        {
            if (!stream)
            {
                await http.Response.WriteAsJsonAsync(new
                {
                    id = "c1", @object = "chat.completion", model,
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = string.Join(' ', words) }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 11, completion_tokens = 2, total_tokens = 13 },
                });
                return;
            }

            http.Response.ContentType = "text/event-stream";
            foreach (var w in words)
            {
                await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { id = "c1", @object = "chat.completion.chunk", model, choices = new[] { new { index = 0, delta = new { content = w } } } })}\n\n");
                await http.Response.Body.FlushAsync();
            }

            var includeUsage = body?["stream_options"]?["include_usage"] is JsonValue iu && iu.GetValue<bool>();
            if (includeUsage)
            {
                await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { id = "c1", @object = "chat.completion.chunk", model, choices = Array.Empty<object>(), usage = new { prompt_tokens = 11, completion_tokens = 2, total_tokens = 13 } })}\n\n");
            }

            await http.Response.WriteAsync("data: [DONE]\n\n");
            return;
        }

        if (!stream)
        {
            await http.Response.WriteAsJsonAsync(new
            {
                model, message = new { role = "assistant", content = string.Join(' ', words) }, done = true,
                prompt_eval_count = 11, eval_count = 2,
            });
            return;
        }

        http.Response.ContentType = "application/x-ndjson";
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            await http.Response.WriteAsync(JsonSerializer.Serialize(new { model, message = new { role = "assistant", content = w }, done = false }) + "\n");
            await http.Response.Body.FlushAsync();
        }

        await http.Response.WriteAsync(JsonSerializer.Serialize(new { model, message = new { role = "assistant", content = "" }, done = true, prompt_eval_count = 11, eval_count = 2 }) + "\n");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
