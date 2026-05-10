using OSVFS.Net;

namespace OSVFS.ObjectStore;

/// <summary>
/// Constructs the right <see cref="IObjectStoreBackend"/> implementation for the
/// requested <see cref="ObjectStoreProvider"/>. Centralizing the dispatch keeps the
/// virtualization host provider-agnostic and gives a single seam for adding GCS / Azure
/// implementations later.
/// </summary>
internal static class ObjectStoreBackendFactory
{
    /// <summary>
    /// Builds a backend bound to <paramref name="bucket"/> for the requested provider.
    /// Unimplemented providers throw <see cref="NotSupportedException"/> with a message
    /// that names the provider, so failures at startup are immediately actionable.
    /// </summary>
    /// <param name="provider">Which object-store implementation to instantiate.</param>
    /// <param name="bucket">Bucket / container name (provider-specific).</param>
    /// <param name="endpointUrl">Optional endpoint override (mainly for S3-compatible servers).</param>
    /// <param name="keyPrefix">Optional key prefix; only objects beneath it are projected.</param>
    /// <param name="region">Optional region / location.</param>
    /// <param name="credentials">Optional credential source (OSVFS DPAPI static or SDK-resolved); when null the SDK's default chain is used.</param>
    /// <param name="bandwidth">Optional per-direction transfer ceilings.</param>
    /// <param name="multipartThresholdBytes">Override for the multipart upload threshold; null uses the backend default.</param>
    /// <param name="multipartPartSizeBytes">Override for the multipart upload part size; null uses the backend default.</param>
    /// <param name="retryMaxAttempts">Total attempts (initial + retries) the SDK makes on transient failures; null uses the backend default.</param>
    /// <param name="maxConcurrentUploads">Maximum number of in-flight upload API calls; null uses the backend default.</param>
    /// <param name="maxConcurrentDownloads">Maximum number of in-flight download API calls; null uses the backend default.</param>
    /// <param name="maxMultipartParts">Maximum number of multipart parts uploaded in parallel inside a single upload; null uses the backend default.</param>
    /// <param name="refreshNotifier">Optional sink for ExpiredToken-driven credential refresh outcomes; null disables operator-facing notifications.</param>
    public static IObjectStoreBackend Create(
        ObjectStoreProvider provider,
        string bucket,
        string? endpointUrl = null,
        string? keyPrefix = null,
        string? region = null,
        IObjectStoreCredentialSource? credentials = null,
        BandwidthLimits? bandwidth = null,
        long? multipartThresholdBytes = null,
        long? multipartPartSizeBytes = null,
        int? retryMaxAttempts = null,
        int? maxConcurrentUploads = null,
        int? maxConcurrentDownloads = null,
        int? maxMultipartParts = null,
        ICredentialRefreshNotifier? refreshNotifier = null)
    {
        var upLimiter = CreateLimiter(bandwidth?.UpBytesPerSecond);
        var downLimiter = CreateLimiter(bandwidth?.DownBytesPerSecond);
        return provider switch
        {
            ObjectStoreProvider.S3 => new S3.S3Backend(
                bucket, endpointUrl, keyPrefix, region,
                CastCredentials<AwsCredentialSource>(credentials, provider),
                upLimiter, downLimiter,
                multipartThresholdBytes, multipartPartSizeBytes, retryMaxAttempts,
                maxConcurrentUploads, maxConcurrentDownloads, maxMultipartParts, refreshNotifier),
            ObjectStoreProvider.Gcs => DisposeAndThrow(upLimiter, downLimiter, new NotSupportedException(
                "Google Cloud Storage backend is not yet implemented. " +
                "Currently only --provider s3 is supported.")),
            ObjectStoreProvider.AzureBlob => CreateAzureBlobBackend(
                bucket, endpointUrl, keyPrefix, credentials, upLimiter, downLimiter, provider),
            _ => DisposeAndThrow(upLimiter, downLimiter, new ArgumentOutOfRangeException(
                nameof(provider), provider, "Unknown object-store provider.")),
        };
    }

    /// <summary>
    /// Builds a token-bucket limiter when <paramref name="bytesPerSecond"/> is set
    /// to a positive value; null otherwise.
    /// </summary>
    private static TokenBucketRateLimiter? CreateLimiter(long? bytesPerSecond) =>
        bytesPerSecond is > 0 ? new TokenBucketRateLimiter(bytesPerSecond.Value) : null;

    /// <summary>
    /// Narrows a generic <see cref="IObjectStoreCredentialSource"/> down to the
    /// concrete type the active backend expects. Returns null when the host
    /// did not supply credentials (the backend then falls back to the SDK
    /// default chain). Throws <see cref="InvalidOperationException"/> when the
    /// supplied credentials carry the wrong shape — that is a host/config
    /// wiring bug (e.g. GCS credentials handed to an S3 mount) and must
    /// surface immediately rather than cause an opaque failure later.
    /// </summary>
    private static T? CastCredentials<T>(
        IObjectStoreCredentialSource? credentials, ObjectStoreProvider provider)
        where T : class, IObjectStoreCredentialSource
    {
        if (credentials is null) return null;
        if (credentials is T typed) return typed;
        throw new InvalidOperationException(
            $"Provider '{provider}' requires credentials of type {typeof(T).Name}, " +
            $"got {credentials.GetType().Name}.");
    }

    /// <summary>
    /// Helper used in the unsupported-provider switch arms to dispose any
    /// already-allocated limiters before propagating the failure.
    /// </summary>
    private static IObjectStoreBackend DisposeAndThrow(
        TokenBucketRateLimiter? up, TokenBucketRateLimiter? down, Exception ex)
    {
        up?.Dispose();
        down?.Dispose();
        throw ex;
    }

    /// <summary>
    /// Builds the Azure Blob arm. Bandwidth limiters are accepted on the
    /// factory surface for symmetry with S3 but are not yet plumbed through
    /// the Azure backend (Step 2D wires them into the Block Blob commit
    /// path). They are disposed here so the host does not leak them.
    /// </summary>
    private static AzureBlob.AzureBlobBackend CreateAzureBlobBackend(
        string bucket,
        string? endpointUrl,
        string? keyPrefix,
        IObjectStoreCredentialSource? credentials,
        TokenBucketRateLimiter? upLimiter,
        TokenBucketRateLimiter? downLimiter,
        ObjectStoreProvider provider)
    {
        // Step 2A backend ignores rate limiters; dispose so they don't leak.
        // Step 2D will move them into the AzureBlobBackend constructor.
        upLimiter?.Dispose();
        downLimiter?.Dispose();
        return new AzureBlob.AzureBlobBackend(
            bucket,
            CastCredentials<AzureBlob.AzureCredentialSource>(credentials, provider),
            endpointUrl,
            keyPrefix);
    }
}
