using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using OllamaRelay.Relay.Data;
using OllamaRelay.Relay.Security;

namespace OllamaRelay.Relay.Inference;

public sealed record ApiCaller(UserDoc User, ApiKeyDoc Key);

public enum ApiAuthStatus
{
    Ok,
    Missing,
    Invalid,
    Blocked,
}

/// <summary>Bearer API key authentication for /api and /v1. Keys never work on /mgmt.</summary>
public sealed class ApiKeyAuth(MongoStore store, Limiters limiters, ILogger<ApiKeyAuth> logger)
{
    private readonly ConcurrentDictionary<ObjectId, DateTime> _lastUsedWrites = new();

    public async Task<(ApiAuthStatus Status, ApiCaller? Caller)> AuthenticateAsync(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (limiters.ApiKeyByIp.IsBlocked(ip))
        {
            return (ApiAuthStatus.Blocked, null);
        }

        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return (ApiAuthStatus.Missing, null);
        }

        var token = header[7..].Trim();
        ApiKeyDoc? key = null;
        UserDoc? user = null;
        if (SecretTokens.LooksLike(token, SecretTokens.ApiKeyPrefix))
        {
            var hash = SecretTokens.Hash(token);
            key = await store.ApiKeys.Find(k => k.KeyHash == hash).FirstOrDefaultAsync(http.RequestAborted);
            if (key is not null)
            {
                user = await store.Users.Find(u => u.Id == key.UserId).FirstOrDefaultAsync(http.RequestAborted);
            }
        }

        if (key is null || user is null || user.Disabled)
        {
            limiters.ApiKeyByIp.RecordFailure(ip);
            logger.LogWarning("security: rejected API key from {Ip}", ip);
            return (ApiAuthStatus.Invalid, null);
        }

        TouchLastUsed(key.Id);
        return (ApiAuthStatus.Ok, new ApiCaller(user, key));
    }

    private void TouchLastUsed(ObjectId keyId)
    {
        var now = DateTime.UtcNow;
        if (_lastUsedWrites.TryGetValue(keyId, out var last) && now - last < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastUsedWrites[keyId] = now;
        _ = store.ApiKeys.UpdateOneAsync(k => k.Id == keyId, Builders<ApiKeyDoc>.Update.Set(k => k.LastUsedAt, now));
    }
}
