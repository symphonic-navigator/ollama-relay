using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace OllamaRelay.Relay.Data;

public sealed class MongoStore
{
    static MongoStore()
    {
        ConventionRegistry.Register("ollama-relay", new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new IgnoreExtraElementsConvention(true),
            new EnumRepresentationConvention(BsonType.String),
        }, _ => true);
    }

    public MongoStore(RelayOptions options)
    {
        var settings = MongoClientSettings.FromConnectionString(options.MongoUrl);
        settings.ApplicationName = "ollama-relay";
        Client = new MongoClient(settings);
        Database = Client.GetDatabase(options.MongoDatabase);
        Users = Database.GetCollection<UserDoc>("users");
        Invites = Database.GetCollection<InviteDoc>("invites");
        RefreshTokens = Database.GetCollection<RefreshTokenDoc>("refreshTokens");
        ApiKeys = Database.GetCollection<ApiKeyDoc>("apiKeys");
        Nodes = Database.GetCollection<NodeDoc>("nodes");
        Publications = Database.GetCollection<PublicationDoc>("publications");
        Grants = Database.GetCollection<GrantDoc>("grants");
        UsageRecords = Database.GetCollection<UsageRecordDoc>("usageRecords");
        UsageDaily = Database.GetCollection<UsageDailyDoc>("usageDaily");
        Audit = Database.GetCollection<AuditDoc>("auditLog");
    }

    public IMongoClient Client { get; }
    public IMongoDatabase Database { get; }
    public IMongoCollection<UserDoc> Users { get; }
    public IMongoCollection<InviteDoc> Invites { get; }
    public IMongoCollection<RefreshTokenDoc> RefreshTokens { get; }
    public IMongoCollection<ApiKeyDoc> ApiKeys { get; }
    public IMongoCollection<NodeDoc> Nodes { get; }
    public IMongoCollection<PublicationDoc> Publications { get; }
    public IMongoCollection<GrantDoc> Grants { get; }
    public IMongoCollection<UsageRecordDoc> UsageRecords { get; }
    public IMongoCollection<UsageDailyDoc> UsageDaily { get; }
    public IMongoCollection<AuditDoc> Audit { get; }

    public static readonly TimeSpan UsageRetention = TimeSpan.FromDays(30);

    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        await Users.Indexes.CreateOneAsync(new CreateIndexModel<UserDoc>(
            Builders<UserDoc>.IndexKeys.Ascending(u => u.UsernameLower), new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
        await Invites.Indexes.CreateManyAsync([
            new CreateIndexModel<InviteDoc>(Builders<InviteDoc>.IndexKeys.Ascending(i => i.TokenHash),
                new CreateIndexOptions { Unique = true }),
            // Expired links are removed a week after expiry; used ones stay until then for the audit view.
            new CreateIndexModel<InviteDoc>(Builders<InviteDoc>.IndexKeys.Ascending(i => i.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(7) }),
        ], ct);
        await RefreshTokens.Indexes.CreateManyAsync([
            new CreateIndexModel<RefreshTokenDoc>(Builders<RefreshTokenDoc>.IndexKeys.Ascending(t => t.TokenHash),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<RefreshTokenDoc>(Builders<RefreshTokenDoc>.IndexKeys.Ascending(t => t.UserId)),
            new CreateIndexModel<RefreshTokenDoc>(Builders<RefreshTokenDoc>.IndexKeys.Ascending(t => t.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
        ], ct);
        await ApiKeys.Indexes.CreateManyAsync([
            new CreateIndexModel<ApiKeyDoc>(Builders<ApiKeyDoc>.IndexKeys.Ascending(k => k.KeyHash),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<ApiKeyDoc>(Builders<ApiKeyDoc>.IndexKeys.Ascending(k => k.UserId)),
        ], ct);
        await Nodes.Indexes.CreateManyAsync([
            new CreateIndexModel<NodeDoc>(Builders<NodeDoc>.IndexKeys.Ascending(n => n.TokenHash),
                new CreateIndexOptions<NodeDoc>
                {
                    Unique = true,
                    PartialFilterExpression = Builders<NodeDoc>.Filter.Type(n => n.TokenHash, BsonType.String),
                }),
            new CreateIndexModel<NodeDoc>(Builders<NodeDoc>.IndexKeys.Ascending(n => n.Name),
                new CreateIndexOptions { Unique = true }),
        ], ct);
        await Publications.Indexes.CreateManyAsync([
            new CreateIndexModel<PublicationDoc>(Builders<PublicationDoc>.IndexKeys.Ascending(p => p.Alias)),
            new CreateIndexModel<PublicationDoc>(Builders<PublicationDoc>.IndexKeys
                .Ascending(p => p.NodeId).Ascending(p => p.Alias), new CreateIndexOptions { Unique = true }),
        ], ct);
        await Grants.Indexes.CreateManyAsync([
            new CreateIndexModel<GrantDoc>(Builders<GrantDoc>.IndexKeys
                .Ascending(g => g.UserId).Ascending(g => g.PublicationId), new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<GrantDoc>(Builders<GrantDoc>.IndexKeys.Ascending(g => g.PublicationId)),
        ], ct);
        await UsageRecords.Indexes.CreateManyAsync([
            new CreateIndexModel<UsageRecordDoc>(Builders<UsageRecordDoc>.IndexKeys.Ascending(r => r.StartedAt),
                new CreateIndexOptions { ExpireAfter = UsageRetention }),
            new CreateIndexModel<UsageRecordDoc>(Builders<UsageRecordDoc>.IndexKeys
                .Ascending(r => r.UserId).Descending(r => r.StartedAt)),
        ], ct);
        await UsageDaily.Indexes.CreateManyAsync([
            new CreateIndexModel<UsageDailyDoc>(Builders<UsageDailyDoc>.IndexKeys.Ascending(d => d.Day)),
            new CreateIndexModel<UsageDailyDoc>(Builders<UsageDailyDoc>.IndexKeys
                .Ascending(d => d.UserId).Ascending(d => d.Day)),
        ], ct);
        await Audit.Indexes.CreateOneAsync(new CreateIndexModel<AuditDoc>(
            Builders<AuditDoc>.IndexKeys.Descending(a => a.At)), cancellationToken: ct);
    }
}
