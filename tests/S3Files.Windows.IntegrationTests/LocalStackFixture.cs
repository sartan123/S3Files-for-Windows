using Testcontainers.LocalStack;
using Xunit;

namespace S3Files.Windows.IntegrationTests;

/// <summary>
/// Boots a LocalStack container once per test class collection so suites can share the
/// (relatively expensive) container startup. Each test is responsible for creating its
/// own bucket to keep tests independent.
/// </summary>
public sealed class LocalStackFixture : IAsyncLifetime
{
    private readonly LocalStackContainer container =
        new LocalStackBuilder("localstack/localstack:3").Build();

    public string ServiceUrl => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await container.DisposeAsync();
    }
}

[CollectionDefinition(LocalStackCollection.Name)]
public sealed class LocalStackCollection : ICollectionFixture<LocalStackFixture>
{
    public const string Name = "LocalStack";
}
