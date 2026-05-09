namespace OSVFS.Sync;

/// <summary>
/// Stream of remote-side object changes that the watcher applies through ProjFS.
/// Different implementations cover different discovery strategies: polling
/// re-lists the bucket on a fixed cadence; an SQS-backed source consumes
/// EventBridge S3 notifications. A composite source merges several into one.
/// </summary>
/// <remarks>
/// Implementations are expected to:
/// <list type="bullet">
/// <item>Yield events lazily and indefinitely until the cancellation token fires.</item>
/// <item>Be safe to enumerate exactly once per instance (use a fresh instance for a fresh stream).</item>
/// <item>Translate provider-specific keys into virt-root-relative form before yielding.</item>
/// </list>
/// </remarks>
internal interface IChangeSource : IAsyncDisposable
{
    /// <summary>
    /// Yields remote object changes as they are discovered. The enumerable runs
    /// for the lifetime of <paramref name="ct"/>; cancellation completes the stream.
    /// </summary>
    IAsyncEnumerable<ObjectChangeEvent> WatchAsync(CancellationToken ct);
}

/// <summary>
/// Optional facet implemented by sources that can suppress change events caused
/// by the local virtualization host's own writes. Only sources backed by an
/// in-memory remote snapshot (currently <see cref="PollingChangeSource"/>) need
/// to implement this; sources fed by external pushes (SQS) rely on the watcher's
/// own self-suppression machinery instead.
/// </summary>
internal interface ILocalMutationRecorder
{
    /// <summary>
    /// Records that the host just uploaded <paramref name="objectKey"/> with the
    /// given metadata, so the source's diff baseline reflects the new state.
    /// </summary>
    void RecordLocalUpload(string objectKey, string etag, long size, DateTimeOffset lastModified);

    /// <summary>
    /// Records that the host just deleted <paramref name="objectKey"/>, removing
    /// it from the source's diff baseline.
    /// </summary>
    void RecordLocalDelete(string objectKey);

    /// <summary>
    /// Records that the host just deleted every object under
    /// <paramref name="objectKeyPrefix"/> (slash-terminated or not).
    /// </summary>
    void RecordLocalDeletePrefix(string objectKeyPrefix);

    /// <summary>
    /// Records that the host just renamed a single object, transposing the
    /// snapshot entry.
    /// </summary>
    void RecordLocalRename(string oldKey, string newKey);

    /// <summary>
    /// Records that the host just renamed a directory, retargeting every entry
    /// under the prefix.
    /// </summary>
    void RecordLocalRenamePrefix(string oldPrefix, string newPrefix);

    /// <summary>
    /// Acquires a token that suppresses the source from emitting changes for
    /// <paramref name="objectKey"/> until the token is disposed. Used to bracket
    /// in-flight local mutations so we don't reflect our own writes back.
    /// </summary>
    IDisposable BeginLocalKeyChange(string objectKey);

    /// <summary>
    /// Acquires a token that suppresses the source from emitting changes for
    /// any key under <paramref name="objectKeyPrefix"/> until the token is disposed.
    /// </summary>
    IDisposable BeginLocalPrefixChange(string objectKeyPrefix);
}
