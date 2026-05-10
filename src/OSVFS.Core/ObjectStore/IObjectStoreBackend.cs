namespace OSVFS.ObjectStore;

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
    /// Provider-specific operator instructions for enabling versioning on the
    /// linked bucket/container. Surfaced through the
    /// <c>BucketVersioningNotEnabledException</c> message so the operator can
    /// fix the bucket without leaving the terminal. Returned as a single
    /// copy-pasteable command (or multi-line block) — already indented for the
    /// remediation message and free of any surrounding wording.
    /// </summary>
    string GetEnableVersioningInstructions();

    /// <summary>
    /// Combined UTF-8 byte ceiling for user-defined metadata names+values that
    /// the provider accepts on a single object. Surfaced so callers can
    /// pre-validate against the right limit (S3: 2 KiB <c>x-amz-meta-*</c>;
    /// Azure Blob: 8 KiB <c>x-ms-meta-*</c>; GCS: 8 KiB metadata block) before
    /// initiating an upload.
    /// </summary>
    int UserMetadataMaxBytes { get; }

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
    /// <paramref name="userMetadata"/>, when non-null, is forwarded as provider-native
    /// user metadata (S3 <c>x-amz-meta-*</c>). Names are normalized to lowercase before
    /// transmission and the combined size is validated against the provider limit.
    /// </summary>
    Task<UploadResult> UploadAsync(
        string relativePath,
        Stream content,
        string? ifMatchETag,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? userMetadata = null);

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
