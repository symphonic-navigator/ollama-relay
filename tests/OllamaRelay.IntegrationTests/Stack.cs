using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using OllamaRelay.Node;
using OllamaRelay.Relay;
using OllamaRelay.Relay.Data;

namespace OllamaRelay.IntegrationTests;

/// <summary>Relay + node agent + fake Ollama + a fresh MongoDB database, wired together over real sockets.</summary>
public sealed class Stack : IAsyncDisposable
{
    public const string AdminPassword = "initial-admin-password";
    public const string AdminNewPassword = "changed-admin-password";

    private CancellationTokenSource? _nodeCts;
    private Task? _nodeTask;

    public required WebApplication Relay { get; init; }
    public required FakeOllama Ollama { get; init; }
    public required LogCapture RelayLogs { get; init; }
    public LogCapture NodeLogs { get; } = new();
    public required string Database { get; init; }
    public required string MongoUrl { get; init; }
    public required Uri BaseUrl { get; init; }
    public HttpClient Http { get; } = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) };
    public string AdminToken { get; private set; } = "";
    public string? NodeId { get; private set; }
    public string? NodeToken { get; private set; }
    public NodeAgent? Agent { get; private set; }

    public string Errors() => string.Join('\n', RelayLogs.Lines.Where(l => l.StartsWith("Error", StringComparison.Ordinal)));

    public MongoStore Store => Relay.Services.GetRequiredService<MongoStore>();

    public static async Task<Stack> StartAsync(MongoFixture mongo, Dictionary<string, string?>? extra = null)
    {
        var database = "t_" + Guid.NewGuid().ToString("N")[..12];
        var logs = new LogCapture();
        var config = new Dictionary<string, string?>
        {
            ["JWT_SIGNING_KEY"] = new string('s', 48),
            ["MONGO_URL"] = mongo.ConnectionString,
            ["MONGO_DATABASE"] = database,
            ["ADMIN_USERNAME"] = "admin",
            ["ADMIN_INITIAL_PASSWORD"] = AdminPassword,
            ["COOKIE_SECURE"] = "false",
            ["ARGON2_MEMORY_KIB"] = "1024",
            ["ARGON2_ITERATIONS"] = "1",
            ["NODE_HEARTBEAT_SECONDS"] = "1",
            ["QUEUE_TIMEOUT_SECONDS"] = "2",
            ["PUBLIC_BASE_URL"] = "http://relay.test",
            ["urls"] = "http://127.0.0.1:0",
        };
        foreach (var (k, v) in extra ?? [])
        {
            config[k] = v;
        }

        var ollama = await FakeOllama.StartAsync();
        var relay = RelayApp.Build([], b =>
        {
            b.Configuration.AddInMemoryCollection(config);
            b.Logging.ClearProviders();
            b.Logging.AddProvider(logs);
            b.Logging.SetMinimumLevel(LogLevel.Trace);
            b.Logging.AddFilter<LogCapture>(null, LogLevel.Trace);
        });
        await relay.StartAsync();
        var stack = new Stack
        {
            Relay = relay,
            Ollama = ollama,
            RelayLogs = logs,
            Database = database,
            MongoUrl = mongo.ConnectionString,
            BaseUrl = new Uri(relay.Urls.First()),
        };
        stack.Http.BaseAddress = stack.BaseUrl;
        await stack.BootstrapAdminAsync();
        return stack;
    }

    private async Task BootstrapAdminAsync()
    {
        var login = await PostJson("/mgmt/auth/login", new { username = "admin", password = AdminPassword });
        var token = (string)login["accessToken"]!;
        var changed = await PostJson("/mgmt/me/password",
            new { currentPassword = AdminPassword, newPassword = AdminNewPassword }, token);
        AdminToken = (string)changed["accessToken"]!;
    }

    public async Task<JsonObject> PostJson(string path, object body, string? bearer = null, int expect = 200)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (bearer is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True((int)res.StatusCode == expect, $"{path}: expected {expect}, got {(int)res.StatusCode}: {text}\n{Errors()}");
        return text.Length > 0 && JsonNode.Parse(text) is JsonObject o ? o : new JsonObject();
    }

    public async Task<JsonNode> GetJson(string path, string bearer, int expect = 200)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True((int)res.StatusCode == expect, $"{path}: expected {expect}, got {(int)res.StatusCode}: {text}\n{Errors()}");
        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!;
    }

    public async Task<int> Send(HttpMethod method, string path, object? body, string bearer)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var res = await Http.SendAsync(req);
        return (int)res.StatusCode;
    }

    /// <summary>Creates a node, starts the agent and waits until it is online and has reported its models.</summary>
    public async Task StartNodeAsync(int maxConcurrency = 2, string name = "diimitra")
    {
        var created = await PostJson("/mgmt/admin/nodes", new { name, maxConcurrency }, AdminToken);
        NodeId = (string)created["id"]!;
        NodeToken = (string)created["token"]!;
        StartAgent();
        await WaitForAsync(async () =>
        {
            var node = await Store.Nodes.Find(n => n.Id == ObjectId.Parse(NodeId)).FirstOrDefaultAsync();
            return node?.ReportedModels.Count > 0 && Hub().Get(node.Id) is not null;
        });
    }

    public Relay.Nodes.NodeHub Hub() => Relay.Services.GetRequiredService<Relay.Nodes.NodeHub>();

    public void StartAgent()
    {
        var factory = LoggerFactory.Create(b =>
        {
            b.AddProvider(NodeLogs);
            b.SetMinimumLevel(LogLevel.Trace);
        });
        var options = new NodeOptions
        {
            RelayUrl = NodeOptions.NormalizeRelayUrl(new Uri(BaseUrl.ToString().Replace("http://", "ws://"))),
            NodeToken = NodeToken!,
            OllamaUrl = Ollama.Url,
            MinBackoff = TimeSpan.FromMilliseconds(100),
            MaxBackoff = TimeSpan.FromMilliseconds(500),
        };
        var http = new HttpClient { BaseAddress = Ollama.Url, Timeout = Timeout.InfiniteTimeSpan };
        Agent = new NodeAgent(options, new OllamaClient(http), factory.CreateLogger<NodeAgent>());
        _nodeCts = new CancellationTokenSource();
        _nodeTask = Agent.RunAsync(_nodeCts.Token);
    }

    public async Task<string> PublishAsync(string upstream, string alias)
    {
        var res = await PostJson("/mgmt/admin/publications", new { nodeId = NodeId, upstreamModel = upstream, alias },
            AdminToken);
        return (string)res["id"]!;
    }

    public sealed record UserHandle(string Id, string Username, string Token, string ApiKey, string ApiKeyId);

    public async Task<UserHandle> CreateUserAsync(string username, params string[] grantAliases)
    {
        var invite = await PostJson("/mgmt/admin/invites", new { }, AdminToken);
        var link = (string)invite["link"]!;
        var token = link[(link.IndexOf('#') + 1)..];
        var session = await PostJson("/mgmt/auth/invite/redeem",
            new { token, username, password = "user-password-123" });
        var access = (string)session["accessToken"]!;
        var userId = (string)session["user"]!["id"]!;
        var key = await PostJson("/mgmt/me/keys", new { name = "test" }, access);
        foreach (var alias in grantAliases)
        {
            Assert.Equal(204, await Send(HttpMethod.Put, "/mgmt/admin/grants",
                new { userId, alias, granted = true }, AdminToken));
        }

        return new UserHandle(userId, username, access, (string)key["key"]!, (string)key["info"]!["id"]!);
    }

    public static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("condition not met in time");
    }

    public static Task WaitForAsync(Func<bool> condition, int timeoutMs = 10_000) =>
        WaitForAsync(() => Task.FromResult(condition()), timeoutMs);

    public async Task StopAgentAsync()
    {
        if (_nodeCts is not null)
        {
            await _nodeCts.CancelAsync();
            try
            {
                await _nodeTask!;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAgentAsync();
        await Relay.StopAsync();
        await Relay.DisposeAsync();
        await Ollama.DisposeAsync();
        Http.Dispose();
        await new MongoClient(MongoUrl).DropDatabaseAsync(Database);
    }
}
