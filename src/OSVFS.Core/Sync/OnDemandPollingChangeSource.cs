using Microsoft.Extensions.Logging;
using OSVFS.ObjectStore;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace OSVFS.Sync;

/// <summary>
/// <see cref="IChangeSource"/> that re-lists only the directories the user has
/// actually visited (registered via <see cref="RegisterWatchedDirectory"/>),
/// matching the AWS S3 Files "metadata import on first access" model. API cost
/// scales with the visited-directory count rather than total bucket size, so
/// large buckets remain workable for Phase&#160;1.
/// </summary>
/// <remarks>
/// Trade-offs (see README — On-demand sync):
/// <list type="bullet">
///   <item>External changes in unvisited directories are not propagated until
///         the user enumerates them (and the on-demand list at that moment shows
///         the latest).</item>
///   <item>The watch set is monotonic — directories registered through ProjFS
///         enumerations are not evicted, mirroring the S3 Files "metadata stays
///         even when data is evicted" behavior.</item>
///   <item>Registering a directory implicitly registers every ancestor up to
///         the root, so the parent listings reconcile alongside.</item>
/// </list>
/// </remarks>
internal sealed class OnDemandPollingChangeSource :
    IChangeSource, ILocalMutationRecorder, IDirectoryWatchRegistrar
{
    private readonly IObjectStoreBackend backend;
    private readonly TimeSpan interval;
    private readonly ILogger<OnDemandPollingChangeSource> logger;

    /// <summary>
    /// Per-directory state. Key is the directory's object-key form
    /// (forward-slash separated, no trailing slash; empty string = root).
    /// </summary>
    private readonly ConcurrentDictionary<string, DirectoryWatchState> watched =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Object keys whose mutation is currently in flight from the local side. Reconcile
    /// loops ignore these to avoid reflecting our own writes back as remote events.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> localKeysInFlight =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Active prefix-scoped local mutations (delete-prefix, rename-prefix) — keys
    /// matching any registered prefix are ignored by the reconcile loop. Stored
    /// as the trailing-slash-normalized prefix.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> localPrefixesInFlight =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a source that re-lists each watched directory every
    /// <paramref name="interval"/>. A non-positive interval disables periodic
    /// polling — <see cref="WatchAsync"/> simply yields nothing and waits for
    /// cancellation, but the watch-set bookkeeping (and unit-test entry points)
    /// stay usable.
    /// </summary>
    public OnDemandPollingChangeSource(
        IObjectStoreBackend backend,
        TimeSpan interval,
        ILogger<OnDemandPollingChangeSource> logger)
    {
        this.backend = backend;
        this.interval = interval;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public int WatchedDirectoryCount => watched.Count;

    /// <inheritdoc/>
    public void RegisterWatchedDirectory(string relativeDirectory)
    {
        var dir = NormalizeRelativeDirectory(relativeDirectory);

        // Walk ancestor chain (root -> dir) so a single visit registers every parent
        // listing too. The order doesn't matter for correctness but root-first reads
        // more naturally in trace logs.
        foreach (var ancestor in EnumerateAncestorChain(dir))
        {
            if (watched.TryAdd(ancestor, new DirectoryWatchState()))
            {
                logger.LogDebug("Registered watched directory '{Directory}'.", ancestor);
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectChangeEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (interval <= TimeSpan.Zero)
        {
            logger.LogInformation(
                "On-demand polling change source disabled (interval is {Interval}).", interval);
            yield break;
        }

        logger.LogInformation(
            "On-demand polling change source started (interval = {Interval}).", interval);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { yield break; }

            List<ObjectChangeEvent>? events;
            try
            {
                events = await ReconcileAllAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { yield break; }
            catch (Exception ex)
            {
                logger.LogError(
                    ex, "Error during on-demand reconcile pass; will retry next cycle.");
                continue;
            }

            foreach (var ev in events)
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
        var (parentDir, leaf) = SplitParent(objectKey);
        if (watched.TryGetValue(parentDir, out var state))
        {
            state.Children[leaf] = new ChildSnapshot(
                IsDirectory: false,
                ETag: etag ?? string.Empty,
                Size: size,
                LastModified: lastModified);
        }
    }

    /// <inheritdoc/>
    public void RecordLocalDelete(string objectKey)
    {
        if (string.IsNullOrEmpty(objectKey)) return;
        var (parentDir, leaf) = SplitParent(objectKey);
        if (watched.TryGetValue(parentDir, out var state))
        {
            state.Children.TryRemove(leaf, out _);
        }
    }

    /// <inheritdoc/>
    public void RecordLocalDeletePrefix(string objectKeyPrefix)
    {
        if (string.IsNullOrEmpty(objectKeyPrefix)) return;
        var prefix = EnsureTrailingSlash(objectKeyPrefix);
        // Drop every cached entry sitting under the deleted prefix; the reconcile
        // loop will rediscover anything that legitimately survives.
        foreach (var (dir, state) in watched)
        {
            foreach (var leaf in state.Children.Keys.ToArray())
            {
                var fullKey = JoinKey(dir, leaf);
                if (fullKey.StartsWith(prefix, StringComparison.Ordinal))
                {
                    state.Children.TryRemove(leaf, out _);
                }
            }
        }
    }

    /// <inheritdoc/>
    public void RecordLocalRename(string oldKey, string newKey)
    {
        if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey)) return;
        ChildSnapshot? carried = null;
        var (oldDir, oldLeaf) = SplitParent(oldKey);
        if (watched.TryGetValue(oldDir, out var oldState)
            && oldState.Children.TryRemove(oldLeaf, out var snap))
        {
            carried = snap;
        }
        var (newDir, newLeaf) = SplitParent(newKey);
        if (watched.TryGetValue(newDir, out var newState))
        {
            newState.Children[newLeaf] = carried ?? new ChildSnapshot(false, string.Empty, 0, default);
        }
    }

    /// <inheritdoc/>
    public void RecordLocalRenamePrefix(string oldPrefix, string newPrefix)
    {
        // The on-demand source's snapshot is per visited directory, not a flat
        // map keyed by full object path, so the cleanest reset is to drop every
        // entry under the old prefix and let the next tick rediscover the new
        // layout. The local-mutation token registered via
        // BeginLocalPrefixChange suppresses self-events in the meantime.
        RecordLocalDeletePrefix(oldPrefix);
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
    /// Reconciles every currently-watched directory once, returning every event
    /// emitted across the pass. Exposed for unit tests so they can drive the
    /// diff loop deterministically without spawning a background task.
    /// </summary>
    internal async Task<List<ObjectChangeEvent>> ReconcileAllAsync(CancellationToken ct)
    {
        var events = new List<ObjectChangeEvent>();

        // Snapshot the watched-directory keys so that newly-registered directories
        // (added concurrently from a ProjFS enumeration) don't cause an iterator
        // mutation; they'll be picked up next tick.
        foreach (var directory in watched.Keys.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            await ReconcileDirectoryAsync(directory, events, ct).ConfigureAwait(false);
        }

        return events;
    }

    /// <summary>
    /// Reconciles a single directory: lists its immediate children with delimiter
    /// '/', diffs the file entries against the cached snapshot, and appends the
    /// resulting upsert/delete events to <paramref name="events"/>.
    /// </summary>
    internal async Task ReconcileDirectoryAsync(
        string directory, List<ObjectChangeEvent> events, CancellationToken ct)
    {
        if (!watched.TryGetValue(directory, out var state)) return;

        var seenLeaves = new HashSet<string>(StringComparer.Ordinal);
        var relativeDir = KeyPath.ToRelativePath(directory);

        await foreach (var entry in backend.ListAsync(relativeDir, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var leaf = ExtractLeaf(directory, entry.Key);
            if (string.IsNullOrEmpty(leaf)) continue;
            seenLeaves.Add(leaf);

            if (entry.IsDirectory)
            {
                // Track that the child directory exists. We don't emit any change
                // event for new common-prefix entries because:
                //   1. The watcher's command sink only handles file placeholders
                //      from change events; directories materialize on the next
                //      ProjFS StartDirectoryEnumeration of the parent.
                //   2. The S3 Files spec only auto-propagates updates for files
                //      whose data is cached. Subtree directories that the user
                //      hasn't visited stay invisible by design.
                state.Children[leaf] = new ChildSnapshot(
                    IsDirectory: true, ETag: string.Empty, Size: 0, LastModified: default);
                continue;
            }

            if (IsLocallyInFlight(entry.Key)) continue;

            var hadPrev = state.Children.TryGetValue(leaf, out var prev);
            if (!hadPrev || prev.IsDirectory || HasChanged(prev, entry))
            {
                events.Add(BuildUpserted(entry));
                state.Children[leaf] = new ChildSnapshot(
                    IsDirectory: false,
                    ETag: entry.ETag,
                    Size: entry.Size,
                    LastModified: entry.LastModified);
            }
        }

        // Anything in the cached snapshot but not seen this tick is a remote delete.
        foreach (var leaf in state.Children.Keys.ToArray())
        {
            if (seenLeaves.Contains(leaf)) continue;
            if (!state.Children.TryGetValue(leaf, out var prev)) continue;

            // Drop directory entries silently: the next ProjFS enumeration of the
            // parent will pick up the missing common prefix on its own.
            if (prev.IsDirectory)
            {
                state.Children.TryRemove(leaf, out _);
                continue;
            }

            var fullKey = JoinKey(directory, leaf);
            if (IsLocallyInFlight(fullKey))
            {
                continue;
            }

            events.Add(new ObjectChangeEvent(
                Kind: ObjectChangeKind.Deleted,
                Key: fullKey,
                RelativePath: KeyPath.ToRelativePath(fullKey),
                Size: 0,
                LastModified: default,
                ETag: string.Empty));
            state.Children.TryRemove(leaf, out _);
        }
    }

    /// <summary>
    /// True when the key (or any registered prefix it falls under) currently has a
    /// local mutation in flight that the reconcile loop should ignore.
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
    /// True when the previous and current snapshots disagree on identity. ETag is
    /// the primary signal; size / last-modified act as fallback for blank ETags.
    /// </summary>
    private static bool HasChanged(ChildSnapshot prev, ObjectInfo current)
    {
        if (!string.IsNullOrEmpty(prev.ETag) && !string.IsNullOrEmpty(current.ETag))
        {
            return !string.Equals(prev.ETag, current.ETag, StringComparison.Ordinal);
        }
        return prev.Size != current.Size || prev.LastModified != current.LastModified;
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
    /// Normalizes a Windows-style relative directory to its object-key form
    /// (forward-slash separated, no trailing slash; root is the empty string).
    /// </summary>
    private static string NormalizeRelativeDirectory(string? relativeDirectory)
    {
        if (string.IsNullOrEmpty(relativeDirectory)) return string.Empty;
        var asKey = KeyPath.ToObjectKey(relativeDirectory);
        return asKey.TrimEnd('/');
    }

    /// <summary>
    /// Yields every ancestor of <paramref name="directory"/> from the root downwards,
    /// inclusive — so registering "a/b/c" produces "" → "a" → "a/b" → "a/b/c".
    /// </summary>
    private static IEnumerable<string> EnumerateAncestorChain(string directory)
    {
        yield return string.Empty;
        if (directory.Length == 0) yield break;

        var idx = 0;
        while (true)
        {
            var slash = directory.IndexOf('/', idx);
            if (slash < 0)
            {
                yield return directory;
                yield break;
            }
            yield return directory[..slash];
            idx = slash + 1;
        }
    }

    /// <summary>
    /// Splits a full object key into (parent-directory-in-key-form, leaf-name).
    /// </summary>
    private static (string ParentDir, string Leaf) SplitParent(string objectKey)
    {
        var slash = objectKey.LastIndexOf('/');
        return slash < 0
            ? (string.Empty, objectKey)
            : (objectKey[..slash], objectKey[(slash + 1)..]);
    }

    /// <summary>
    /// Recombines a directory + leaf back into the full object key.
    /// </summary>
    private static string JoinKey(string directory, string leaf) =>
        directory.Length == 0 ? leaf : directory + '/' + leaf;

    /// <summary>
    /// Extracts the leaf name of <paramref name="entryKey"/> relative to
    /// <paramref name="directory"/>. The backend's <c>ListAsync</c> with
    /// delimiter '/' returns immediate children only, so the suffix is a single
    /// path segment for files and (already trimmed) common-prefix name for dirs.
    /// </summary>
    private static string ExtractLeaf(string directory, string entryKey)
    {
        if (directory.Length == 0) return entryKey;
        var prefix = directory + '/';
        if (!entryKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return entryKey;
        }
        return entryKey[prefix.Length..];
    }

    /// <summary>
    /// Per-directory cached state. The child map is keyed by leaf name (no
    /// directory prefix) so it can be reconciled in O(1) lookups.
    /// </summary>
    private sealed class DirectoryWatchState
    {
        public ConcurrentDictionary<string, ChildSnapshot> Children { get; } =
            new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Minimal projection of an immediate child kept inside <see cref="DirectoryWatchState"/>.
    /// </summary>
    private readonly record struct ChildSnapshot(
        bool IsDirectory, string ETag, long Size, DateTimeOffset LastModified);

    /// <summary>
    /// Disposable that runs an action exactly once on first dispose.
    /// </summary>
    private sealed class ReleaseToken(Action onDispose) : IDisposable
    {
        private Action? onDispose = onDispose;

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
        public static readonly EmptyDisposable Instance = new();

        public void Dispose() { }
    }
}
