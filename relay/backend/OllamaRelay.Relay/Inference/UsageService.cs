using MongoDB.Driver;
using OllamaRelay.Relay.Data;

namespace OllamaRelay.Relay.Inference;

/// <summary>Writes one metadata-only record per inference call plus a daily aggregate.</summary>
public sealed class UsageService(MongoStore store, ILogger<UsageService> logger)
{
    public async Task RecordAsync(UsageRecordDoc record)
    {
        try
        {
            await store.UsageRecords.InsertOneAsync(record);
            var day = record.StartedAt.Date;
            var id = $"{day:yyyy-MM-dd}|{record.UserId}|{record.Alias}|{record.NodeId}";
            var update = Builders<UsageDailyDoc>.Update
                .SetOnInsert(d => d.Day, DateTime.SpecifyKind(day, DateTimeKind.Utc))
                .SetOnInsert(d => d.UserId, record.UserId)
                .SetOnInsert(d => d.Alias, record.Alias)
                .SetOnInsert(d => d.NodeId, record.NodeId)
                .Set(d => d.Username, record.Username)
                .Set(d => d.NodeName, record.NodeName)
                .Inc(d => d.Requests, 1)
                .Inc(d => d.Errors, record.Outcome == "ok" ? 0 : 1)
                .Inc(d => d.PromptTokens, record.PromptTokens ?? 0)
                .Inc(d => d.CompletionTokens, record.CompletionTokens ?? 0)
                .Inc(d => d.DurationMs, record.DurationMs);
            await store.UsageDaily.UpdateOneAsync(d => d.Id == id, update, new UpdateOptions { IsUpsert = true });
        }
        catch (MongoException ex)
        {
            logger.LogError("usage record could not be written: {Error}", ex.GetType().Name);
        }
    }
}
