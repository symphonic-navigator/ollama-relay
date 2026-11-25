using MongoDB.Bson;
using MongoDB.Driver;
using OllamaRelay.Relay.Data;
using OllamaRelay.Relay.Security;

namespace OllamaRelay.Relay.Mgmt;

/// <summary>
/// Web sessions: a short-lived JWT access token held in memory by the SPA, plus a rotating refresh token
/// in an HttpOnly, SameSite=Strict cookie scoped to /mgmt/auth. Refresh tokens are stored hashed.
/// </summary>
public sealed class Sessions(MongoStore store, JwtIssuer jwt, RelayOptions options)
{
    public const string CookieName = "orl_refresh";
    public const string CookiePath = "/mgmt/auth";
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(14);

    public async Task<string> StartAsync(HttpContext http, UserDoc user, ObjectId? family = null)
    {
        var secret = SecretTokens.NewOpaque();
        var now = DateTime.UtcNow;
        await store.RefreshTokens.InsertOneAsync(new RefreshTokenDoc
        {
            Id = ObjectId.GenerateNewId(),
            TokenHash = SecretTokens.Hash(secret),
            UserId = user.Id,
            FamilyId = family ?? ObjectId.GenerateNewId(),
            CreatedAt = now,
            ExpiresAt = now.Add(RefreshLifetime),
        });
        http.Response.Cookies.Append(CookieName, secret, CookieOptions(now.Add(RefreshLifetime)));
        return jwt.Issue(user);
    }

    public enum RefreshResult
    {
        Ok,
        Invalid,
    }

    public async Task<(RefreshResult, UserDoc?, string?)> RefreshAsync(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var secret) || string.IsNullOrEmpty(secret))
        {
            return (RefreshResult.Invalid, null, null);
        }

        var hash = SecretTokens.Hash(secret);
        var token = await store.RefreshTokens.Find(t => t.TokenHash == hash).FirstOrDefaultAsync();
        if (token is null || token.ExpiresAt < DateTime.UtcNow)
        {
            Clear(http);
            return (RefreshResult.Invalid, null, null);
        }

        if (token.ReplacedAt is not null)
        {
            // A rotated token came back: someone holds a copy. End the whole session family.
            await store.RefreshTokens.DeleteManyAsync(t => t.FamilyId == token.FamilyId);
            Clear(http);
            return (RefreshResult.Invalid, null, null);
        }

        var user = await store.Users.Find(u => u.Id == token.UserId).FirstOrDefaultAsync();
        if (user is null || user.Disabled)
        {
            Clear(http);
            return (RefreshResult.Invalid, null, null);
        }

        // Keep the rotated token briefly to detect reuse; the TTL index removes it.
        await store.RefreshTokens.UpdateOneAsync(t => t.Id == token.Id, Builders<RefreshTokenDoc>.Update
            .Set(t => t.ReplacedAt, DateTime.UtcNow)
            .Set(t => t.ExpiresAt, DateTime.UtcNow.AddDays(1)));
        var access = await StartAsync(http, user, token.FamilyId);
        return (RefreshResult.Ok, user, access);
    }

    public async Task EndAsync(HttpContext http)
    {
        if (http.Request.Cookies.TryGetValue(CookieName, out var secret) && !string.IsNullOrEmpty(secret))
        {
            var hash = SecretTokens.Hash(secret);
            var token = await store.RefreshTokens.Find(t => t.TokenHash == hash).FirstOrDefaultAsync();
            if (token is not null)
            {
                await store.RefreshTokens.DeleteManyAsync(t => t.FamilyId == token.FamilyId);
            }
        }

        Clear(http);
    }

    public Task RevokeAllAsync(ObjectId userId) => store.RefreshTokens.DeleteManyAsync(t => t.UserId == userId);

    private void Clear(HttpContext http) =>
        http.Response.Cookies.Delete(CookieName, CookieOptions(DateTime.UnixEpoch));

    private CookieOptions CookieOptions(DateTime expires) => new()
    {
        HttpOnly = true,
        Secure = options.CookieSecure,
        SameSite = SameSiteMode.Strict,
        Path = CookiePath,
        Expires = expires,
        IsEssential = true,
    };
}
