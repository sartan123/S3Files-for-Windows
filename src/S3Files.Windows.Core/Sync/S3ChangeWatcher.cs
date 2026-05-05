using Microsoft.Extensions.Logging;
using S3Files.Windows.S3;
using System.Collections.Concurrent;

namespace S3Files.Windows.Sync;

/// <summary>
/// Periodically reconciles the linked S3 bucket against an in-memory snapshot to discover
/// objects that other applications have added, modified, or deleted, and reflects those
/// changes through ProjFS. Implements the "Changes in your S3 bucket automatically appear
/// in your file system" behavior described in the S3 Files spec, including the
/// "S3 bucket is the source of truth" conflict policy.
/// </summary>
/// <remarks>
/// The AWS-managed S3 Files service uses S3 Event Notifications (SNS/SQS) for near-real-time
/// updates. This local desktop port uses polling instead, since wiring up event delivery
/// requires bucket configuration we cannot assume. The polling interval is configurable; the
/// poll itself uses a single recursive list of the bucket and is best-effort.
/// </remarks>
internal sealed class S3ChangeWatcher : IAsyncDisposable
{
    /// <summary>Name of the lost+found directory created in the virtualization root for
    /// quarantined local copies. The S3 Files spec uses
    /// <c>.s3files-lost+found-{filesystemId}</c>; we drop the suffix because there is one
    /// virt-root per process.</summary>
    public const string LostAndFoundDirectoryName = ".s3files-lost+found";

    private readonly IS3Backend backend;
    private readonly IProjFsCommandSink commandSink;
    private readonly ILostAndFoundQuarantine quarantine;
    private readonly TimeSpan interval;
    private readonly ILogger<S3ChangeWatcher> logger;

    /// <summary>S3-key → last-known state. Updated on poll diffs and on local mutations
    /// recorded via <see cref="RecordLocalUpload"/> / <see cref="RecordLocalDelete"/> /
    /// <see cref="RecordLocalRename"/> so the next poll doesn't re-import our own writes.</summary>
    private readonly ConcurrentDictionary<string, ObjectSnapshot> snapshot =
        new(StringComparer.Ordinal);

    /// <summary>S3 keys whose mutation is currently in flight from the local side. The poll
    /// loop ignores these to avoid a "we just uploaded → S3 has a new ETag → revert local
    /// because we haven't recorded the new ETag yet" race.</summary>
    private readonly ConcurrentDictionary<string, byte> localKeysInFlight =
        new(StringComparer.Ordinal);

    /// <summary>Active prefix-scoped local mutations (delete-prefix, rename-prefix). Keys
    /// matching any registered prefix are ignored by the poll loop. Stored as the
    /// trailing-slash-normalized prefix.</summary>
    private readonly ConcurrentDictionary<string, byte> localPrefixesInFlight =
        new(StringComparer.Ordinal);

    private CancellationTokenSource? cts;
    private Task? loopTask;

    public S3ChangeWatcher(
        IS3Backend backend,
        IProjFsCommandSink commandSink,
        ILostAndFoundQuarantine quarantine,
        TimeSpan interval,
        ILogger<S3ChangeWatcher> logger)
    {
        this.backend = backend;
        this.commandSink = commandSink;
        this.quarantine = quarantine;
        this.interval = interval;
        this.logger = logger;
    }

    /// <summary>Starts a background polling loop. No-op if the configured interval is non-positive.</summary>
    public Task StartAsync(CancellationToken initialCt)
    {
        if (interval <= TimeSpan.Zero)
        {
            logger.LogInformation(
                "S3 change watcher disabled (sync interval is {Interval}).", interval);
            return Task.CompletedTask;
        }

        cts = CancellationTokenSource.CreateLinkedTokenSource(initialCt);
        loopTask = Task.Run(() => RunLoopAsync(cts.Token), cts.Token);
        logger.LogInformation(
            "S3 change watcher started (interval = {Interval}).", interval);
        return Task.CompletedTask;
    }

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

    /// <summary>Records a local upload so the next poll doesn't see the new ETag as an
    /// external modification and try to re-import our own write.</summary>
    public void RecordLocalUpload(string s3Key, string etag, long size, DateTimeOffset lastModified)
    {
        if (string.IsNullOrEmpty(s3Key)) return;
        snapshot[s3Key] = new ObjectSnapshot(etag ?? string.Empty, size, lastModified);
    }

    /// <summary>Records a local delete so the next poll doesn't see the missing key as an
    /// external delete (which would attempt to remove a placeholder we already removed).</summary>
    public void RecordLocalDelete(string s3Key)
    {
        if (string.IsNullOrEmpty(s3Key)) return;
        snapshot.TryRemove(s3Key, out _);
    }

    /// <summary>Records a local prefix-delete: removes every snapshot entry under the
    /// (slash-terminated) prefix.</summary>
    public void RecordLocalDeletePrefix(string s3KeyPrefix)
    {
        if (string.IsNullOrEmpty(s3KeyPrefix)) return;
        var prefix = s3KeyPrefix.EndsWith('/') ? s3KeyPrefix : s3KeyPrefix + '/';
        foreach (var key in snapshot.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                snapshot.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Records a local single-object rename, transposing the snapshot entry.</summary>
    public void RecordLocalRename(string oldKey, string newKey)
    {
        if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey)) return;
        if (snapshot.TryRemove(oldKey, out var snap))
        {
            snapshot[newKey] = snap;
        }
    }

    /// <summary>Records a local prefix-rename, retargeting every matching snapshot entry.</summary>
    public void RecordLocalRenamePrefix(string oldPrefix, string newPrefix)
    {
        if (string.IsNullOrEmpty(oldPrefix) || string.IsNullOrEmpty(newPrefix)) return;
        var oldP = oldPrefix.EndsWith('/') ? oldPrefix : oldPrefix + '/';
        var newP = newPrefix.EndsWith('/') ? newPrefix : newPrefix + '/';
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

    /// <summary>Acquires an "in-flight" token for a single S3 key. The poll loop ignores
    /// keys with active tokens until the token is disposed, preventing self-trigger races.</summary>
    public IDisposable BeginLocalKeyChange(string s3Key)
    {
        if (string.IsNullOrEmpty(s3Key)) return EmptyDisposable.Instance;
        localKeysInFlight[s3Key] = 0;
        return new ReleaseToken(() => localKeysInFlight.TryRemove(s3Key, out _));
    }

    /// <summary>Acquires an in-flight token for a key prefix.</summary>
    public IDisposable BeginLocalPrefixChange(string s3KeyPrefix)
    {
        if (string.IsNullOrEmpty(s3KeyPrefix)) return EmptyDisposable.Instance;
        var prefix = s3KeyPrefix.EndsWith('/') ? s3KeyPrefix : s3KeyPrefix + '/';
        localPrefixesInFlight[prefix] = 0;
        return new ReleaseToken(() => localPrefixesInFlight.TryRemove(prefix, out _));
    }

    /// <summary>Runs a single reconciliation cycle. Exposed for tests.</summary>
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

    /// <summary>Visible for tests: take an initial snapshot without applying any side effects.</summary>
    internal async Task PrimeSnapshotAsync(CancellationToken ct)
    {
        await foreach (var obj in backend.ListAllAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            snapshot[obj.Key] = ObjectSnapshot.From(obj);
        }
        logger.LogDebug(
            "Initial S3 snapshot primed with {Count} object(s).", snapshot.Count);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await PrimeSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to take initial S3 snapshot; continuing with empty baseline.");
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
                logger.LogError(ex, "Error during S3 change poll; will retry next cycle.");
            }
        }
    }

    private bool IsLocallyInFlight(string s3Key)
    {
        if (localKeysInFlight.ContainsKey(s3Key)) return true;
        foreach (var prefix in localPrefixesInFlight.Keys)
        {
            if (s3Key.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private void HandleRemoteCreated(S3ObjectInfo obj)
    {
        var contentId = S3Util.BuildContentId(obj.ETag);
        var ok = commandSink.TryWritePlaceholder(
            obj.RelativePath, obj.Size, obj.LastModified, contentId, isDirectory: false);
        if (ok)
        {
            logger.LogInformation(
                "Imported new S3 object as placeholder: {Path}", obj.RelativePath);
        }
        else
        {
            // The parent directory likely hasn't been materialized yet. Future enumerations
            // will fetch the latest from S3, so silent skip is correct.
            logger.LogDebug(
                "Skipped placeholder for new S3 object {Path} (parent not materialized).",
                obj.RelativePath);
        }
    }

    private void HandleRemoteModified(S3ObjectInfo obj)
    {
        var contentId = S3Util.BuildContentId(obj.ETag);
        var outcome = commandSink.TryUpdateFile(
            obj.RelativePath, obj.Size, obj.LastModified, contentId);

        switch (outcome)
        {
            case ProjFsUpdateOutcome.Updated:
                logger.LogInformation(
                    "Imported updated S3 object: {Path}", obj.RelativePath);
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
                    "Failed to import updated S3 object: {Path}", obj.RelativePath);
                return;
        }
    }

    private void HandleRemoteDeleted(string s3Key)
    {
        var relativePath = S3Util.ToRelativePath(s3Key);
        var outcome = commandSink.TryDeleteFile(relativePath, allowDirty: false);

        switch (outcome)
        {
            case ProjFsUpdateOutcome.Updated:
            case ProjFsUpdateOutcome.NotFound:
                logger.LogInformation(
                    "Removed local placeholder after external S3 delete: {Path}", relativePath);
                return;

            case ProjFsUpdateOutcome.DirtyConflict:
                // Synthesize a "deleted" S3ObjectInfo for the conflict resolver. We won't
                // re-create a placeholder afterwards because the object is gone in S3.
                var stub = new S3ObjectInfo(
                    Key: s3Key,
                    RelativePath: relativePath,
                    Size: 0,
                    LastModified: default,
                    ETag: string.Empty,
                    IsDirectory: false);
                ResolveConflict(stub, S3Util.BuildContentId(string.Empty), isDelete: true);
                return;

            default:
                logger.LogWarning(
                    "Failed to delete local placeholder after external S3 delete: {Path}",
                    relativePath);
                return;
        }
    }

    private void ResolveConflict(S3ObjectInfo obj, byte[] contentId, bool isDelete)
    {
        // S3 Files spec: "S3 bucket as the source of truth in case of conflicts." Move the
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
            "Conflict on {Path}: local copy quarantined to {LostAndFound}; S3 version is now authoritative.",
            obj.RelativePath, LostAndFoundDirectoryName);
    }

    private static bool HasChanged(ObjectSnapshot prev, S3ObjectInfo current)
    {
        // ETag is the primary signal; size/lastModified are tie-breakers when ETag is missing
        // (e.g., some S3-compatible servers return blank ETags for multipart uploads).
        if (!string.IsNullOrEmpty(prev.ETag) && !string.IsNullOrEmpty(current.ETag))
        {
            return !string.Equals(prev.ETag, current.ETag, StringComparison.Ordinal);
        }
        return prev.Size != current.Size || prev.LastModified != current.LastModified;
    }

    private readonly record struct ObjectSnapshot(string ETag, long Size, DateTimeOffset LastModified)
    {
        public static ObjectSnapshot From(S3ObjectInfo info) =>
            new(info.ETag, info.Size, info.LastModified);
    }

    private sealed class ReleaseToken(Action onDispose) : IDisposable
    {
        private Action? onDispose = onDispose;

        public void Dispose()
        {
            var action = Interlocked.Exchange(ref onDispose, null);
            action?.Invoke();
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
