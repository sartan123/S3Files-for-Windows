using Microsoft.Extensions.Logging;
using OSVFS.ObjectStore;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace OSVFS.Sync;

/// <summary>
/// <see cref="IChangeSource"/> that discovers remote changes by re-listing the
/// linked bucket on a fixed cadence and diffing the result against an in-memory
/// snapshot. Owns the snapshot dictionary and the local-mutation tokens that
/// keep self-uploads from re-importing as remote changes.
/// </summary>
/// <remarks>
/// Equivalent to the original <c>ObjectStoreChangeWatcher</c> polling loop, now
/// reshaped as a change-event source. Diff computation is unchanged.
/// </remarks>
internal sealed class PollingChangeSource : IChangeSource, ILocalMutationRecorder
{
    private readonly IObjectStoreBackend backend;
    private readonly TimeSpan interval;
    private readonly ILogger<PollingChangeSource> logger;

    /// <summary>
    /// Object-key → last-known state. Updated on poll diffs and on local mutations
    /// recorded via <see cref="RecordLocalUpload"/> / <see cref="RecordLocalDelete"/> /
    /// <see cref="RecordLocalRename"/> so the next poll doesn't re-import our own writes.
    /// </summary>
    private readonly ConcurrentDictionary<string, ObjectSnapshot> snapshot =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Object keys whose mutation is currently in flight from the local side. The poll
    /// loop ignores these to avoid a "we just uploaded → remote has a new ETag → revert local
    /// because we haven't recorded the new ETag yet" race.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> localKeysInFlight =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Active prefix-scoped local mutations (delete-prefix, rename-prefix). Keys
    /// matching any registered prefix are ignored by the poll loop. Stored as the
    /// trailing-slash-normalized prefix.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> localPrefixesInFlight =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a polling source that re-lists <paramref name="backend"/> every
    /// <paramref name="interval"/>. A non-positive interval means
    /// <see cref="WatchAsync"/> primes the snapshot once and then yields nothing,
    /// effectively disabling the source while leaving local-mutation accounting
    /// intact for tests.
    /// </summary>
    public PollingChangeSource(
        IObjectStoreBackend backend,
        TimeSpan interval,
        ILogger<PollingChangeSource> logger)
    {
        this.backend = backend;
        this.interval = interval;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectChangeEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await PrimeSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to take initial object-store snapshot; continuing with empty baseline.");
        }

        if (interval <= TimeSpan.Zero)
        {
            logger.LogInformation(
                "Polling change source disabled (interval is {Interval}).", interval);
            yield break;
        }

        logger.LogInformation(
            "Polling change source started (interval = {Interval}).", interval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { yield break; }

            List<ObjectChangeEvent>? batch;
            try
            {
                batch = await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { yield break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during object-store change poll; will retry next cycle.");
                continue;
            }

            foreach (var ev in batch)
            {
                yield return ev;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public void RecordLocalUpload(string objectKey, string etag, long size, DateTimeOffset lastModified)
    {
        if (string.IsNullOrEmpty(objectKey)) return;
        snapshot[objectKey] = new ObjectSnapshot(etag ?? string.Empty, size, lastModified);
    }

    /// <inheritdoc/>
    public void RecordLocalDelete(string objectKey)
    {
        if (string.IsNullOrEmpty(objectKey)) return;
        snapshot.TryRemove(objectKey, out _);
    }

    /// <inheritdoc/>
    public void RecordLocalDeletePrefix(string objectKeyPrefix)
    {
        if (string.IsNullOrEmpty(objectKeyPrefix)) return;
        var prefix = EnsureTrailingSlash(objectKeyPrefix);
        foreach (var key in snapshot.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                snapshot.TryRemove(key, out _);
            }
        }
    }

    /// <inheritdoc/>
    public void RecordLocalRename(string oldKey, string newKey)
    {
        if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey)) return;
        if (snapshot.TryRemove(oldKey, out var snap))
        {
            snapshot[newKey] = snap;
        }
    }

    /// <inheritdoc/>
    public void RecordLocalRenamePrefix(string oldPrefix, string newPrefix)
    {
        if (string.IsNullOrEmpty(oldPrefix) || string.IsNullOrEmpty(newPrefix)) return;
        var oldP = EnsureTrailingSlash(oldPrefix);
        var newP = EnsureTrailingSlash(newPrefix);
        foreach (var (key, snap) in snapshot)
        {
            if (key.StartsWith(oldP, StringComparison.Ordinal))
            {
                var moved = newP + key[oldP.Length..];
                snapshot.TryRemove(key, out _);
                snapshot[moved] = snap;
            }
        }
    }

    /// <inheritdoc/>
    public IDisposable BeginLocalKeyChange(string objectKey)
    {
        if (string.IsNullOrEmpty(objectKey)) return EmptyDisposable.Instance;
        localKeysInFlight[objectKey] = 0;
        return new ReleaseToken(() => localKeysInFlight.TryRemove(objectKey, out _));
    }

    /// <inheritdoc/>
    public IDisposable BeginLocalPrefixChange(string objectKeyPrefix)
    {
        if (string.IsNullOrEmpty(objectKeyPrefix)) return EmptyDisposable.Instance;
        var prefix = EnsureTrailingSlash(objectKeyPrefix);
        localPrefixesInFlight[prefix] = 0;
        return new ReleaseToken(() => localPrefixesInFlight.TryRemove(prefix, out _));
    }

    /// <summary>
    /// Runs a single reconciliation cycle. Exposed for tests so they can drive
    /// the diff loop deterministically without spawning a background task.
    /// </summary>
    internal async Task<List<ObjectChangeEvent>> PollOnceAsync(CancellationToken ct)
    {
        var events = new List<ObjectChangeEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var obj in backend.ListAllAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(obj.Key);
            if (IsLocallyInFlight(obj.Key)) continue;

            if (snapshot.TryGetValue(obj.Key, out var prev))
            {
                if (HasChanged(prev, obj))
                {
                    events.Add(BuildUpserted(obj));
                    snapshot[obj.Key] = ObjectSnapshot.From(obj);
                }
            }
            else
            {
                events.Add(BuildUpserted(obj));
                snapshot[obj.Key] = ObjectSnapshot.From(obj);
            }
        }

        // Anything in the previous snapshot that wasn't seen this cycle is a remote delete.
        foreach (var key in snapshot.Keys)
        {
            if (seen.Contains(key)) continue;
            if (IsLocallyInFlight(key)) continue;
            events.Add(new ObjectChangeEvent(
                Kind: ObjectChangeKind.Deleted,
                Key: key,
                RelativePath: KeyPath.ToRelativePath(key),
                Size: 0,
                LastModified: default,
                ETag: string.Empty));
            snapshot.TryRemove(key, out _);
        }

        return events;
    }

    /// <summary>
    /// Visible for tests: take an initial snapshot without emitting any events.
    /// </summary>
    internal async Task PrimeSnapshotAsync(CancellationToken ct)
    {
        await foreach (var obj in backend.ListAllAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            snapshot[obj.Key] = ObjectSnapshot.From(obj);
        }
        logger.LogDebug(
            "Initial object-store snapshot primed with {Count} object(s).", snapshot.Count);
    }

    /// <summary>
    /// True when the key (or any registered prefix it falls under) currently has a
    /// local mutation in flight that the poll loop should ignore.
    /// </summary>
    private bool IsLocallyInFlight(string objectKey)
    {
        if (localKeysInFlight.ContainsKey(objectKey)) return true;
        foreach (var prefix in localPrefixesInFlight.Keys)
        {
            if (objectKey.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Projects an <see cref="ObjectInfo"/> into the watcher-facing event shape.
    /// </summary>
    private static ObjectChangeEvent BuildUpserted(ObjectInfo obj) => new(
        Kind: ObjectChangeKind.Upserted,
        Key: obj.Key,
        RelativePath: obj.RelativePath,
        Size: obj.Size,
        LastModified: obj.LastModified,
        ETag: obj.ETag);

    /// <summary>
    /// Returns the input as a slash-terminated key prefix, leaving already-terminated
    /// prefixes untouched.
    /// </summary>
    private static string EnsureTrailingSlash(string keyPrefix) =>
        keyPrefix.EndsWith('/') ? keyPrefix : keyPrefix + '/';

    /// <summary>
    /// True when the previous and current snapshots disagree on identity. ETag is
    /// the primary signal; size and last-modified act as fallback for blank ETags.
    /// </summary>
    private static bool HasChanged(ObjectSnapshot prev, ObjectInfo current)
    {
        // ETag is the primary signal; size/lastModified are tie-breakers when ETag is missing
        // (e.g., some S3-compatible servers return blank ETags for multipart uploads).
        if (!string.IsNullOrEmpty(prev.ETag) && !string.IsNullOrEmpty(current.ETag))
        {
            return !string.Equals(prev.ETag, current.ETag, StringComparison.Ordinal);
        }
        return prev.Size != current.Size || prev.LastModified != current.LastModified;
    }

    /// <summary>
    /// Minimal projection of an object stored in the in-memory snapshot.
    /// </summary>
    private readonly record struct ObjectSnapshot(string ETag, long Size, DateTimeOffset LastModified)
    {
        /// <summary>
        /// Projects an <see cref="ObjectInfo"/> into the snapshot shape.
        /// </summary>
        public static ObjectSnapshot From(ObjectInfo info) =>
            new(info.ETag, info.Size, info.LastModified);
    }

    /// <summary>
    /// Disposable that runs an action exactly once on first dispose.
    /// </summary>
    private sealed class ReleaseToken(Action onDispose) : IDisposable
    {
        private Action? onDispose = onDispose;

        /// <summary>
        /// Invokes the registered action, atomically clearing it so subsequent
        /// dispose calls are no-ops.
        /// </summary>
        public void Dispose()
        {
            var action = Interlocked.Exchange(ref onDispose, null);
            action?.Invoke();
        }
    }

    /// <summary>
    /// Singleton no-op disposable returned when the caller passed an empty key.
    /// </summary>
    private sealed class EmptyDisposable : IDisposable
    {
        /// <summary>
        /// Shared instance — the type is stateless.
        /// </summary>
        public static readonly EmptyDisposable Instance = new();

        /// <summary>
        /// No-op.
        /// </summary>
        public void Dispose() { }
    }
}
