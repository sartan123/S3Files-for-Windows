namespace S3Files.Windows.ObjectStore;

/// <summary>
/// Identifies which object-store provider should back the virtualization root.
/// Surfaced through the <c>--provider</c> CLI flag.
/// </summary>
internal enum ObjectStoreProvider
{
    /// <summary>
    /// Amazon S3 (and S3-compatible services such as LocalStack and MinIO when
    /// combined with <c>--endpoint-url</c>).
    /// </summary>
    S3,

    /// <summary>
    /// Google Cloud Storage. Not yet implemented; selecting this provider fails at startup.
    /// </summary>
    Gcs,

    /// <summary>
    /// Azure Blob Storage. Not yet implemented; selecting this provider fails at startup.
    /// </summary>
    AzureBlob,
}
