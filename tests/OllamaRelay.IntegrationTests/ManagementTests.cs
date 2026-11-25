using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using OllamaRelay.Protocol;

namespace OllamaRelay.IntegrationTests;

[Collection("relay")]
public sealed class ManagementTests(MongoFixture mongo) : IAsyncLifetime
{
    private Stack _stack = null!;

    public async ValueTask InitializeAsync() => _stack = await Stack.StartAsync(mongo);

    public async ValueTask DisposeAsync() => await _stack.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpStatusCode, string, HttpResponseMessage)> Post(string path, object body,
        string? cookie = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (cookie is not null)
        {
            req.Headers.Add("Cookie", cookie);
        }

        var res = await _stack.Http.SendAsync(req, Ct);
        return (res.StatusCode, await res.Content.ReadAsStringAsync(Ct), res);
    }

    private static string RefreshCookie(HttpResponseMessage res)
    {
        var header = res.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("orl_refresh=", StringComparison.Ordinal));
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/mgmt/auth", header, StringComparison.OrdinalIgnoreCase);
        return header.Split(';')[0];
    }

    [Fact]
    public async Task Unknown_user_and_wrong_password_look_the_same()
    {
        var (s1, b1, _) = await Post("/mgmt/auth/login", new { username = "nobody", password = "whatever-password" });
        var (s2, b2, _) = await Post("/mgmt/auth/login", new { username = "admin", password = "whatever-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, s1);
        Assert.Equal(s1, s2);
        Assert.Equal(b1, b2);
    }

    [Fact]
    public async Task Login_is_rate_limited()
    {
        HttpStatusCode last = 0;
        for (var i = 0; i < 12; i++)
        {
            (last, _, _) = await Post("/mgmt/auth/login", new { username = "admin", password = "wrong-password-x" });
        }

        Assert.Equal((HttpStatusCode)429, last);
    }

    [Fact]
    public async Task First_login_forces_a_password_change()
    {
        await using var fresh = await FreshStackWithoutBootstrap();
        var login = await fresh.PostJson("/mgmt/auth/login", new { username = "admin", password = Stack.AdminPassword });
        Assert.True((bool)login["user"]!["mustChangePassword"]!);
        var token = (string)login["accessToken"]!;
        await fresh.GetJson("/mgmt/admin/users", token, 403);
        await fresh.GetJson("/mgmt/me/keys", token, 403);
        await fresh.GetJson("/mgmt/me", token);
    }

    private async Task<Stack> FreshStackWithoutBootstrap()
    {
        // Stack.StartAsync changes the password; build a second one and reset the flag directly.
        var stack = await Stack.StartAsync(mongo);
        await stack.Store.Users.UpdateOneAsync(u => u.UsernameLower == "admin", Builders<Relay.Data.UserDoc>.Update
            .Set(u => u.MustChangePassword, true)
            .Set(u => u.PasswordHash, stack.Relay.Services.GetRequiredService<Relay.Security.PasswordHasher>()
                .Hash(Stack.AdminPassword)), cancellationToken: Ct);
        return stack;
    }

    [Fact]
    public async Task Invites_are_single_use()
    {
        var invite = await _stack.PostJson("/mgmt/admin/invites", new { }, _stack.AdminToken);
        var token = ((string)invite["link"]!).Split('#')[1];
        Assert.StartsWith("http://relay.test/invite#", (string)invite["link"]!);

        await _stack.PostJson("/mgmt/auth/invite/redeem", new { token, username = "dave", password = "short" }, expect: 400);
        await _stack.PostJson("/mgmt/auth/invite/redeem", new { token, username = "dave", password = "long-enough-pass" });
        await _stack.PostJson("/mgmt/auth/invite/redeem", new { token, username = "eve", password = "long-enough-pass" },
            expect: 404);

        // Stored hashed only.
        var stored = await _stack.Store.Invites.Find(FilterDefinition<Relay.Data.InviteDoc>.Empty).FirstAsync(Ct);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.DoesNotContain(token, stored.ToBsonDocument().ToJson());
    }

    [Fact]
    public async Task Reset_link_sets_password_and_ends_sessions()
    {
        var user = await _stack.CreateUserAsync("frank");
        var reset = await _stack.PostJson($"/mgmt/admin/users/{user.Id}/reset", new { }, _stack.AdminToken);
        var token = ((string)reset["link"]!).Split('#')[1];

        var check = await _stack.PostJson("/mgmt/auth/link/check", new { token });
        Assert.Equal("reset", (string?)check["kind"]);
        Assert.Equal("frank", (string?)check["username"]);

        await _stack.PostJson("/mgmt/auth/reset/redeem", new { token, password = "brand-new-password" });
        await _stack.GetJson("/mgmt/me", user.Token, 401);
        await _stack.PostJson("/mgmt/auth/login", new { username = "frank", password = "brand-new-password" });

        // API keys stay valid after a reset.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/tags");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.ApiKey);
        using var res = await _stack.Http.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var audit = await _stack.Store.Audit.Find(a => a.Action == "user.password_reset_redeemed").AnyAsync(Ct);
        Assert.True(audit);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_kills_the_session()
    {
        var (_, _, login) = await Post("/mgmt/auth/login", new { username = "admin", password = Stack.AdminNewPassword });
        var first = RefreshCookie(login);

        var (s1, b1, r1) = await Post("/mgmt/auth/refresh", new { }, first);
        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.NotNull(JsonNode.Parse(b1)!["accessToken"]);
        var second = RefreshCookie(r1);
        Assert.NotEqual(first, second);

        // The first cookie was rotated away; presenting it again ends the family, including the second one.
        var (s2, _, _) = await Post("/mgmt/auth/refresh", new { }, first);
        Assert.Equal(HttpStatusCode.Unauthorized, s2);
        var (s3, _, _) = await Post("/mgmt/auth/refresh", new { }, second);
        Assert.Equal(HttpStatusCode.Unauthorized, s3);
    }

    [Fact]
    public async Task Node_token_is_shown_once_and_stored_hashed()
    {
        await _stack.StartNodeAsync();
        var node = await _stack.Store.Nodes.Find(n => n.Id == ObjectId.Parse(_stack.NodeId)).FirstAsync(Ct);
        Assert.DoesNotContain(_stack.NodeToken!, node.ToBsonDocument().ToJson());
        var list = await _stack.GetJson("/mgmt/admin/nodes", _stack.AdminToken);
        Assert.DoesNotContain(_stack.NodeToken!, list.ToJsonString());
        Assert.True((bool)list["nodes"]![0]!["online"]!);
    }

    [Fact]
    public async Task Revoking_a_node_token_closes_the_connection()
    {
        await _stack.StartNodeAsync();
        var id = ObjectId.Parse(_stack.NodeId);
        Assert.Equal(204, await _stack.Send(HttpMethod.Post, $"/mgmt/admin/nodes/{_stack.NodeId}/revoke", null,
            _stack.AdminToken));
        Assert.Null(_stack.Hub().Get(id));
        await Task.Delay(1500, Ct);
        Assert.Null(_stack.Hub().Get(id));
    }

    [Fact]
    public async Task Rotated_token_needs_the_new_secret()
    {
        await _stack.StartNodeAsync();
        var rotated = await _stack.PostJson($"/mgmt/admin/nodes/{_stack.NodeId}/rotate", new { }, _stack.AdminToken);
        Assert.StartsWith("orn_", (string)rotated["token"]!);
        await Task.Delay(1000, Ct);
        Assert.Null(_stack.Hub().Get(ObjectId.Parse(_stack.NodeId)));
    }

    [Fact]
    public async Task Protocol_version_mismatch_is_refused_cleanly()
    {
        await _stack.StartNodeAsync();
        await _stack.StopAgentAsync();
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + _stack.NodeToken);
        await ws.ConnectAsync(new Uri(_stack.BaseUrl.ToString().Replace("http://", "ws://") + "node/connect"), Ct);
        var socket = new FrameSocket(ws);
        await socket.SendAsync(ProtocolJson.ToFrame(FrameType.Hello, 0, new HelloMessage(99, "future")), Ct);
        Assert.Null(await socket.ReceiveAsync(Ct));
        Assert.Equal(ProtocolConstants.CloseProtocolMismatch, (int)ws.CloseStatus!);
        Assert.Contains("protocol version", _stack.RelayLogs.All);
    }

    [Fact]
    public async Task Bad_node_token_is_rejected()
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer orn_00000000_" + new string('0', 64));
        ws.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() =>
            ws.ConnectAsync(new Uri(_stack.BaseUrl.ToString().Replace("http://", "ws://") + "node/connect"), Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, ws.HttpStatusCode);
    }

    [Fact]
    public async Task Second_connection_with_same_token_replaces_the_first_and_is_audited()
    {
        await _stack.StartNodeAsync();
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + _stack.NodeToken);
        await ws.ConnectAsync(new Uri(_stack.BaseUrl.ToString().Replace("http://", "ws://") + "node/connect"), Ct);
        var socket = new FrameSocket(ws);
        await socket.SendAsync(ProtocolJson.ToFrame(FrameType.Hello, 0, new HelloMessage(ProtocolConstants.Version, "x")), Ct);
        var ack = await socket.ReceiveAsync(Ct);
        Assert.Equal(FrameType.HelloAck, ack!.Value.Type);
        await Stack.WaitForAsync(async () =>
            await _stack.Store.Audit.Find(a => a.Action == "node.connection_replaced").AnyAsync(Ct));
    }

    [Fact]
    public async Task Grants_by_alias_cover_every_node_in_the_pool()
    {
        await _stack.StartNodeAsync();
        await _stack.PublishAsync("fake-model:7b", "pool");
        var second = await _stack.PostJson("/mgmt/admin/nodes", new { name = "second", maxConcurrency = 1 },
            _stack.AdminToken);
        var secondId = ObjectId.Parse((string)second["id"]!);
        await _stack.Store.Nodes.UpdateOneAsync(n => n.Id == secondId, Builders<Relay.Data.NodeDoc>.Update
            .Set(n => n.ReportedModels, [new Relay.Data.ReportedModel { Name = "other:1b" }]), cancellationToken: Ct);
        await _stack.PostJson("/mgmt/admin/publications",
            new { nodeId = (string)second["id"]!, upstreamModel = "other:1b", alias = "pool" }, _stack.AdminToken);
        await _stack.PostJson("/mgmt/admin/publications",
            new { nodeId = (string)second["id"]!, upstreamModel = "not-reported", alias = "x" }, _stack.AdminToken,
            expect: 400);
        await _stack.PostJson("/mgmt/admin/publications",
            new { nodeId = _stack.NodeId, upstreamModel = "fake-model:7b", alias = "Bad:Alias" }, _stack.AdminToken,
            expect: 400);

        var user = await _stack.CreateUserAsync("gina", "pool");
        var grants = await _stack.Store.Grants.Find(g => g.UserId == ObjectId.Parse(user.Id)).ToListAsync(Ct);
        Assert.Equal(2, grants.Count);

        var models = await _stack.GetJson("/mgmt/me/models", user.Token);
        Assert.Equal("pool", (string?)models[0]!["alias"]);
        Assert.True((bool)models[0]!["online"]!);
    }

    [Fact]
    public async Task Deleting_a_user_keeps_usage_history_readable()
    {
        await _stack.StartNodeAsync();
        await _stack.PublishAsync("fake-model:7b", "lit-writer");
        var user = await _stack.CreateUserAsync("henry", "lit-writer");
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
               {
                   Content = JsonContent.Create(new { model = "lit-writer", stream = false, messages = Array.Empty<object>() }),
               })
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.ApiKey);
            using var res = await _stack.Http.SendAsync(req, Ct);
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{res.StatusCode} {await res.Content.ReadAsStringAsync(Ct)} {_stack.Errors()}");
        }

        Assert.Equal(204, await _stack.Send(HttpMethod.Delete, $"/mgmt/admin/users/{user.Id}", null, _stack.AdminToken));
        Assert.False(await _stack.Store.ApiKeys.Find(k => k.UserId == ObjectId.Parse(user.Id)).AnyAsync(Ct));
        Assert.False(await _stack.Store.Grants.Find(g => g.UserId == ObjectId.Parse(user.Id)).AnyAsync(Ct));

        var usage = await _stack.GetJson("/mgmt/admin/usage", _stack.AdminToken);
        Assert.Equal("deleted user (henry)", (string?)usage[0]!["user"]);
        var recent = await _stack.GetJson("/mgmt/admin/usage/recent", _stack.AdminToken);
        Assert.Equal("deleted user (henry)", (string?)recent[0]!["user"]);
    }

    [Fact]
    public async Task Spa_gets_security_headers_and_unknown_mgmt_paths_are_404()
    {
        using var res = await _stack.Http.GetAsync("/mgmt/nope", Ct);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        using var health = await _stack.Http.GetAsync("/healthz", Ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
