using System.Security.Claims;
using MongoDB.Bson;
using OllamaRelay.Relay.Data;

namespace OllamaRelay.Relay.Mgmt;

public sealed record Actor(ObjectId? Id, string? Name)
{
    public static Actor From(ClaimsPrincipal user) =>
        new(ObjectId.TryParse(user.FindFirstValue("sub"), out var id) ? id : null, user.Identity?.Name);

    public static readonly Actor System = new(null, "system");
}

public sealed class AuditService(MongoStore store)
{
    public Task WriteAsync(Actor? actor, string action, string? targetType = null, string? targetId = null,
        string? targetName = null) =>
        store.Audit.InsertOneAsync(new AuditDoc
        {
            At = DateTime.UtcNow,
            ActorId = actor?.Id,
            ActorName = actor?.Name ?? "system",
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            TargetName = targetName,
        });
}
