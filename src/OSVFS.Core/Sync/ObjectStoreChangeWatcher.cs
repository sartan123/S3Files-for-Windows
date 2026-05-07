using Microsoft.Extensions.Logging;
using OSVFS.ObjectStore;
using System.Collections.Concurrent;

namespace OSVFS.Sync;

/// <summary>
/// Periodically reconciles the linked object-store bucket/container against an in-memory
/// snapshot to discover objects that other applications have added, modified, or deleted,
/// and reflects those changes through ProjFS. Implements the "Changes in your bucket
/// automatically appear in your file system" behavior described in the AWS S3 Files spec,
/// including the "remote bucket is the source of truth" conflict policy. The same machinery
/// applies unchanged to GCS and Azure Blob backends once they implement
/// <see cref="IObjectStoreBackend"/>.
/// </summary>
/// <remarks>
/// The AWS-managed S3 Files service uses S3 Event Notifications (SNS/SQS) for near-real-time
/// updates. This local desktop port uses polling instead, since wiring up event delivery
/// requires bucket configuration we cannot assume. The polling interval is configurable; the
/// poll itself uses a single recursive list of the bucket and is best-effort.
/// </remarks>
internal sealed class ObjectStoreChangeWatcher : IAsyncDisposable
{
    /// <summary>
    /// Name of the lost+found directory created in the virtualization root for
    /// quarantined local copies. Modeled on the AWS S3 Files spec
    /// (<c>.s3files-lost+found-{filesystemId}</c>); we drop the suffix because there is one
    /// virt-root per process.
    /// </summary>
    public const string LostAndFoundDirectoryName = ".osvfs-lost+found";

    private readonly IObjectStoreBackend backend;
    private readonly IProjFsCommandSink commandSink;
    private readonly ILostAndFoundQuarantine quarantine;
    private readonly TimeSpan interval;
    private readonly ILogger<ObjectStoreChangeWatcher> logger;

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

    private CancellationTokenSource? cts;
    private Task? loopTask;

    /// <summary>
    /// Creates a watcher that will poll <paramref name="backend"/> every
    /// <paramref name="interval"/> and apply discovered changes through <paramref name="commandSink"/>.
    /// A non-positive interval disables polling entirely.
    /// </summary>
    public ObjectStoreChangeWatcher(
        IObjectStoreBackend backend,
        IProjFsCommandSink commandSink,
        ILostAndFoundQuarantine quarantine,
        TimeSpan interval,
        ILogger<ObjectStoreChangeWatcher> logger)
    {
        this.backend = backend;
        this.commandSink = commandSink;
        this.quarantine = quarantine;
        this.interval = interval;
        this.logger = logger;
    }

    /// <summary>
    /// Starts a background polling loop. No-op if the configured interval is non-positive.
    /// </summary>
    public Task StartAsync(CancellationToken initialCt)
    {
        if (interval <= TimeSpan.Zero)
        {
            logger.LogInformation(
                "Object-store change watcher disabled (sync interval is {Interval}).", interval);
            return Task.CompletedTask;
        }

        cts = CancellationTokenSource.CreateLinkedTokenSource(initialCt);
        loopTask = Task.Run(() => RunLoopAsync(cts.Token), cts.Token);
        logger.LogInformation(
            "Object-store change watcher started (interval = {Interval}).", interval);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels the background loop and waits for it to drain.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (cts is null) return;
        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
            if (loopTask is not null)
            {
                try { await loopTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on shutdown */ }
            }
        }
        finally
        {
            cts.Dispose();
            cts = null;
            loopTask = null;
        }
    }

    /// <summary>
    /// Records a local upload so the next poll doesn't see the new ETag as an
    /// external modification and try to re-import our own write.
    /// </summary>
    public void RecordLocalUpload(string objectKey, string etag, long size, DateTimeOffset lastModified)
    {
        if (string.IsNullOrEmpty(objectKey)) return;
        snapshot[objectKey] = new ObjectSnapshot(etag ?? string.Empty, size, lastModified);
    }

    /// <summary>
    /// Records a local delete so the next poll doesn't see the missing key as an
    /// external delete (which would attempt to remove a placeholder we already removed).
    /// </summary>
    public void RecordLocalDelete(string objectKey)
    {
        if (string.IsNullOrEmpty(objectKey)) return;
        snapshot.TryRemove(objectKey, out _);
    }

    /// <summary>
    /// Records a local prefix-delete: removes every snapshot entry under the
    /// (slash-terminated) prefix.
    /// </summary>
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

    /// <summary>
    /// Records a local single-object rename, transposing the snapshot entry.
    /// </summary>
    public void RecordLocalRename(string oldKey, string newKey)
    {
        if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey)) return;
        if (snapshot.TryRemove(oldKey, out var snap))
        {
            snapshot[newKey] = snap;
        }
    }

    /// <summary>
    /// Records a local prefix-rename, retargeting every matching snapshot entry.
    /// </summary>
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

    /// <summary>
    /// Acquires an "in-flight" token for a single object key. The poll loop ignores
    /// keys with active tokens until the token is disposed, preventing self-trigger races.
    /// </summary>
    public IDisposable BeginLocalKeyChange(string objectKey)
    {
        if (string.IsNullOrEmpty(objectKey)) return EmptyDisposable.Instance;
        localKeysInFlight[objectKey] = 0;
        return new ReleaseToken(() => localKeysInFlight.TryRemove(objectKey, out _));
    }

    /// <summary>
    /// Acquires an in-flight token for a key prefix.
    /// </summary>
    public IDisposable BeginLocalPrefixChange(string objectKeyPrefix)
    {
        if (string.IsNullOrEmpty(objectKeyPrefix)) return EmptyDisposable.Instance;
        var prefix = EnsureTrailingSlash(objectKeyPrefix);
        localPrefixesInFlight[prefix] = 0;
        return new ReleaseToken(() => localPrefixesInFlight.TryRemove(prefix, out _));
    }

    /// <summary>
    /// Runs a single reconciliation cycle. Exposed for tests.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
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
                    HandleRemoteModified(obj);
                    snapshot[obj.Key] = ObjectSnapshot.From(obj);
                }
            }
            else
            {
                HandleRemoteCreated(obj);
                snapshot[obj.Key] = ObjectSnapshot.From(obj);
            }
        }

        // Anything in the previous snapshot that wasn't seen this cycle is a remote delete.
        foreach (var key in snapshot.Keys)
        {
            if (seen.Contains(key)) continue;
            if (IsLocallyInFlight(key)) continue;
            HandleRemoteDeleted(key);
            snapshot.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Visible for tests: take an initial snapshot without applying any side effects.
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
    /// Background loop that primes the snapshot once and then polls on a fixed cadence.
    /// </summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await PrimeSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to take initial object-store snapshot; continuing with empty baseline.");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during object-store change poll; will retry next cycle.");
            }
        }
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
    /// Reacts to a newly-discovered remote object by injecting a placeholder.
    /// </summary>
    private void HandleRemoteCreated(ObjectInfo obj)
    {
        var contentId = KeyPath.BuildContentId(obj.ETag);
        var ok = commandSink.TryWritePlaceholder(
            obj.RelativePath, obj.Size, obj.LastModified, contentId, isDirectory: false);
        if (ok)
        {
            logger.LogInformation(
                "Imported new remote object as placeholder: {Path}", obj.RelativePath);
        }
        else
        {
            // The parent directory likely hasn't been materialized yet. Future enumerations
            // will fetch the latest from the backend, so silent skip is correct.
            logger.LogDebug(
                "Skipped placeholder for new remote object {Path} (parent not materialized).",
                obj.RelativePath);
        }
    }

    /// <summary>
    /// Reacts to a remote modification by updating the placeholder, recreating it,
    /// or routing through conflict resolution when local data is dirty.
    /// </summary>
    private void HandleRemoteModified(ObjectInfo obj)
    {
        var contentId = KeyPath.BuildContentId(obj.ETag);
        var outcome = commandSink.TryUpdateFile(
            obj.RelativePath, obj.Size, obj.LastModified, contentId);

        switch (outcome)
        {
            case ProjFsUpdateOutcome.Updated:
                logger.LogInformation(
                    "Imported updated remote object: {Path}", obj.RelativePath);
                return;

            case ProjFsUpdateOutcome.NotFound:
                // Placeholder doesn't exist (e.g. user deleted it locally without us seeing,
                // or parent dir is no longer materialized). Try to re-create it.
                commandSink.TryWritePlaceholder(
                    obj.RelativePath, obj.Size, obj.LastModified, contentId, isDirectory: false);
                return;

            case ProjFsUpdateOutcome.DirtyConflict:
                ResolveConflict(obj, contentId, isDelete: false);
                return;

            default:
                logger.LogWarning(
                    "Failed to import updated remote object: {Path}", obj.RelativePath);
                return;
        }
    }

    /// <summary>
    /// Reacts to a remote delete by removing the local placeholder, quarantining
    /// dirty local copies first when needed.
    /// </summary>
    private void HandleRemoteDeleted(string objectKey)
    {
        var relativePath = KeyPath.ToRelativePath(objectKey);
        var outcome = commandSink.TryDeleteFile(relativePath, allowDirty: false);

        switch (outcome)
        {
            case ProjFsUpdateOutcome.Updated:
            case ProjFsUpdateOutcome.NotFound:
                logger.LogInformation(
                    "Removed local placeholder after external delete: {Path}", relativePath);
                return;

            case ProjFsUpdateOutcome.DirtyConflict:
                // Synthesize a "deleted" ObjectInfo for the conflict resolver. We won't
                // re-create a placeholder afterwards because the object is gone remotely.
                var stub = new ObjectInfo(
                    Key: objectKey,
                    RelativePath: relativePath,
                    Size: 0,
                    LastModified: default,
                    ETag: string.Empty,
                    IsDirectory: false);
                ResolveConflict(stub, KeyPath.BuildContentId(string.Empty), isDelete: true);
                return;

            default:
                logger.LogWarning(
                    "Failed to delete local placeholder after external delete: {Path}",
                    relativePath);
                return;
        }
    }

    /// <summary>
    /// Quarantines the dirty local copy and force-replaces (or removes) it so the
    /// remote version becomes authoritative.
    /// </summary>
    private void ResolveConflict(ObjectInfo obj, byte[] contentId, bool isDelete)
    {
        // Spec: "remote bucket as the source of truth in case of conflicts." Move the
        // dirty local copy to lost+found, then force-replace.
        var quarantined = quarantine.TryQuarantine(obj.RelativePath);
        if (!quarantined)
        {
            logger.LogWarning(
                "Could not quarantine local copy of {Path}; proceeding with replacement anyway.",
                obj.RelativePath);
        }

        var deleteOutcome = commandSink.TryDeleteFile(obj.RelativePath, allowDirty: true);
        if (deleteOutcome is not (ProjFsUpdateOutcome.Updated or ProjFsUpdateOutcome.NotFound))
        {
            logger.LogWarning(
                "Force-delete after conflict failed for {Path}: {Outcome}.",
                obj.RelativePath, deleteOutcome);
            return;
        }

        if (!isDelete)
        {
            commandSink.TryWritePlaceholder(
                obj.RelativePath, obj.Size, obj.LastModified, contentId, isDirectory: false);
        }

        logger.LogWarning(
            "Conflict on {Path}: local copy quarantined to {LostAndFound}; remote version is now authoritative.",
            obj.RelativePath, LostAndFoundDirectoryName);
    }

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
