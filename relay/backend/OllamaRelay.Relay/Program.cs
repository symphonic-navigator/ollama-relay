using OllamaRelay.Relay;
using OllamaRelay.Relay.Infrastructure;

if (args.Length > 0 && args[0] == "reset-admin-password")
{
    var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    return await BreakGlass.RunAsync(args, config);
}

if (args.Length > 0 && args[0] == "healthcheck")
{
    // Used by the container HEALTHCHECK; the runtime image has no curl.
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var res = await http.GetAsync("http://127.0.0.1:8080/healthz");
        return res.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
    catch (TaskCanceledException)
    {
        return 1;
    }
}

var app = RelayApp.Build(args);
await app.RunAsync();
return 0;
