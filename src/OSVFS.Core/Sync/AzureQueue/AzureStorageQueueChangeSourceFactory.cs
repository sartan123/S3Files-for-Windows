using Azure;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using OSVFS.ObjectStore;
using OSVFS.ObjectStore.AzureBlob;

namespace OSVFS.Sync.AzureQueue;

/// <summary>
/// Builds a fully-wired <see cref="AzureStorageQueueChangeSource"/> from CLI /
/// config options, constructing the underlying <see cref="QueueClient"/> with
/// the same credential conventions as <c>AzureBlobBackend</c> so a single
/// connection string or token credential drives both.
/// </summary>
internal static class AzureStorageQueueChangeSourceFactory
{
    /// <summary>
    /// Constructs an <see cref="AzureStorageQueueChangeSource"/> bound to
    /// <paramref name="queueUrlOrName"/>. URLs are honored as-is; bare names
    /// are resolved against the storage account named on the supplied
    /// <paramref name="credentials"/> (connection string carries it; SAS /
    /// Managed Identity / DefaultAzureCredential branches name it explicitly).
    /// <paramref name="clientOptions"/> lets callers pin the Storage Queue
    /// SDK's <c>x-ms-version</c> (and configure retry / pipeline policies);
    /// when null the SDK falls back to its newest-default
    /// <c>ServiceVersion</c>. Integration tests pass a pinned-version
    /// instance so Azurite's lagging API support cannot block Dependabot
    /// bumps.
    /// </summary>
    public static AzureStorageQueueChangeSource Create(
        string queueUrlOrName,
        string containerName,
        string? keyPrefix,
        IObjectStoreCredentialSource? credentials,
        ILogger<AzureStorageQueueChangeSource> logger,
        QueueClientOptions? clientOptions = null)
    {
        var azure = NarrowToAzure(credentials);
        var client = BuildQueueClient(queueUrlOrName, azure, clientOptions);
        return new AzureStorageQueueChangeSource(client, containerName, keyPrefix, logger);
    }

    /// <summary>
    /// Narrows the provider-neutral credential seam to the Azure shape this
    /// factory requires. Null is rejected because Storage Queue access always
    /// needs credentials (the SDK has no anonymous-public path for queues
    /// the way Blob does for read-only public containers).
    /// </summary>
    private static AzureCredentialSource NarrowToAzure(IObjectStoreCredentialSource? credentials)
    {
        if (credentials is null)
        {
            throw new InvalidOperationException(
                "Azure Storage Queue change source requires an Azure credential source " +
                "(connection-string / sas / managed-identity / default-azure-credential).");
        }
        if (credentials is AzureCredentialSource azure) return azure;
        throw new InvalidOperationException(
            $"AzureStorageQueueChangeSourceFactory requires credentials of type {nameof(AzureCredentialSource)}, " +
            $"got {credentials.GetType().Name}.");
    }

    /// <summary>
    /// Builds a <see cref="QueueClient"/> for one of the four Azure auth
    /// branches. Mirrors <c>AzureBlobBackend.BuildServiceClient</c> so the
    /// same operator config drives both. <paramref name="clientOptions"/> is
    /// threaded through every constructor branch so a caller-supplied options
    /// instance (e.g. ServiceVersion pin from the IT) reaches the SDK; null
    /// preserves the SDK default behaviour.
    /// </summary>
    private static QueueClient BuildQueueClient(
        string queueUrlOrName,
        AzureCredentialSource credentials,
        QueueClientOptions? clientOptions)
    {
        // Connection string carries account name + endpoints; the SDK can
        // resolve a bare queue name against it.
        if (credentials.ConnectionString is { } connectionString)
        {
            return new QueueClient(connectionString, queueUrlOrName, clientOptions);
        }

        // SAS / TokenCredential branches need an explicit queue URL because
        // we cannot synthesize the queue endpoint from just an account name
        // when the operator might be on a sovereign cloud / Azure Stack with
        // a non-standard suffix. Bare names are rejected here; the operator
        // sees a clear "use the full queue URL" message instead of a 404.
        var queueUri = new Uri(queueUrlOrName, UriKind.Absolute);

        if (credentials.Sas is { } sas)
        {
            return new QueueClient(queueUri, new AzureSasCredential(sas), clientOptions);
        }

        if (credentials.TokenCredential is { } tokenCredential)
        {
            return new QueueClient(queueUri, tokenCredential, clientOptions);
        }

        throw new InvalidOperationException(
            $"Azure credential source '{credentials.Description}' carries no usable branch for the queue client.");
    }
}
