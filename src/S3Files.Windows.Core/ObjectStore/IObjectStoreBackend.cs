namespace S3Files.Windows.ObjectStore;

/// <summary>
/// Abstraction over the subset of object-store operations the virtualization layer requires.
/// Path arguments are virt-root-relative; the implementation is responsible for prepending
/// any configured key prefix and translating between Windows-style relative paths and the
/// underlying provider's key/blob name convention.
/// </summary>
/// <remarks>
/// Implementations typically own SDK clients and HTTP connections, so the interface
/// extends <see cref="IDisposable"/> to give the host a single seam for releasing them.
/// </remarks>
internal interface IObjectStoreBackend : IDisposable
{
    /// <summary>
    /// Enumerates immediate children of <paramref name="relativeDirectory"/> using
    /// the "/" delimiter, yielding both real objects and synthesized directory entries.
    /// </summary>
    IAsyncEnumerable<ObjectInfo> ListAsync(string relativeDirectory, CancellationToken ct);

    /// <summary>
    /// Enumerates every object under the linked prefix (no delimiter). Used by the
    /// change watcher to take a full snapshot.
    /// </summary>
    IAsyncEnumerable<ObjectInfo> ListAllAsync(CancellationToken ct);

    /// <summary>
    /// Recursively enumerates every object beneath <paramref name="relativeDirectory"/>
    /// (no delimiter), yielding only real objects.
    /// </summary>
    IAsyncEnumerable<ObjectInfo> ListRecursiveAsync(string relativeDirectory, CancellationToken ct);

    /// <summary>
    /// Reads the bucket/container's current versioning status.
    /// </summary>
    Task<BucketVersioningStatus> GetBucketVersioningStatusAsync(CancellationToken ct);

    /// <summary>
    /// Returns metadata for a single object, or a synthesized directory entry if the
    /// path corresponds to a common prefix; null when nothing matches.
    /// </summary>
    Task<ObjectInfo?> HeadAsync(string relativePath, CancellationToken ct);

    /// <summary>
    /// Streams a byte range of an object into <paramref name="destination"/>.
    /// </summary>
    Task ReadRangeAsync(string relativePath, long offset, long length, Stream destination, CancellationToken ct);

    /// <summary>
    /// Uploads <paramref name="content"/> as the named object. When
    /// <paramref name="ifMatchETag"/> is supplied the upload uses an If-Match precondition
    /// to fail on stale local copies; otherwise the implementation chooses the
    /// most efficient transport (single PUT / multipart / resumable / block blob).
    /// </summary>
    Task<UploadResult> UploadAsync(string relativePath, Stream content, string? ifMatchETag, CancellationToken ct);

    /// <summary>
    /// Deletes a single object. Missing keys are treated as success.
    /// </summary>
    Task DeleteAsync(string relativePath, CancellationToken ct);

    /// <summary>
    /// Deletes every object beneath the given directory. Implementations may batch.
    /// </summary>
    Task DeletePrefixAsync(string relativeDirectory, CancellationToken ct);

    /// <summary>
    /// Renames a single object via copy + delete (or the provider's native equivalent).
    /// </summary>
    Task RenameAsync(string oldRelativePath, string newRelativePath, CancellationToken ct);

    /// <summary>
    /// Renames every object under a directory by copying each to the new prefix and
    /// deleting the originals.
    /// </summary>
    Task RenamePrefixAsync(string oldRelativeDirectory, string newRelativeDirectory, CancellationToken ct);
}
