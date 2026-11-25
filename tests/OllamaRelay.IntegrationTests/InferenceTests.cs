using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using OllamaRelay.Protocol;

namespace OllamaRelay.IntegrationTests;

[Collection("relay")]
public sealed class InferenceTests(MongoFixture mongo) : IAsyncLifetime
{
    private Stack _stack = null!;
    private Stack.UserHandle _alice = null!;

    public async ValueTask InitializeAsync()
    {
        _stack = await Stack.StartAsync(mongo);
        await _stack.StartNodeAsync();
        await _stack.PublishAsync("fake-model:7b", "lit-writer");
        await _stack.PublishAsync("fake-slow:1b", "slow");
        await _stack.PublishAsync("fake-fail:1b", "broken");
        await _stack.PublishAsync("hf.co/other/hidden:Q4", "secret");
        _alice = await _stack.CreateUserAsync("alice", "lit-writer", "slow", "broken");
    }

    public async ValueTask DisposeAsync() => await _stack.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpRequestMessage Api(HttpMethod method, string path, object? body = null, string? key = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key ?? _alice.ApiKey);
        return req;
    }

    private async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> Call(HttpMethod method,
        string path, object? body = null, string? key = null)
    {
        using var req = Api(method, path, body, key);
        var res = await _stack.Http.SendAsync(req, Ct);
        return (res.StatusCode, await res.Content.ReadAsStringAsync(Ct), res);
    }

    [Fact]
    public async Task Native_chat_streams_ndjson_with_alias_rewritten_both_ways()
    {
        var (status, body, res) = await Call(HttpMethod.Post, "/api/chat", new
        {
            model = "lit-writer",
            keep_alive = -1,
            messages = new[] { new { role = "user", content = "hello" } },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("application/x-ndjson", res.Content.Headers.ContentType?.MediaType);
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.True(lines.Count >= 3);
        Assert.All(lines, l => Assert.Equal("lit-writer", (string?)l["model"]));
        Assert.DoesNotContain("fake-model", body);

        var upstream = _stack.Ollama.Requests.Last(r => r.Path == "POST /api/chat").Body!;
        Assert.Equal("fake-model:7b", (string?)upstream["model"]);
        Assert.False(upstream.ContainsKey("keep_alive"));

        await Stack.WaitForAsync(async () =>
            await _stack.Store.UsageRecords.Find(r => r.Alias == "lit-writer").AnyAsync(Ct));
        var record = await _stack.Store.UsageRecords.Find(r => r.Alias == "lit-writer").FirstAsync(Ct);
        Assert.Equal("ok", record.Outcome);
        Assert.Equal(11, record.PromptTokens);
        Assert.Equal(2, record.CompletionTokens);
        Assert.Equal("chat", record.Kind);
        Assert.Equal("alice", record.Username);
        Assert.Equal("diimitra", record.NodeName);
    }

    [Fact]
    public async Task Latest_suffix_is_accepted_and_echoed()
    {
        var (status, body, _) = await Call(HttpMethod.Post, "/api/chat", new
        {
            model = "lit-writer:latest",
            stream = false,
            messages = new[] { new { role = "user", content = "x" } },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("lit-writer:latest", (string?)JsonNode.Parse(body)!["model"]);
    }

    [Fact]
    public async Task OpenAi_chat_streams_sse_with_usage_and_alias()
    {
        var (status, body, res) = await Call(HttpMethod.Post, "/v1/chat/completions", new
        {
            model = "lit-writer",
            stream = true,
            messages = new[] { new { role = "user", content = "hello" } },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith("data: [DONE]\n\n", body);
        var events = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(e => e.StartsWith("data: {", StringComparison.Ordinal))
            .Select(e => JsonNode.Parse(e["data: ".Length..])!).ToList();
        Assert.All(events, e => Assert.Equal("lit-writer", (string?)e["model"]));
        Assert.Contains(events, e => e["usage"] is not null);

        var upstream = _stack.Ollama.Requests.Last(r => r.Path == "POST /v1/chat/completions").Body!;
        Assert.True((bool)upstream["stream_options"]!["include_usage"]!);
        Assert.Equal("fake-model:7b", (string?)upstream["model"]);
    }

    [Fact]
    public async Task OpenAi_non_streaming_and_embeddings()
    {
        var (status, body, _) = await Call(HttpMethod.Post, "/v1/chat/completions", new
        {
            model = "lit-writer",
            messages = new[] { new { role = "user", content = "hello" } },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("lit-writer", (string?)JsonNode.Parse(body)!["model"]);

        (status, body, _) = await Call(HttpMethod.Post, "/v1/embeddings", new { model = "lit-writer", input = "x" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("lit-writer", (string?)JsonNode.Parse(body)!["model"]);

        (status, body, _) = await Call(HttpMethod.Post, "/api/embed", new { model = "lit-writer", input = "x" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("lit-writer", (string?)JsonNode.Parse(body)!["model"]);
    }

    [Fact]
    public async Task Model_lists_show_only_granted_aliases()
    {
        var (_, tags, _) = await Call(HttpMethod.Get, "/api/tags");
        var names = JsonNode.Parse(tags)!["models"]!.AsArray().Select(m => (string?)m!["name"]).ToList();
        Assert.Equal(["broken:latest", "lit-writer:latest", "slow:latest"], names);
        Assert.DoesNotContain("fake-model", tags);
        Assert.DoesNotContain("secret", tags);

        var (_, models, _) = await Call(HttpMethod.Get, "/v1/models");
        var ids = JsonNode.Parse(models)!["data"]!.AsArray().Select(m => (string?)m!["id"]).ToList();
        Assert.Equal(["broken", "lit-writer", "slow"], ids);

        var (single, _, _) = await Call(HttpMethod.Get, "/v1/models/lit-writer");
        Assert.Equal(HttpStatusCode.OK, single);
        var (hidden, _, _) = await Call(HttpMethod.Get, "/v1/models/secret");
        Assert.Equal(HttpStatusCode.NotFound, hidden);

        var (_, ps, _) = await Call(HttpMethod.Get, "/api/ps");
        var loaded = JsonNode.Parse(ps)!["models"]!.AsArray();
        Assert.Single(loaded);
        Assert.Equal("lit-writer:latest", (string?)loaded[0]!["name"]);

        var (_, version, _) = await Call(HttpMethod.Get, "/api/version");
        Assert.Equal("0.12.6", (string?)JsonNode.Parse(version)!["version"]);
    }

    [Fact]
    public async Task Unknown_and_ungranted_models_look_the_same()
    {
        var (s1, b1, _) = await Call(HttpMethod.Post, "/api/chat", new { model = "secret", messages = Array.Empty<object>() });
        var (s2, b2, _) = await Call(HttpMethod.Post, "/api/chat", new { model = "nonexistent", messages = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, s1);
        Assert.Equal(HttpStatusCode.NotFound, s2);
        Assert.Equal(b1.Replace("secret", "X"), b2.Replace("nonexistent", "X"));

        // Upstream names are not addressable either.
        var (s3, _, _) = await Call(HttpMethod.Post, "/api/chat", new { model = "fake-model:7b", messages = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, s3);

        var (s4, b4, _) = await Call(HttpMethod.Post, "/v1/chat/completions", new { model = "secret", messages = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NotFound, s4);
        Assert.Equal("model_not_found", (string?)JsonNode.Parse(b4)!["error"]!["code"]);
        Assert.DoesNotContain(_stack.Ollama.Requests, r => (string?)r.Body?["model"] == "hf.co/other/hidden:Q4");
    }

    [Theory]
    [InlineData("POST", "/api/pull")]
    [InlineData("DELETE", "/api/delete")]
    [InlineData("POST", "/api/create")]
    [InlineData("POST", "/api/copy")]
    [InlineData("POST", "/api/push")]
    [InlineData("GET", "/api/chat")]
    [InlineData("POST", "/v1/files")]
    [InlineData("POST", "/api/blobs/sha256:abc")]
    public async Task Relay_allowlist_blocks_management_endpoints(string method, string path)
    {
        var (status, _, _) = await Call(new HttpMethod(method), path, new { model = "lit-writer", name = "lit-writer" });
        Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{status}");
        Assert.DoesNotContain(_stack.Ollama.Requests, r => r.Path.Contains("pull") || r.Path.Contains("delete") ||
                                                           r.Path.Contains("create") || r.Path.Contains("copy") ||
                                                           r.Path.Contains("push") || r.Path.Contains("blobs"));
    }

    [Fact]
    public async Task Requests_need_a_valid_api_key()
    {
        var (s1, _, _) = await Call(HttpMethod.Get, "/api/tags", key: "orl_00000000_" + new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, s1);
        var (s2, _, _) = await Call(HttpMethod.Get, "/api/tags", key: _alice.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, s2);

        // API keys never work on the management API.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/mgmt/me/keys");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _alice.ApiKey);
        using var res = await _stack.Http.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Client_disconnect_cancels_the_upstream_call()
    {
        _stack.Ollama.ResetSlow();
        using var cts = new CancellationTokenSource();
        using var req = Api(HttpMethod.Post, "/api/chat",
            new { model = "slow", messages = new[] { new { role = "user", content = "go" } } });
        var res = await _stack.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var stream = await res.Content.ReadAsStreamAsync(Ct);
        var buffer = new byte[256];
        Assert.True(await stream.ReadAsync(buffer, Ct) > 0);
        await _stack.Ollama.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await cts.CancelAsync();
        res.Dispose();

        await _stack.Ollama.SlowAborted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Stack.WaitForAsync(() => _stack.Agent!.ActiveStreams == 0);
        await Stack.WaitForAsync(async () =>
            await _stack.Store.UsageRecords.Find(r => r.Alias == "slow" && r.Outcome == "cancelled").AnyAsync(Ct));
    }

    [Fact]
    public async Task Queue_times_out_with_503_and_retry_after_when_node_is_full()
    {
        Assert.Equal(204, await _stack.Send(HttpMethod.Patch, $"/mgmt/admin/nodes/{_stack.NodeId}",
            new { maxConcurrency = 1 }, _stack.AdminToken));

        _stack.Ollama.ResetSlow();
        using var first = Api(HttpMethod.Post, "/api/chat",
            new { model = "slow", messages = new[] { new { role = "user", content = "hold" } } });
        using var held = await _stack.Http.SendAsync(first, HttpCompletionOption.ResponseHeadersRead, Ct);
        await _stack.Ollama.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var started = DateTime.UtcNow;
        var (status, _, res) = await Call(HttpMethod.Post, "/api/chat",
            new { model = "lit-writer", messages = new[] { new { role = "user", content = "wait" } } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.NotNull(res.Headers.RetryAfter);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task Node_reconnects_after_the_connection_drops()
    {
        var nodeId = ObjectId.Parse(_stack.NodeId);
        await _stack.Hub().DisconnectAsync(nodeId, ProtocolConstants.CloseReplaced, "test drop");
        Assert.Null(_stack.Hub().Get(nodeId));

        await Stack.WaitForAsync(() => _stack.Hub().Get(nodeId) is not null);
        var (status, _, _) = await Call(HttpMethod.Post, "/api/chat",
            new { model = "lit-writer", stream = false, messages = new[] { new { role = "user", content = "x" } } });
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task No_node_online_returns_503()
    {
        await _stack.StopAgentAsync();
        await Stack.WaitForAsync(() => _stack.Hub().All.Count == 0);
        var (status, _, _) = await Call(HttpMethod.Post, "/api/chat",
            new { model = "lit-writer", messages = new[] { new { role = "user", content = "x" } } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
    }

    [Fact]
    public async Task Revoking_an_api_key_stops_running_streams_immediately()
    {
        _stack.Ollama.ResetSlow();
        using var req = Api(HttpMethod.Post, "/api/chat",
            new { model = "slow", messages = new[] { new { role = "user", content = "go" } } });
        using var res = await _stack.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, Ct);
        await _stack.Ollama.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var reading = Task.Run(async () =>
        {
            try
            {
                await res.Content.ReadAsStringAsync(Ct);
            }
            catch (HttpRequestException)
            {
            }
            catch (IOException)
            {
            }
        }, Ct);

        Assert.Equal(204, await _stack.Send(HttpMethod.Delete, $"/mgmt/me/keys/{_alice.ApiKeyId}", null, _alice.Token));

        await _stack.Ollama.SlowAborted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await reading.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var (status, _, _) = await Call(HttpMethod.Get, "/api/tags");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Disabling_a_user_stops_streams_and_keys()
    {
        _stack.Ollama.ResetSlow();
        using var req = Api(HttpMethod.Post, "/v1/chat/completions",
            new { model = "slow", stream = true, messages = new[] { new { role = "user", content = "go" } } });
        using var res = await _stack.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, Ct);
        await _stack.Ollama.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(204, await _stack.Send(HttpMethod.Patch, $"/mgmt/admin/users/{_alice.Id}",
            new { disabled = true }, _stack.AdminToken));
        await _stack.Ollama.SlowAborted.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var (status, _, _) = await Call(HttpMethod.Get, "/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        // The web session is gone too.
        await _stack.GetJson("/mgmt/me", _alice.Token, 401);
    }

    [Fact]
    public async Task Upstream_errors_pass_through_to_the_client()
    {
        var (status, body, _) = await Call(HttpMethod.Post, "/api/chat",
            new { model = "broken", messages = new[] { new { role = "user", content = "boom" } } });
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Contains("boom", body);
    }

    [Fact]
    public async Task Per_user_concurrency_limit_returns_429()
    {
        await using var stack = await Stack.StartAsync(mongo, new() { ["USER_MAX_CONCURRENT_STREAMS"] = "1" });
        await stack.StartNodeAsync();
        await stack.PublishAsync("fake-slow:1b", "slow");
        var bob = await stack.CreateUserAsync("bob", "slow");

        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { model = "slow", messages = new[] { new { role = "user", content = "a" } } }),
        };
        first.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bob.ApiKey);
        using var held = await stack.Http.SendAsync(first, HttpCompletionOption.ResponseHeadersRead, Ct);

        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { model = "slow", messages = new[] { new { role = "user", content = "b" } } }),
        };
        second.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bob.ApiKey);
        using var res = await stack.Http.SendAsync(second, Ct);
        Assert.Equal((HttpStatusCode)429, res.StatusCode);
    }
}
