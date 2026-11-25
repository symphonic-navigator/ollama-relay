using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace OllamaRelay.IntegrationTests;

/// <summary>
/// Hard requirement: prompt and completion content never ends up in a log line or a database document.
/// A unique canary goes through every path, including errors and a cancelled stream, and is then searched for
/// in all relay logs, all node logs and every collection.
/// </summary>
[Collection("relay")]
public sealed class CanaryTests(MongoFixture mongo)
{
    [Fact]
    public async Task Canary_never_reaches_logs_or_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var canary = "CANARY-" + Guid.NewGuid().ToString("N");
        await using var stack = await Stack.StartAsync(mongo);
        await stack.StartNodeAsync();
        await stack.PublishAsync("fake-model:7b", "lit-writer");
        await stack.PublishAsync("fake-slow:1b", "slow");
        await stack.PublishAsync("fake-fail:1b", "broken");
        var user = await stack.CreateUserAsync("carol", "lit-writer", "slow", "broken");

        async Task<string> Send(string path, object body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.ApiKey);
            using var res = await stack.Http.SendAsync(req, ct);
            return await res.Content.ReadAsStringAsync(ct);
        }

        var messages = new[] { new { role = "user", content = canary } };

        // Both dialects, streamed and not.
        Assert.Contains(canary, await Send("/api/chat", new { model = "lit-writer", messages }));
        Assert.Contains(canary, await Send("/api/chat", new { model = "lit-writer", stream = false, messages }));
        Assert.Contains(canary, await Send("/api/generate", new { model = "lit-writer", prompt = canary }));
        Assert.Contains(canary, await Send("/v1/chat/completions", new { model = "lit-writer", stream = true, messages }));
        Assert.Contains(canary, await Send("/v1/chat/completions", new { model = "lit-writer", messages }));
        await Send("/api/embed", new { model = "lit-writer", input = canary });
        await Send("/v1/embeddings", new { model = "lit-writer", input = canary });

        // Error path: the upstream error body quotes the prompt. The client gets it, the logs do not.
        Assert.Contains(canary, await Send("/api/chat", new { model = "broken", messages }));
        Assert.Contains(canary, await Send("/v1/chat/completions", new { model = "broken", messages }));

        // Bad requests and refused models.
        using (var bad = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
               {
                   Content = new StringContent("{\"model\": \"lit-writer\", \"messages\": [" + canary, Encoding.UTF8,
                       "application/json"),
               })
        {
            bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.ApiKey);
            using var _ = await stack.Http.SendAsync(bad, ct);
        }

        await Send("/api/chat", new { model = "not-granted", messages });
        await Send("/api/pull", new { model = canary });

        // Cancelled stream.
        stack.Ollama.ResetSlow();
        using (var cts = new CancellationTokenSource())
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
            {
                Content = JsonContent.Create(new { model = "slow", messages }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.ApiKey);
            var res = await stack.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var stream = await res.Content.ReadAsStreamAsync(ct);
            var buf = new byte[512];
            _ = await stream.ReadAsync(buf, ct);
            await cts.CancelAsync();
            res.Dispose();
            await stack.Ollama.SlowAborted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }

        // Let metering and heartbeats settle, then search everywhere.
        await Task.Delay(1500, ct);
        await Stack.WaitForAsync(async () => await stack.Store.UsageRecords.CountDocumentsAsync(
            FilterDefinition<Relay.Data.UsageRecordDoc>.Empty, cancellationToken: ct) >= 9);

        Assert.True(stack.RelayLogs.Lines.Count > 20, "relay logs were not captured");
        Assert.True(stack.NodeLogs.Lines.Count > 5, "node logs were not captured");
        Assert.DoesNotContain(canary, stack.RelayLogs.All);
        Assert.DoesNotContain(canary, stack.NodeLogs.All);

        var db = new MongoClient(stack.MongoUrl).GetDatabase(stack.Database);
        var names = await (await db.ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        Assert.Contains("usageRecords", names);
        foreach (var name in names)
        {
            var docs = await db.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync(ct);
            foreach (var doc in docs)
            {
                Assert.DoesNotContain(canary, doc.ToJson());
            }
        }
    }
}
