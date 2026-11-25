using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OllamaRelay.Relay.Data;

// Schemas deliberately contain no free-text field that could carry prompt or completion content.
// Names (usernames, node names, aliases, key names) are set by the admin or the user in the UI.

public sealed class UserDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public required string Username { get; set; }
    public required string UsernameLower { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsAdmin { get; set; }
    public bool Disabled { get; set; }
    public bool MustChangePassword { get; set; }

    /// <summary>Bumped on password change, disable and reset; access tokens carry it and die with it.</summary>
    public int TokenVersion { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public enum InviteKind
{
    Invite,
    PasswordReset,
}

public sealed class InviteDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public InviteKind Kind { get; set; }
    public required string TokenHash { get; set; }

    /// <summary>For password resets: the user the link belongs to.</summary>
    public ObjectId? UserId { get; set; }

    public bool GrantAdmin { get; set; }
    public ObjectId CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public ObjectId? UsedBy { get; set; }
}

public sealed class RefreshTokenDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public required string TokenHash { get; set; }
    public ObjectId UserId { get; set; }
    public ObjectId FamilyId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>Set when rotated. Presenting a rotated token again revokes the whole family.</summary>
    public DateTime? ReplacedAt { get; set; }
}

public sealed class ApiKeyDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public ObjectId UserId { get; set; }
    public required string Name { get; set; }
    public required string Prefix { get; set; }
    public required string KeyHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public sealed class NodeDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public required string Name { get; set; }
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Null when the token was revoked.</summary>
    public string? TokenHash { get; set; }

    public string? TokenPrefix { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? TokenIssuedAt { get; set; }

    /// <summary>Last reported local models, persisted so the admin can publish while the node is offline.</summary>
    public List<ReportedModel> ReportedModels { get; set; } = [];

    public DateTime? LastSeenAt { get; set; }
    public string? LastOllamaVersion { get; set; }
    public string? LastAgentVersion { get; set; }
}

public sealed class ReportedModel
{
    public required string Name { get; set; }
    public long Size { get; set; }
    public string? Family { get; set; }
    public string? ParameterSize { get; set; }
    public string? QuantizationLevel { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

public sealed class PublicationDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public ObjectId NodeId { get; set; }
    public required string UpstreamModel { get; set; }
    public required string Alias { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class GrantDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public ObjectId UserId { get; set; }
    public ObjectId PublicationId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class UsageRecordDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public ObjectId UserId { get; set; }
    public required string Username { get; set; }
    public ObjectId ApiKeyId { get; set; }
    public ObjectId NodeId { get; set; }
    public required string NodeName { get; set; }
    public required string Alias { get; set; }
    public required string UpstreamModel { get; set; }
    public required string Endpoint { get; set; }
    public required string Kind { get; set; }
    public DateTime StartedAt { get; set; }
    public long QueueWaitMs { get; set; }
    public long? TimeToFirstByteMs { get; set; }
    public long DurationMs { get; set; }
    public long? PromptTokens { get; set; }
    public long? CompletionTokens { get; set; }
    public required string Outcome { get; set; }
    public int Status { get; set; }
}

public sealed class UsageDailyDoc
{
    /// <summary>Composite key: day|user|alias|node.</summary>
    [BsonId] public required string Id { get; set; }
    public DateTime Day { get; set; }
    public ObjectId UserId { get; set; }
    public required string Username { get; set; }
    public required string Alias { get; set; }
    public ObjectId NodeId { get; set; }
    public required string NodeName { get; set; }
    public long Requests { get; set; }
    public long Errors { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long DurationMs { get; set; }
}

public sealed class AuditDoc
{
    [BsonId] public ObjectId Id { get; set; }
    public DateTime At { get; set; }
    public ObjectId? ActorId { get; set; }
    public string? ActorName { get; set; }
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? TargetName { get; set; }
}
