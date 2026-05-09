using Microsoft.Extensions.Logging;
using OSVFS.ObjectStore;
using System.Collections.Concurrent;

namespace OSVFS.Sync;

/// <summary>
/// Applies remote-side object changes — discovered by an injected
/// <see cref="IChangeSource"/> — to the local virtualization root through ProjFS,
/// quarantining any dirty local copies before they are overwritten. Implements
/// the "Changes in your bucket automatically appear in your file system" behavior
/// described in the AWS S3 Files spec, including the "remote bucket is the source
/// of truth" conflict policy. The same machinery applies unchanged to GCS and
/// Azure Blob backends once they implement
/// <see cref="IObjectStoreBackend"/>.
/// </summary>
/// <remarks>
/// Discovery is the change source's responsibility. Polling-based discovery
/// lives in <see cref="PollingChangeSource"/>; event-driven discovery lives in
/// <c>SqsChangeSource</c>; <c>CompositeChangeSource</c> merges them. The watcher
/// itself stays source-agnostic and idempotent because event streams (especially
/// SQS) deliver duplicates.
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

    /// <summary>
    /// How long after a local mutation we keep its (key, etag) signature so push-based
    /// change sources (SQS) don't reflect our own writes back at us. Polling sources
    /// already self-suppress through their snapshot, but events fan out asynchronously
    /// from S3 and can arrive seconds after the upload returns.
    /// </summary>
    private static readonly TimeSpan SelfMutationSuppressWindow = TimeSpan.FromMinutes(2);

    private readonly IChangeSource source;
    private readonly ILocalMutationRecorder? sourceRecorder;
    private readonly IDirectoryWatchRegistrar? sourceRegistrar;
    private readonly IProjFsCommandSink commandSink;
    private readonly ILostAndFoundQuarantine quarantine;
    private readonly ILogger<ObjectStoreChangeWatcher> logger;

    /// <summary>
    /// Bounded TTL map of "the host just did this" signatures. Consulted by
    /// <see cref="ShouldSuppress"/> when an event arrives, so push-based sources
    /// don't trigger us to re-import our own writes.
    /// </summary>
    private readonly ConcurrentDictionary<string, RecentLocalMutation> recentLocalMutations =
        new(StringComparer.Ordinal);

    private CancellationTokenSource? cts;
    private Task? loopTask;

    /// <summary>
    /// Creates a watcher that drains <paramref name="source"/> and applies each
    /// emitted change through <paramref name="commandSink"/>, using
    /// <paramref name="quarantine"/> to preserve dirty local copies on conflict.
    /// </summary>
    public ObjectStoreChangeWatcher(
        IChangeSource source,
        IProjFsCommandSink commandSink,
        ILostAndFoundQuarantine quarantine,
        ILogger<ObjectStoreChangeWatcher> logger)
    {
        this.source = source;
        this.sourceRecorder = source as ILocalMutationRecorder;
        this.sourceRegistrar = source as IDirectoryWatchRegistrar;
        this.commandSink = commandSink;
        this.quarantine = quarantine;
        this.logger = logger;
    }

    /// <summary>
    /// True when the underlying change source maintains a per-directory watch
    /// set (i.e. on-demand polling). The host uses this to gate watch-set
    /// seeding and ProjFS-callback hookups.
    /// </summary>
    public bool SupportsDirectoryWatchRegistration => sourceRegistrar is not null;

    /// <summary>
    /// Number of directories the underlying source currently has under watch
    /// (zero when the source doesn't track per-directory state).
    /// </summary>
    public int WatchedDirectoryCount => sourceRegistrar?.WatchedDirectoryCount ?? 0;

    /// <summary>
    /// Forwards a directory enumeration to the underlying source's watch set.
    /// No-op when the source doesn't track per-directory state.
    /// </summary>
    public void RegisterWatchedDirectory(string relativeDirectory) =>
        sourceRegistrar?.RegisterWatchedDirectory(relativeDirectory ?? string.Empty);

    /// <summary>
    /// Starts a background task that pumps events from the change source until
    /// <see cref="DisposeAsync"/> is called.
    /// </summary>
    public Task StartAsync(CancellationToken initialCt)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(initialCt);
        loopTask = Task.Run(() => RunLoopAsync(cts.Token), cts.Token);
        logger.LogInformation("Object-store change watcher started.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels the background loop and waits for it to drain. Disposes the
    /// underlying change source as well.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (cts is not null)
        {
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

        await source.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Records a local upload so subsequent discovered changes for the same
    /// (key, etag) are suppressed. Also forwards to the underlying source's
    /// snapshot if it tracks one, keeping the diff baseline in sync.
    /// </summary>
    public void RecordLocalUpload(string objectKey, string etag, long size, DateTimeOffset lastModified)
    {
        if (string.IsNullOrEmpty(objectKey)) return;
        TrackRecent(objectKey, RecentLocalMutationKind.Upserted, etag);
        sourceRecorder?.RecordLocalUpload(objectKey, etag, size, lastModified);
    }

    /// <summary>
    /// Records a local delete so a subsequent "object gone" event is suppressed.
    /// </summary>
    public void RecordLocalDelete(string objectKey)
    {
        if (string.IsNullOrEmpty(objectKey)) return;
        TrackRecent(objectKey, RecentLocalMutationKind.Deleted, etag: null);
        sourceRecorder?.RecordLocalDelete(objectKey);
    }

    /// <summary>
    /// Records a local prefix-delete: every snapshot entry under the prefix is
    /// removed, and any subsequent delete event arriving for a child key is
    /// suppressed (we tracked it explicitly via the recent-mutation map).
    /// </summary>
    public void RecordLocalDeletePrefix(string objectKeyPrefix)
    {
        if (string.IsNullOrEmpty(objectKeyPrefix)) return;
        // The recent-mutation map is keyed by full object key so we can't
        // pre-populate every child here. Push-based sources should arrive with
        // per-object delete events; the source's prefix-token (below) handles
        // the in-flight window, and any straggler is harmless because the
        // placeholder is already gone.
        sourceRecorder?.RecordLocalDeletePrefix(objectKeyPrefix);
    }

    /// <summary>
    /// Records a local single-object rename, transposing the snapshot entry.
    /// </summary>
    public void RecordLocalRename(string oldKey, string newKey)
    {
        if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey)) return;
        TrackRecent(oldKey, RecentLocalMutationKind.Deleted, etag: null);
        sourceRecorder?.RecordLocalRename(oldKey, newKey);
    }

    /// <summary>
    /// Records a local prefix-rename, retargeting every matching snapshot entry.
    /// </summary>
    public void RecordLocalRenamePrefix(string oldPrefix, string newPrefix)
    {
        if (string.IsNullOrEmpty(oldPrefix) || string.IsNullOrEmpty(newPrefix)) return;
        sourceRecorder?.RecordLocalRenamePrefix(oldPrefix, newPrefix);
    }

    /// <summary>
    /// Acquires an "in-flight" token for a single object key. The change source
    /// should ignore the key for the duration; the watcher additionally suppresses
    /// any event for that key that slips through anyway.
    /// </summary>
    public IDisposable BeginLocalKeyChange(string objectKey)
    {
        if (string.IsNullOrEmpty(objectKey)) return EmptyDisposable.Instance;
        return sourceRecorder?.BeginLocalKeyChange(objectKey) ?? EmptyDisposable.Instance;
    }

    /// <summary>
    /// Acquires an in-flight token for a key prefix, forwarded to the underlying source.
    /// </summary>
    public IDisposable BeginLocalPrefixChange(string objectKeyPrefix)
    {
        if (string.IsNullOrEmpty(objectKeyPrefix)) return EmptyDisposable.Instance;
        return sourceRecorder?.BeginLocalPrefixChange(objectKeyPrefix) ?? EmptyDisposable.Instance;
    }

    /// <summary>
    /// Apply a single change event synchronously. Exposed for unit tests so they
    /// can drive the dispatch path without spinning up the background task.
    /// </summary>
    internal void ApplyForTesting(ObjectChangeEvent ev) => Apply(ev);

    /// <summary>
    /// Background loop that pumps the change source, applying every event until
    /// cancellation. Exceptions inside <see cref="Apply"/> are logged and
    /// swallowed so a single bad event can't tear down the watcher.
    /// </summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var ev in source.WatchAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    Apply(ev);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(
                        ex, "Failed to apply change event for {Path}", ev.RelativePath);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Object-store change watcher loop terminated unexpectedly.");
        }
    }

    /// <summary>
    /// Routes one event to the appropriate ProjFS command, after consulting the
    /// recent-mutation suppressor.
    /// </summary>
    private void Apply(ObjectChangeEvent ev)
    {
        if (ShouldSuppress(ev))
        {
            logger.LogDebug(
                "Suppressing self-triggered change event for {Path}", ev.RelativePath);
            return;
        }

        switch (ev.Kind)
        {
            case ObjectChangeKind.Upserted:
                HandleRemoteUpserted(ev);
                break;
            case ObjectChangeKind.Deleted:
                HandleRemoteDeleted(ev);
                break;
            default:
                logger.LogWarning("Unknown change event kind {Kind} for {Path}", ev.Kind, ev.RelativePath);
                break;
        }
    }

    /// <summary>
    /// Reacts to an upsert by trying to update the placeholder; falls back to a
    /// fresh placeholder write if the local file is missing, and to conflict
    /// resolution if the local copy is dirty.
    /// </summary>
    private void HandleRemoteUpserted(ObjectChangeEvent ev)
    {
        var contentId = KeyPath.BuildContentId(ev.ETag);

        // First-time create vs. modification of an existing placeholder are not
        // distinguishable from the source's perspective. TryUpdateFile reports
        // NotFound when there is no existing placeholder, which becomes our
        // "create" path; it reports DirtyConflict when we'd clobber local edits.
        var outcome = commandSink.TryUpdateFile(
            ev.RelativePath, ev.Size, ev.LastModified, contentId);

        switch (outcome)
        {
            case ProjFsUpdateOutcome.Updated:
                logger.LogInformation(
                    "Imported updated remote object: {Path}", ev.RelativePath);
                return;

            case ProjFsUpdateOutcome.NotFound:
                var ok = commandSink.TryWritePlaceholder(
                    ev.RelativePath, ev.Size, ev.LastModified, contentId, isDirectory: false);
                if (ok)
                {
                    logger.LogInformation(
                        "Imported new remote object as placeholder: {Path}", ev.RelativePath);
                }
                else
                {
                    // The parent directory likely hasn't been materialized yet. Future enumerations
                    // will fetch the latest from the backend, so silent skip is correct.
                    logger.LogDebug(
                        "Skipped placeholder for new remote object {Path} (parent not materialized).",
                        ev.RelativePath);
                }
                return;

            case ProjFsUpdateOutcome.DirtyConflict:
                ResolveConflict(ev, contentId, isDelete: false);
                return;

            default:
                logger.LogWarning(
                    "Failed to import updated remote object: {Path}", ev.RelativePath);
                return;
        }
    }

    /// <summary>
    /// Reacts to a remote delete by removing the local placeholder, quarantining
    /// dirty local copies first when needed.
    /// </summary>
    private void HandleRemoteDeleted(ObjectChangeEvent ev)
    {
        var outcome = commandSink.TryDeleteFile(ev.RelativePath, allowDirty: false);

        switch (outcome)
        {
            case ProjFsUpdateOutcome.Updated:
            case ProjFsUpdateOutcome.NotFound:
                logger.LogInformation(
                    "Removed local placeholder after external delete: {Path}", ev.RelativePath);
                return;

            case ProjFsUpdateOutcome.DirtyConflict:
                ResolveConflict(ev, KeyPath.BuildContentId(string.Empty), isDelete: true);
                return;

            default:
                logger.LogWarning(
                    "Failed to delete local placeholder after external delete: {Path}",
                    ev.RelativePath);
                return;
        }
    }

    /// <summary>
    /// Quarantines the dirty local copy and force-replaces (or removes) it so the
    /// remote version becomes authoritative.
    /// </summary>
    private void ResolveConflict(ObjectChangeEvent ev, byte[] contentId, bool isDelete)
    {
        // Spec: "remote bucket as the source of truth in case of conflicts." Move the
        // dirty local copy to lost+found, then force-replace.
        var quarantined = quarantine.TryQuarantine(ev.RelativePath);
        if (!quarantined)
        {
            logger.LogWarning(
                "Could not quarantine local copy of {Path}; proceeding with replacement anyway.",
                ev.RelativePath);
        }

        var deleteOutcome = commandSink.TryDeleteFile(ev.RelativePath, allowDirty: true);
        if (deleteOutcome is not (ProjFsUpdateOutcome.Updated or ProjFsUpdateOutcome.NotFound))
        {
            logger.LogWarning(
                "Force-delete after conflict failed for {Path}: {Outcome}.",
                ev.RelativePath, deleteOutcome);
            return;
        }

        if (!isDelete)
        {
            commandSink.TryWritePlaceholder(
                ev.RelativePath, ev.Size, ev.LastModified, contentId, isDirectory: false);
        }

        logger.LogWarning(
            "Conflict on {Path}: local copy quarantined to {LostAndFound}; remote version is now authoritative.",
            ev.RelativePath, LostAndFoundDirectoryName);
    }

    /// <summary>
    /// Stamps the recent-mutation map and lazily evicts entries older than
    /// <see cref="SelfMutationSuppressWindow"/>.
    /// </summary>
    private void TrackRecent(string objectKey, RecentLocalMutationKind kind, string? etag)
    {
        var now = DateTimeOffset.UtcNow;
        recentLocalMutations[objectKey] = new RecentLocalMutation(kind, etag ?? string.Empty, now);

        // Cheap incidental cleanup so the map can't grow unboundedly across long sessions.
        if (recentLocalMutations.Count > 256)
        {
            foreach (var (k, v) in recentLocalMutations)
            {
                if (now - v.At > SelfMutationSuppressWindow)
                {
                    recentLocalMutations.TryRemove(k, out _);
                }
            }
        }
    }

    /// <summary>
    /// True when an event matches a recent local mutation and should not be
    /// re-applied. Matching is by (key, kind, etag): an Upserted event whose
    /// ETag matches our last upload, or a Deleted event after a recent local
    /// delete, is suppressed.
    /// </summary>
    private bool ShouldSuppress(ObjectChangeEvent ev)
    {
        if (!recentLocalMutations.TryGetValue(ev.Key, out var recent)) return false;
        if (DateTimeOffset.UtcNow - recent.At > SelfMutationSuppressWindow)
        {
            recentLocalMutations.TryRemove(ev.Key, out _);
            return false;
        }

        return (ev.Kind, recent.Kind) switch
        {
            (ObjectChangeKind.Deleted, RecentLocalMutationKind.Deleted) => true,
            (ObjectChangeKind.Upserted, RecentLocalMutationKind.Upserted) =>
                MatchEtag(ev.ETag, recent.Etag),
            _ => false,
        };
    }

    /// <summary>
    /// Compares two ETag strings ignoring surrounding double quotes, since some
    /// providers (and EventBridge) drop the quotes that S3 returns over the wire.
    /// </summary>
    private static bool MatchEtag(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return a.AsSpan().Trim('"').SequenceEqual(b.AsSpan().Trim('"'));
    }

    /// <summary>
    /// Discriminator for entries in the recent-local-mutation map.
    /// </summary>
    private enum RecentLocalMutationKind
    {
        Upserted,
        Deleted,
    }

    /// <summary>
    /// One stamped self-mutation kept around to suppress the matching push-based
    /// echo from <see cref="IChangeSource"/>.
    /// </summary>
    private readonly record struct RecentLocalMutation(
        RecentLocalMutationKind Kind, string Etag, DateTimeOffset At);

    /// <summary>
    /// Singleton no-op disposable returned when the caller passed an empty key
    /// or the change source doesn't track local mutations.
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
