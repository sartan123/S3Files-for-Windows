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
    public static IObjectStoreBackend Create(
        ObjectStoreProvider provider,
        string bucket,
        string? endpointUrl = null,
        string? keyPrefix = null,
        string? region = null,
        AwsCredentialSource? credentials = null,
        BandwidthLimits? bandwidth = null,
        long? multipartThresholdBytes = null,
        long? multipartPartSizeBytes = null,
        int? retryMaxAttempts = null)
    {
        var upLimiter = CreateLimiter(bandwidth?.UpBytesPerSecond);
        var downLimiter = CreateLimiter(bandwidth?.DownBytesPerSecond);
        return provider switch
        {
            ObjectStoreProvider.S3 => new S3.S3Backend(
                bucket, endpointUrl, keyPrefix, region, credentials, upLimiter, downLimiter,
                multipartThresholdBytes, multipartPartSizeBytes, retryMaxAttempts),
            ObjectStoreProvider.Gcs => DisposeAndThrow(upLimiter, downLimiter, new NotSupportedException(
                "Google Cloud Storage backend is not yet implemented. " +
                "Currently only --provider s3 is supported.")),
            ObjectStoreProvider.AzureBlob => DisposeAndThrow(upLimiter, downLimiter, new NotSupportedException(
                "Azure Blob Storage backend is not yet implemented. " +
                "Currently only --provider s3 is supported.")),
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
}
