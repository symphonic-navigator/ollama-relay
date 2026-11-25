using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using MongoDB.Driver;
using OllamaRelay.Relay.Data;
using OllamaRelay.Relay.Inference;
using OllamaRelay.Relay.Infrastructure;
using OllamaRelay.Relay.Mgmt;
using OllamaRelay.Relay.Nodes;
using OllamaRelay.Relay.Security;

namespace OllamaRelay.Relay;

public static class RelayApp
{
    /// <summary>Builds the relay. Tests call this with extra configuration and logging.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = "wwwroot",
        });

        // No HTTP logging middleware and no body capture anywhere. Request lines stay out of the logs too.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Lifetime", LogLevel.Information);
        configure?.Invoke(builder);

        var options = RelayOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(options);

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
            k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            k.Limits.MaxRequestLineSize = 8 * 1024;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
            k.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        });
        if (builder.Configuration["ASPNETCORE_URLS"] is null && builder.Configuration["urls"] is null &&
            builder.Configuration["URLS"] is null)
        {
            builder.WebHost.UseUrls("http://0.0.0.0:8080");
        }

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
        });

        builder.Services.AddSingleton<MongoStore>();
        builder.Services.AddSingleton<PasswordHasher>();
        builder.Services.AddSingleton<JwtIssuer>();
        builder.Services.AddSingleton<Limiters>();
        builder.Services.AddSingleton<AuditService>();
        builder.Services.AddSingleton<Sessions>();
        builder.Services.AddSingleton<Scheduler>();
        builder.Services.AddSingleton<ActiveStreams>();
        builder.Services.AddSingleton<NodeHub>();
        builder.Services.AddSingleton<ApiKeyAuth>();
        builder.Services.AddSingleton<Catalog>();
        builder.Services.AddSingleton<UsageService>();
        builder.Services.AddSingleton<InferenceHandler>();
        builder.Services.AddHostedService<Bootstrap>();
        builder.Services.AddExceptionHandler<ContentSafeExceptionHandler>();
        builder.Services.AddProblemDetails();

        var jwt = new JwtIssuer(options);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = jwt.ValidationParameters();
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    // API keys are never accepted on /mgmt.
                    var header = ctx.Request.Headers.Authorization.ToString();
                    if (header.Contains(SecretTokens.ApiKeyPrefix, StringComparison.Ordinal))
                    {
                        ctx.NoResult();
                    }

                    return Task.CompletedTask;
                },
                OnTokenValidated = async ctx =>
                {
                    var store = ctx.HttpContext.RequestServices.GetRequiredService<MongoStore>();
                    var sub = ctx.Principal?.FindFirst("sub")?.Value;
                    if (!MongoDB.Bson.ObjectId.TryParse(sub, out var id))
                    {
                        ctx.Fail("bad subject");
                        return;
                    }

                    var user = await store.Users.Find(u => u.Id == id).FirstOrDefaultAsync();
                    var version = ctx.Principal?.FindFirst(JwtIssuer.ClaimTokenVersion)?.Value;
                    if (user is null || user.Disabled ||
                        version != user.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    {
                        ctx.Fail("session ended");
                    }
                },
            };
        });
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Any, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.User, p => p.RequireAuthenticatedUser()
                .RequireAssertion(c => !c.User.HasClaim(x => x.Type == JwtIssuer.ClaimMustChangePassword)))
            .AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().RequireRole("admin")
                .RequireAssertion(c => !c.User.HasClaim(x => x.Type == JwtIssuer.ClaimMustChangePassword)));

        builder.Services.AddCors(c => c.AddPolicy("inference", p =>
        {
            if (options.CorsAllowedOrigins.Length > 0)
            {
                p.WithOrigins(options.CorsAllowedOrigins).AllowAnyHeader().AllowAnyMethod();
            }
        }));

        if (options.TrustForwardedHeaders)
        {
            builder.Services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                o.ForwardLimit = 1;
                // The relay is only reachable through the reverse proxy on the compose network.
                o.KnownProxies.Clear();
                o.KnownIPNetworks.Clear();
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("192.168.0.0/16"));
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("127.0.0.0/8"));
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("fc00::/7"));
                o.KnownProxies.Add(IPAddress.IPv6Loopback);
            });
        }

        var app = builder.Build();
        if (options.TrustForwardedHeaders)
        {
            app.UseForwardedHeaders();
        }

        app.UseExceptionHandler();
        app.UseSecurityHeaders();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();

        // Management bodies are small; keep the large limit for the inference API only.
        app.UseWhen(c => c.Request.Path.StartsWithSegments("/mgmt"), b => b.Use((http, next) =>
        {
            var feature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
            {
                feature.MaxRequestBodySize = 64 * 1024;
            }

            return next(http);
        }));

        app.Map(Protocol.ProtocolConstants.ConnectPath, (HttpContext http, NodeHub hub) => hub.HandleAsync(http));
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        InferenceHandler.Map(app);
        AuthEndpoints.Map(app);
        MeEndpoints.Map(app, options);
        AdminEndpoints.Map(app, options);
        app.Map("/mgmt/{**rest}", () => Results.Json(new { error = "not found" }, statusCode: 404));
        app.Map("/node/{**rest}", () => Results.NotFound());
        app.MapFallbackToFile("index.html");
        return app;
    }
}
