using Testcontainers.MongoDb;

namespace OllamaRelay.IntegrationTests;

public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:8").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("relay")]
public sealed class RelayCollection : ICollectionFixture<MongoFixture>;
