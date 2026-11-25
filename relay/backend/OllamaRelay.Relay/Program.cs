using OllamaRelay.Relay;
using OllamaRelay.Relay.Infrastructure;

if (args.Length > 0 && args[0] == "reset-admin-password")
{
    var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    return await BreakGlass.RunAsync(args, config);
}

var app = RelayApp.Build(args);
await app.RunAsync();
return 0;
