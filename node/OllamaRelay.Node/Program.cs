using OllamaRelay.Node;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss ";
});
// HttpClient request logging would only show URLs, but keep it quiet: the node logs its own metadata.
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

var options = NodeOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => new OllamaClient(new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectTimeout = TimeSpan.FromSeconds(5),
})
{
    BaseAddress = options.OllamaUrl,
    Timeout = Timeout.InfiniteTimeSpan,
}));
builder.Services.AddSingleton<NodeAgent>();
builder.Services.AddHostedService<NodeWorker>();

var host = builder.Build();
host.Services.GetRequiredService<ILogger<NodeAgent>>()
    .LogInformation("ollama-relay node {Version}, relay {Relay}, ollama {Ollama}", NodeAgent.AgentVersion,
        options.RelayUrl.GetLeftPart(UriPartial.Path), options.OllamaUrl);
host.Run();

internal sealed class NodeWorker(NodeAgent agent) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => agent.RunAsync(stoppingToken);
}
