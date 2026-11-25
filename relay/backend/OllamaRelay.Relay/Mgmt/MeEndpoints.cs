using System.Security.Claims;
using MongoDB.Bson;
using MongoDB.Driver;
using OllamaRelay.Relay.Data;
using OllamaRelay.Relay.Inference;
using OllamaRelay.Relay.Nodes;
using OllamaRelay.Relay.Security;

namespace OllamaRelay.Relay.Mgmt;

public static class MeEndpoints
{
    public static ObjectId UserId(this ClaimsPrincipal user) => ObjectId.Parse(user.FindFirstValue("sub"));

    public static void Map(IEndpointRouteBuilder app, RelayOptions options)
    {
        app.MapGet("/mgmt/info", () => Results.Ok(new
        {
            publicBaseUrl = options.PublicBaseUrl,
            version = typeof(MeEndpoints).Assembly.GetName().Version?.ToString(3),
        })).AllowAnonymous();

        // Reachable while a password change is pending.
        var any = app.MapGroup("/mgmt/me").RequireAuthorization(Policies.Any);
        any.MapGet("", async (ClaimsPrincipal principal, MongoStore store) =>
        {
            var user = await store.Users.Find(u => u.Id == principal.UserId()).FirstOrDefaultAsync();
            return user is null ? Results.NotFound() : Results.Ok(Views.Of(user));
        });

        any.MapPost("/password", async (ChangePasswordRequest req, ClaimsPrincipal principal, HttpContext http,
            MongoStore store, PasswordHasher hasher, Sessions sessions, AuditService audit) =>
        {
            var user = await store.Users.Find(u => u.Id == principal.UserId()).FirstOrDefaultAsync();
            if (user is null || !hasher.Verify(req.CurrentPassword ?? "", user.PasswordHash))
            {
                return Results.Json(new { error = "Current password is wrong." }, statusCode: 400);
            }

            if (PasswordHasher.ValidatePolicy(req.NewPassword) is { } problem)
            {
                return Results.Json(new { error = problem }, statusCode: 400);
            }

            if (req.NewPassword == req.CurrentPassword)
            {
                return Results.Json(new { error = "The new password must differ from the current one." },
                    statusCode: 400);
            }

            user = await store.Users.FindOneAndUpdateAsync(u => u.Id == user.Id, Builders<UserDoc>.Update
                    .Set(u => u.PasswordHash, hasher.Hash(req.NewPassword!))
                    .Set(u => u.MustChangePassword, false)
                    .Inc(u => u.TokenVersion, 1),
                new FindOneAndUpdateOptions<UserDoc> { ReturnDocument = ReturnDocument.After });
            await sessions.RevokeAllAsync(user.Id);
            await audit.WriteAsync(Actor.From(principal), "user.password_changed", "user", user.Id.ToString(),
                user.Username);
            var access = await sessions.StartAsync(http, user);
            return Results.Ok(new SessionResponse(access, Views.Of(user)));
        });

        var me = app.MapGroup("/mgmt/me").RequireAuthorization(Policies.User);

        me.MapGet("/keys", async (ClaimsPrincipal principal, MongoStore store) =>
        {
            var keys = await store.ApiKeys.Find(k => k.UserId == principal.UserId())
                .SortByDescending(k => k.CreatedAt).ToListAsync();
            return Results.Ok(keys.Select(KeyView));
        });

        me.MapPost("/keys", async (CreateKeyRequest req, ClaimsPrincipal principal, MongoStore store,
            AuditService audit) =>
        {
            var name = (req.Name ?? "").Trim();
            if (name.Length is 0 or > 60)
            {
                return Results.Json(new { error = "Give the key a name of up to 60 characters." }, statusCode: 400);
            }

            var issued = SecretTokens.NewApiKey();
            var doc = new ApiKeyDoc
            {
                Id = ObjectId.GenerateNewId(),
                UserId = principal.UserId(),
                Name = name,
                Prefix = issued.Prefix,
                KeyHash = issued.Hash,
                CreatedAt = DateTime.UtcNow,
            };
            await store.ApiKeys.InsertOneAsync(doc);
            await audit.WriteAsync(Actor.From(principal), "apikey.created", "apikey", doc.Id.ToString(), doc.Name);
            return Results.Ok(new { key = issued.Secret, info = KeyView(doc) });
        });

        me.MapDelete("/keys/{id}", async (string id, ClaimsPrincipal principal, MongoStore store,
            ActiveStreams active, AuditService audit) =>
        {
            if (!Views.TryId(id, out var keyId))
            {
                return Results.NotFound();
            }

            var deleted = await store.ApiKeys.FindOneAndDeleteAsync(k => k.Id == keyId && k.UserId == principal.UserId());
            if (deleted is null)
            {
                return Results.NotFound();
            }

            active.CancelWhere(s => s.ApiKeyId == keyId, Protocol.CancelReasons.Revoked);
            await audit.WriteAsync(Actor.From(principal), "apikey.revoked", "apikey", id, deleted.Name);
            return Results.NoContent();
        });

        me.MapGet("/models", async (ClaimsPrincipal principal, Catalog catalog, NodeHub hub) =>
        {
            var pubs = await catalog.AllGrantedAsync(principal.UserId(), CancellationToken.None);
            return Results.Ok(pubs.GroupBy(p => p.Alias).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new
            {
                alias = g.Key,
                online = g.Any(p => hub.Get(p.NodeId) is not null),
                loaded = g.Any(p => hub.Get(p.NodeId)?.HasLoaded(p.UpstreamModel) == true),
            }));
        });

        me.MapGet("/usage", async (int? days, ClaimsPrincipal principal, MongoStore store) =>
        {
            var since = DateTime.UtcNow.Date.AddDays(-Math.Clamp(days ?? 30, 1, 3650));
            var uid = principal.UserId();
            var rows = await store.UsageDaily.Find(d => d.UserId == uid && d.Day >= since).SortBy(d => d.Day)
                .ToListAsync();
            return Results.Ok(rows.Select(r => new
            {
                day = r.Day.ToString("yyyy-MM-dd"),
                r.Alias,
                r.Requests,
                r.Errors,
                r.PromptTokens,
                r.CompletionTokens,
                r.DurationMs,
            }));
        });
    }

    private static object KeyView(ApiKeyDoc k) => new
    {
        id = k.Id.ToString(),
        k.Name,
        k.Prefix,
        k.CreatedAt,
        k.LastUsedAt,
    };
}

public static class Policies
{
    /// <summary>Signed in, even with a pending forced password change.</summary>
    public const string Any = "any";

    /// <summary>Signed in, no pending password change.</summary>
    public const string User = "user";

    public const string Admin = "admin";
}
