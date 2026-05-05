namespace S3Files.Windows.S3;

internal readonly record struct UploadResult(string ETag, string VersionId, long Size, DateTimeOffset LastModified);

internal interface IS3Backend
{
    IAsyncEnumerable<S3ObjectInfo> ListAsync(string relativeDirectory, CancellationToken ct);

    IAsyncEnumerable<S3ObjectInfo> ListAllAsync(CancellationToken ct);

    IAsyncEnumerable<S3ObjectInfo> ListRecursiveAsync(string relativeDirectory, CancellationToken ct);

    Task<S3ObjectInfo?> HeadAsync(string relativePath, CancellationToken ct);

    Task ReadRangeAsync(string relativePath, long offset, long length, Stream destination, CancellationToken ct);

    Task<UploadResult> UploadAsync(string relativePath, Stream content, string? ifMatchETag, CancellationToken ct);

    Task DeleteAsync(string relativePath, CancellationToken ct);

    Task DeletePrefixAsync(string relativeDirectory, CancellationToken ct);

    Task RenameAsync(string oldRelativePath, string newRelativePath, CancellationToken ct);

    Task RenamePrefixAsync(string oldRelativeDirectory, string newRelativeDirectory, CancellationToken ct);
}
