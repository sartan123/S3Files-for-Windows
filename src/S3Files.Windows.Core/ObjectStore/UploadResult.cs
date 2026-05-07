namespace S3Files.Windows.ObjectStore;

/// <summary>
/// Result of a successful upload, used to update the watcher's snapshot so the next
/// poll doesn't re-import our own write. Provider-neutral: each backend maps its
/// native identity fields into ETag / VersionId (S3 ETag + VersionId, GCS etag + generation,
/// Azure ETag + VersionId).
/// </summary>
internal readonly record struct UploadResult(string ETag, string VersionId, long Size, DateTimeOffset LastModified);
