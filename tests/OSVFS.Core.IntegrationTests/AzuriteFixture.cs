using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Testcontainers.Azurite;
using Xunit;

namespace OSVFS.Core.IntegrationTests;

/// <summary>
/// Boots an Azurite container once per test class collection so suites can share the
/// startup cost. Each test creates its own container under the same Azurite instance
/// to keep tests independent.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    /// <summary>
    /// Default Azurite well-known account that the official image preconfigures.
    /// Connection strings constructed from this account work against any Azurite
    /// container started with the default settings.
    /// </summary>
    public const string AccountName = "devstoreaccount1";

    /// <summary>
    /// Highest <c>x-ms-version</c> the floating Azurite "latest" image (currently
    /// 3.35.0) understands. The Azure SDK ships a newer default with each release;
    /// without pinning, IT requests sent under the SDK default fail at Azurite
    /// with InvalidHeaderValue / unparseable bodies. Update this constant when
    /// Azurite ships support for a higher API version.
    /// </summary>
    public const BlobClientOptions.ServiceVersion BlobServiceVersion =
        BlobClientOptions.ServiceVersion.V2025_11_05;

    /// <summary>
    /// Storage-Queue counterpart of <see cref="BlobServiceVersion"/>. Kept at
    /// the same wire-version so the queue and blob halves of an Azure mount
    /// agree on what Azurite can actually parse.
    /// </summary>
    public const QueueClientOptions.ServiceVersion QueueServiceVersion =
        QueueClientOptions.ServiceVersion.V2025_11_05;

    // Use the floating "latest" Azurite tag so the running container speaks
    // the highest API version Azurite has shipped to date. The Azure SDK's
    // default ServiceVersion still rolls past Azurite's max on every minor
    // bump — the IT pins ServiceVersion via BuildBlobClientOptions /
    // BuildQueueClientOptions instead of trying to make the container catch up.
    private readonly AzuriteContainer container =
        new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .Build();

    /// <summary>
    /// Connection string handed back by the running Azurite container. Carries the
    /// dynamically-mapped Blob endpoint so tests do not have to hard-code 10000.
    /// </summary>
    public string ConnectionString => container.GetConnectionString();

    /// <summary>
    /// Builds a <see cref="BlobClientOptions"/> pinned to the highest API version
    /// the running Azurite supports. IT-side backend constructors must pass this
    /// in so a Dependabot bump of <c>Azure.Storage.Blobs</c> does not silently
    /// roll the SDK's default <c>x-ms-version</c> past Azurite's ceiling.
    /// </summary>
    public static BlobClientOptions BuildBlobClientOptions() =>
        new(BlobServiceVersion);

    /// <summary>
    /// Storage-Queue counterpart of <see cref="BuildBlobClientOptions"/>. Pinned
    /// to <see cref="QueueServiceVersion"/> for the same reason.
    /// </summary>
    public static QueueClientOptions BuildQueueClientOptions() =>
        new(QueueServiceVersion);

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();
}

[CollectionDefinition(AzuriteCollection.Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "Azurite";
}
