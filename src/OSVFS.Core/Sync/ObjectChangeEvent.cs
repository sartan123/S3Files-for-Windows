namespace OSVFS.Sync;

/// <summary>
/// What kind of change a remote object underwent. Polling and SQS both collapse
/// "created" and "modified" into <see cref="Upserted"/> because S3 events do not
/// distinguish them and the watcher's apply path treats both the same way
/// (try-update → fall back to write-placeholder).
/// </summary>
internal enum ObjectChangeKind
{
    /// <summary>An object appeared or changed; placeholder must be created or refreshed.</summary>
    Upserted,

    /// <summary>An object disappeared; placeholder must be removed.</summary>
    Deleted,
}

/// <summary>
/// Single remote-side change emitted by an <see cref="IChangeSource"/>. Carries
/// just enough metadata for the watcher to act without re-fetching the object.
/// </summary>
/// <param name="Kind">Whether the object was upserted or deleted.</param>
/// <param name="Key">Virt-root-relative object key (linked prefix already stripped, forward-slash separated).</param>
/// <param name="RelativePath">Same key in Windows path form (backslash-separated).</param>
/// <param name="Size">Object size in bytes; <c>0</c> for deletes.</param>
/// <param name="LastModified">Timestamp of the change; <c>default</c> when unknown (deletes from EventBridge).</param>
/// <param name="ETag">Provider entity tag of the new content; empty for deletes.</param>
internal readonly record struct ObjectChangeEvent(
    ObjectChangeKind Kind,
    string Key,
    string RelativePath,
    long Size,
    DateTimeOffset LastModified,
    string ETag);
