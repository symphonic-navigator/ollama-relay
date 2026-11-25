using MongoDB.Driver;
using OllamaRelay.Relay.Data;
using OllamaRelay.Relay.Mgmt;
using OllamaRelay.Relay.Security;

namespace OllamaRelay.Relay.Infrastructure;

/// <summary>
/// <c>reset-admin-password &lt;username&gt;</c>: sets a new password for an admin, for a locked-out last admin.
/// Needs shell access to the relay container, which is the intended bar.
/// The new password is read from stdin, or generated and printed when stdin is empty.
/// </summary>
public static class BreakGlass
{
    public static async Task<int> RunAsync(string[] args, IConfiguration config)
    {
        if (args.Length < 2)
        {
            await Console.Error.WriteLineAsync("usage: reset-admin-password <username>   (new password on stdin, or empty to generate)");
            return 2;
        }

        var options = RelayOptions.FromConfiguration(config);
        var store = new MongoStore(options);
        var hasher = new PasswordHasher(options);
        var name = args[1].Trim().ToLowerInvariant();

        var password = Console.IsInputRedirected ? (await Console.In.ReadLineAsync())?.Trim() : null;
        var generated = string.IsNullOrEmpty(password);
        if (generated)
        {
            password = SecretTokens.NewOpaque()[..24];
        }

        if (PasswordHasher.ValidatePolicy(password) is { } problem)
        {
            await Console.Error.WriteLineAsync(problem);
            return 2;
        }

        var user = await store.Users.FindOneAndUpdateAsync(u => u.UsernameLower == name, Builders<UserDoc>.Update
            .Set(u => u.PasswordHash, hasher.Hash(password!))
            .Set(u => u.IsAdmin, true)
            .Set(u => u.Disabled, false)
            .Set(u => u.MustChangePassword, true)
            .Inc(u => u.TokenVersion, 1));
        if (user is null)
        {
            await Console.Error.WriteLineAsync($"no user named '{args[1]}'");
            return 1;
        }

        await store.RefreshTokens.DeleteManyAsync(t => t.UserId == user.Id);
        await new AuditService(store).WriteAsync(Actor.System, "admin.break_glass_reset", "user", user.Id.ToString(),
            user.Username);
        Console.WriteLine(generated
            ? $"New password for {user.Username}: {password}\nChange it at the next login."
            : $"Password for {user.Username} set. Change it at the next login.");
        return 0;
    }
}
