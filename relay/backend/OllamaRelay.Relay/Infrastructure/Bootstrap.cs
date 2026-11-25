using MongoDB.Bson;
using MongoDB.Driver;
using OllamaRelay.Relay.Data;
using OllamaRelay.Relay.Mgmt;
using OllamaRelay.Relay.Security;

namespace OllamaRelay.Relay.Infrastructure;

/// <summary>Creates indexes and the first admin on start.</summary>
public sealed class Bootstrap(MongoStore store, RelayOptions options, PasswordHasher hasher, AuditService audit,
    ILogger<Bootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.EnsureIndexesAsync(cancellationToken);
        if (await store.Users.Find(u => u.IsAdmin).AnyAsync(cancellationToken))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.AdminUsername) || string.IsNullOrEmpty(options.AdminInitialPassword))
        {
            logger.LogWarning("no admin exists; set ADMIN_USERNAME and ADMIN_INITIAL_PASSWORD to create one");
            return;
        }

        if (!AuthEndpoints.IsValidUsername(options.AdminUsername) ||
            PasswordHasher.ValidatePolicy(options.AdminInitialPassword) is not null)
        {
            logger.LogError(
                "ADMIN_USERNAME must be 3 to 32 characters [A-Za-z0-9._-] and ADMIN_INITIAL_PASSWORD at least {Min} characters",
                PasswordHasher.MinLength);
            return;
        }

        var admin = new UserDoc
        {
            Id = ObjectId.GenerateNewId(),
            Username = options.AdminUsername,
            UsernameLower = options.AdminUsername.ToLowerInvariant(),
            PasswordHash = hasher.Hash(options.AdminInitialPassword),
            IsAdmin = true,
            MustChangePassword = true,
            CreatedAt = DateTime.UtcNow,
        };
        try
        {
            await store.Users.InsertOneAsync(admin, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // A non-admin user holds the name. Make it admin rather than silently doing nothing.
            await store.Users.UpdateOneAsync(u => u.UsernameLower == admin.UsernameLower,
                Builders<UserDoc>.Update.Set(u => u.IsAdmin, true), cancellationToken: cancellationToken);
        }

        await audit.WriteAsync(Actor.System, "admin.bootstrapped", "user", admin.Id.ToString(), admin.Username);
        logger.LogInformation("created first admin {Admin}; a password change is required at first login",
            admin.Username);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
