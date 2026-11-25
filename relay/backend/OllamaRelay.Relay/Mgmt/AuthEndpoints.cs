using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using OllamaRelay.Relay.Data;
using OllamaRelay.Relay.Inference;
using OllamaRelay.Relay.Security;

namespace OllamaRelay.Relay.Mgmt;

public static partial class AuthEndpoints
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(72);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,31}$")]
    private static partial Regex UsernamePattern();

    public static bool IsValidUsername(string? name) => name is not null && UsernamePattern().IsMatch(name);

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/mgmt/auth");

        g.MapPost("/login", async (LoginRequest req, HttpContext http, MongoStore store, PasswordHasher hasher,
            Sessions sessions, Limiters limiters, AuditService audit, ILogger<Sessions> log) =>
        {
            var ip = Ip(http);
            var name = (req.Username ?? "").Trim().ToLowerInvariant();
            if (limiters.LoginByIp.IsBlocked(ip) || limiters.LoginByUser.IsBlocked(name))
            {
                log.LogWarning("security: login rate limited for {Ip}", ip);
                return Results.Json(new { error = "Too many attempts. Try again later." }, statusCode: 429);
            }

            var user = name.Length == 0
                ? null
                : await store.Users.Find(u => u.UsernameLower == name).FirstOrDefaultAsync();
            var password = req.Password ?? "";
            bool ok;
            if (user is null)
            {
                hasher.VerifyDummy(password);
                ok = false;
            }
            else
            {
                ok = hasher.Verify(password, user.PasswordHash) && !user.Disabled;
            }

            if (!ok)
            {
                limiters.LoginByIp.RecordFailure(ip);
                limiters.LoginByUser.RecordFailure(name);
                log.LogWarning("security: failed login from {Ip}", ip);
                return Results.Json(new { error = "Invalid username or password." }, statusCode: 401);
            }

            limiters.LoginByUser.Reset(name);
            await store.Users.UpdateOneAsync(u => u.Id == user!.Id,
                Builders<UserDoc>.Update.Set(u => u.LastLoginAt, DateTime.UtcNow));
            await audit.WriteAsync(new Actor(user!.Id, user.Username), "auth.login", "user", user.Id.ToString(),
                user.Username);
            var access = await sessions.StartAsync(http, user);
            return Results.Ok(new SessionResponse(access, Views.Of(user)));
        });

        g.MapPost("/refresh", async (HttpContext http, Sessions sessions) =>
        {
            var (result, user, access) = await sessions.RefreshAsync(http);
            return result == Sessions.RefreshResult.Ok
                ? Results.Ok(new SessionResponse(access!, Views.Of(user!)))
                : Results.Json(new { error = "Not signed in." }, statusCode: 401);
        });

        g.MapPost("/logout", async (HttpContext http, Sessions sessions) =>
        {
            await sessions.EndAsync(http);
            return Results.NoContent();
        });

        g.MapPost("/link/check", async (TokenRequest req, HttpContext http, MongoStore store, Limiters limiters) =>
        {
            var ip = Ip(http);
            if (limiters.RedeemByIp.IsBlocked(ip))
            {
                return Results.Json(new { error = "Too many attempts. Try again later." }, statusCode: 429);
            }

            var link = await FindLinkAsync(store, req.Token);
            if (link is null)
            {
                limiters.RedeemByIp.RecordFailure(ip);
                return Results.Json(new { error = "This link is invalid or has expired." }, statusCode: 404);
            }

            string? username = null;
            if (link.Kind == InviteKind.PasswordReset && link.UserId is { } uid)
            {
                username = (await store.Users.Find(u => u.Id == uid).FirstOrDefaultAsync())?.Username;
            }

            return Results.Ok(new { kind = link.Kind == InviteKind.Invite ? "invite" : "reset", username });
        });

        g.MapPost("/invite/redeem", async (RedeemInviteRequest req, HttpContext http, MongoStore store,
            PasswordHasher hasher, Sessions sessions, Limiters limiters, AuditService audit) =>
        {
            var ip = Ip(http);
            if (limiters.RedeemByIp.IsBlocked(ip))
            {
                return Results.Json(new { error = "Too many attempts. Try again later." }, statusCode: 429);
            }

            var link = await FindLinkAsync(store, req.Token);
            if (link is null || link.Kind != InviteKind.Invite)
            {
                limiters.RedeemByIp.RecordFailure(ip);
                return Results.Json(new { error = "This link is invalid or has expired." }, statusCode: 404);
            }

            var username = req.Username?.Trim();
            if (!IsValidUsername(username))
            {
                return Results.Json(new
                {
                    error = "Username must be 3 to 32 characters: letters, digits, dot, dash or underscore.",
                }, statusCode: 400);
            }

            if (PasswordHasher.ValidatePolicy(req.Password) is { } problem)
            {
                return Results.Json(new { error = problem }, statusCode: 400);
            }

            var user = new UserDoc
            {
                Id = ObjectId.GenerateNewId(),
                Username = username!,
                UsernameLower = username!.ToLowerInvariant(),
                PasswordHash = hasher.Hash(req.Password!),
                IsAdmin = link.GrantAdmin,
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow,
            };

            // Claim the link first, atomically, so it can only ever be used once.
            var claimed = await store.Invites.UpdateOneAsync(
                i => i.Id == link.Id && i.UsedAt == null,
                Builders<InviteDoc>.Update.Set(i => i.UsedAt, DateTime.UtcNow).Set(i => i.UsedBy, user.Id));
            if (claimed.ModifiedCount != 1)
            {
                return Results.Json(new { error = "This link is invalid or has expired." }, statusCode: 404);
            }

            try
            {
                await store.Users.InsertOneAsync(user);
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // Give the link back so the invitee can pick another name.
                await store.Invites.UpdateOneAsync(i => i.Id == link.Id,
                    Builders<InviteDoc>.Update.Set(i => i.UsedAt, null).Set(i => i.UsedBy, null));
                return Results.Json(new { error = "This username is taken." }, statusCode: 409);
            }

            await audit.WriteAsync(new Actor(user.Id, user.Username), "user.invite_redeemed", "user",
                user.Id.ToString(), user.Username);
            var access = await sessions.StartAsync(http, user);
            return Results.Ok(new SessionResponse(access, Views.Of(user)));
        });

        g.MapPost("/reset/redeem", async (RedeemResetRequest req, HttpContext http, MongoStore store,
            PasswordHasher hasher, Sessions sessions, Limiters limiters, AuditService audit, ActiveStreams active) =>
        {
            var ip = Ip(http);
            if (limiters.RedeemByIp.IsBlocked(ip))
            {
                return Results.Json(new { error = "Too many attempts. Try again later." }, statusCode: 429);
            }

            var link = await FindLinkAsync(store, req.Token);
            if (link is null || link.Kind != InviteKind.PasswordReset || link.UserId is null)
            {
                limiters.RedeemByIp.RecordFailure(ip);
                return Results.Json(new { error = "This link is invalid or has expired." }, statusCode: 404);
            }

            if (PasswordHasher.ValidatePolicy(req.Password) is { } problem)
            {
                return Results.Json(new { error = problem }, statusCode: 400);
            }

            var claimed = await store.Invites.UpdateOneAsync(i => i.Id == link.Id && i.UsedAt == null,
                Builders<InviteDoc>.Update.Set(i => i.UsedAt, DateTime.UtcNow).Set(i => i.UsedBy, link.UserId));
            if (claimed.ModifiedCount != 1)
            {
                return Results.Json(new { error = "This link is invalid or has expired." }, statusCode: 404);
            }

            var user = await store.Users.FindOneAndUpdateAsync(u => u.Id == link.UserId,
                Builders<UserDoc>.Update
                    .Set(u => u.PasswordHash, hasher.Hash(req.Password!))
                    .Set(u => u.MustChangePassword, false)
                    .Inc(u => u.TokenVersion, 1),
                new FindOneAndUpdateOptions<UserDoc> { ReturnDocument = ReturnDocument.After });
            if (user is null)
            {
                return Results.Json(new { error = "This link is invalid or has expired." }, statusCode: 404);
            }

            await sessions.RevokeAllAsync(user.Id);
            await audit.WriteAsync(new Actor(user.Id, user.Username), "user.password_reset_redeemed", "user",
                user.Id.ToString(), user.Username);
            if (user.Disabled)
            {
                return Results.Ok(new { disabled = true });
            }

            var access = await sessions.StartAsync(http, user);
            return Results.Ok(new SessionResponse(access, Views.Of(user)));
        });
    }

    private static async Task<InviteDoc?> FindLinkAsync(MongoStore store, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var hash = SecretTokens.Hash(token.Trim());
        var link = await store.Invites.Find(i => i.TokenHash == hash).FirstOrDefaultAsync();
        return link is null || link.UsedAt is not null || link.ExpiresAt < DateTime.UtcNow ? null : link;
    }

    public static string Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
