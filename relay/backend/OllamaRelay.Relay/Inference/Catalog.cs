using MongoDB.Bson;
using MongoDB.Driver;
using OllamaRelay.Relay.Data;

namespace OllamaRelay.Relay.Inference;

/// <summary>Alias and grant lookups. Unknown alias and missing grant look exactly the same to callers.</summary>
public sealed class Catalog(MongoStore store)
{
    public async Task<IReadOnlyList<PublicationDoc>> GrantedPublicationsAsync(ObjectId userId, string alias,
        CancellationToken ct)
    {
        var publications = await store.Publications.Find(p => p.Alias == alias).ToListAsync(ct);
        if (publications.Count == 0)
        {
            return [];
        }

        var ids = publications.Select(p => p.Id).ToList();
        var granted = await store.Grants.Find(g => g.UserId == userId && ids.Contains(g.PublicationId))
            .Project(g => g.PublicationId).ToListAsync(ct);
        var set = granted.ToHashSet();
        return publications.Where(p => set.Contains(p.Id)).ToList();
    }

    public async Task<IReadOnlyList<PublicationDoc>> AllGrantedAsync(ObjectId userId, CancellationToken ct)
    {
        var ids = await store.Grants.Find(g => g.UserId == userId).Project(g => g.PublicationId).ToListAsync(ct);
        if (ids.Count == 0)
        {
            return [];
        }

        return await store.Publications.Find(p => ids.Contains(p.Id)).ToListAsync(ct);
    }
}
