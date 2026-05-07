namespace S3Files.Windows.ObjectStore;

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
    public static IObjectStoreBackend Create(
        ObjectStoreProvider provider,
        string bucket,
        string? endpointUrl = null,
        string? keyPrefix = null,
        string? region = null) =>
        provider switch
        {
            ObjectStoreProvider.S3 => new S3.S3Backend(bucket, endpointUrl, keyPrefix, region),
            ObjectStoreProvider.Gcs => throw new NotSupportedException(
                "Google Cloud Storage backend is not yet implemented. " +
                "Currently only --provider s3 is supported."),
            ObjectStoreProvider.AzureBlob => throw new NotSupportedException(
                "Azure Blob Storage backend is not yet implemented. " +
                "Currently only --provider s3 is supported."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider), provider, "Unknown object-store provider."),
        };
}
