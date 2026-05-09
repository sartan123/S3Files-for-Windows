using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.ProjFS;
using OSVFS.Configuration;
using OSVFS.ObjectStore;
using OSVFS.Sync;
using OSVFS.Sync.ProjFs;
using OSVFS.Sync.Sqs;
using System.Collections.Concurrent;

namespace OSVFS.ProjFs;

/// <summary>
/// ProjFS callback host that bridges the virtualization instance to the object-store
/// backend and the change watcher. Owns the lifetime of every long-lived collaborator.
/// </summary>
internal sealed class ProjFsProvider : IRequiredCallbacks, IDisposable
{
    /// <summary>
    /// Provider identifier embedded in every placeholder we write.
    /// </summary>
    private static readonly byte[] ProviderId = [1];

    private readonly ILogger<ProjFsProvider> logger;
    private readonly ILoggerFactory loggerFactory;
    private readonly string syncRootPath;
    private readonly VirtualizationInstance virtualizationInstance;
    private readonly IObjectStoreBackend backend;
    private readonly ConcurrentDictionary<Guid, DirectoryEnumerationSession> activeEnumerations = new();
    private readonly NotificationCallbacks notificationCallbacks;
    private readonly ObjectStoreChangeWatcher? changeWatcher;

    private bool virtualizationInstanceStarted;

    /// <summary>
    /// Effective options the provider was constructed with.
    /// </summary>
    public ProjFsProviderOptions Options { get; }

    /// <summary>
    /// Convenience constructor that disables loggers for collaborator components.
    /// </summary>
    public ProjFsProvider(ProjFsProviderOptions options, ILogger<ProjFsProvider> logger)
        : this(options, logger, NullLoggerFactory.Instance)
    {
    }

    /// <summary>
    /// Constructs the provider, marks the directory as a virtualization root, and
    /// wires up notification mappings without yet starting virtualization.
    /// </summary>
    public ProjFsProvider(
        ProjFsProviderOptions options,
        ILogger<ProjFsProvider> logger,
        ILoggerFactory loggerFactory)
    {
        Options = options;
        this.logger = logger;
        this.loggerFactory = loggerFactory;
        syncRootPath = options.VirtRoot;
        backend = ObjectStoreBackendFactory.Create(
            options.Provider,
            options.Bucket,
            options.EndpointUrl,
            options.KeyPrefix,
            options.Region,
            options.Credentials,
            options.BandwidthLimits,
            options.MultipartThresholdBytes,
            options.MultipartPartSizeBytes);

        EnsureVirtualizationRoot();

        var notificationMappings = new List<NotificationMapping>
        {
            new(
                NotificationType.FileOpened
                | NotificationType.NewFileCreated
                | NotificationType.FileOverwritten
                | NotificationType.PreDelete
                | NotificationType.PreRename
                | NotificationType.PreCreateHardlink
                | NotificationType.FileRenamed
                | NotificationType.HardlinkCreated
                | NotificationType.FileHandleClosedNoModification
                | NotificationType.FileHandleClosedFileModified
                | NotificationType.FileHandleClosedFileDeleted
                | NotificationType.FilePreConvertToFull,
                string.Empty),
        };

        virtualizationInstance = new VirtualizationInstance(
            syncRootPath,
            poolThreadCount: 0,
            concurrentThreadCount: 0,
            enableNegativePathCache: false,
            notificationMappings: notificationMappings);

        notificationCallbacks = new NotificationCallbacks(this, virtualizationInstance, notificationMappings);

        changeWatcher = CreateChangeWatcher();
    }

    /// <summary>
    /// Stops the change watcher, halts virtualization, and disposes the backend.
    /// </summary>
    public void Dispose()
    {
        if (changeWatcher is not null)
        {
            try
            {
                changeWatcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to stop S3 change watcher");
            }
        }

        if (virtualizationInstanceStarted)
        {
            try
            {
                virtualizationInstance.StopVirtualizing();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to stop virtualization instance");
            }
            virtualizationInstanceStarted = false;
        }
        backend.Dispose();
    }

    /// <summary>
    /// Verifies the bucket safety preconditions, starts virtualization, and kicks
    /// off the background change watcher. Returns false on any startup failure.
    /// Throws <see cref="BucketVersioningNotEnabledException"/> when the bucket
    /// has versioning disabled and <c>--allow-unversioned</c> was not passed.
    /// </summary>
    public bool StartVirtualization()
    {
        // Refuse to start before touching ProjFS so we leave no virtualization instance
        // running on a bucket that doesn't meet the safety requirement.
        if (!EnsureBucketVersioningEnabled()) return false;

        var hr = virtualizationInstance.StartVirtualizing(this);
        if (hr != HResult.Ok)
        {
            logger.LogError("StartVirtualizing failed: {HResult}", hr);
            return false;
        }
        virtualizationInstanceStarted = true;

        // Seed the on-demand watch set from existing placeholder directories before the
        // first reconcile tick fires. ProjFS persists placeholders across runs, so the
        // directories the user visited previously stay materialized — replaying them
        // restores the same polling coverage they had before exit.
        if (changeWatcher is not null)
        {
            WatchSetSeeder.Seed(syncRootPath, changeWatcher, logger);
        }

        // The watcher is fire-and-forget: it self-cancels on Dispose. Polling only starts
        // after virtualization is up so the command sink is safe to call.
        _ = changeWatcher?.StartAsync(CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Reads the bucket versioning status and applies the safety policy. Returns
    /// false on backend failure; rethrows
    /// <see cref="BucketVersioningNotEnabledException"/> when the bucket is
    /// unversioned and the operator did not opt out.
    /// </summary>
    private bool EnsureBucketVersioningEnabled()
    {
        BucketVersioningStatus status;
        try
        {
            status = backend.GetBucketVersioningStatusAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Failed to read bucket versioning state for {Bucket}", Options.Bucket);
            return false;
        }

        BucketVersioningGuard.Validate(status, Options.Bucket, Options.AllowUnversioned, logger);
        return true;
    }

    /// <summary>
    /// ProjFS callback: lists the immediate children of <paramref name="relativePath"/>
    /// and registers an enumeration session under <paramref name="enumerationId"/>.
    /// Also registers the directory in the change watcher's on-demand watch set so
    /// future polling ticks reconcile it.
    /// </summary>
    public HResult StartDirectoryEnumerationCallback(
        int commandId,
        Guid enumerationId,
        string relativePath,
        uint triggeringProcessId,
        string triggeringProcessImageFileName)
    {
        try
        {
            // Register the visited directory before listing so a concurrent reconcile
            // pass that races with this enumeration sees the new entry on the next tick.
            // Safe to call even when the watcher is null (read-only mode) or doesn't
            // support per-directory watches (full polling, SQS).
            changeWatcher?.RegisterWatchedDirectory(relativePath ?? string.Empty);

            var entries = ListDirectoryAsync(relativePath ?? string.Empty).GetAwaiter().GetResult();
            var session = new DirectoryEnumerationSession(entries);
            return activeEnumerations.TryAdd(enumerationId, session) ? HResult.Ok : HResult.InternalError;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "StartDirectoryEnumeration({RelativePath})", relativePath);
            return HResult.InternalError;
        }
    }

    /// <summary>
    /// ProjFS callback: tears down the enumeration session.
    /// </summary>
    public HResult EndDirectoryEnumerationCallback(Guid enumerationId)
    {
        activeEnumerations.TryRemove(enumerationId, out _);
        return HResult.Ok;
    }

    /// <summary>
    /// ProjFS callback: yields filtered entries from the active enumeration session
    /// into <paramref name="result"/> until full or the session is exhausted.
    /// </summary>
    public HResult GetDirectoryEnumerationCallback(
        int commandId,
        Guid enumerationId,
        string filterFileName,
        bool restartScan,
        IDirectoryEnumerationResults result)
    {
        if (!activeEnumerations.TryGetValue(enumerationId, out var session))
        {
            return HResult.InternalError;
        }

        if (restartScan)
        {
            session.Restart(filterFileName);
        }
        else
        {
            session.EnsureFilter(filterFileName);
        }

        while (session.TryGetCurrent(out var entry, out var leafName))
        {
            if (!result.Add(leafName, entry.Size, entry.IsDirectory))
            {
                return HResult.Ok;
            }
            session.Advance();
        }
        return HResult.Ok;
    }

    /// <summary>
    /// ProjFS callback: writes a placeholder for <paramref name="relativePath"/>
    /// based on the backend HEAD response, returning FileNotFound when the object is absent.
    /// </summary>
    public HResult GetPlaceholderInfoCallback(
        int commandId,
        string relativePath,
        uint triggeringProcessId,
        string triggeringProcessImageFileName)
    {
        try
        {
            var info = backend.HeadAsync(relativePath, CancellationToken.None).GetAwaiter().GetResult();
            if (info is null)
            {
                return HResult.FileNotFound;
            }

            var (key, _, size, lastModified, etag, isDirectory) = info.Value;
            var attrs = isDirectory ? FileAttributes.Directory : FileAttributes.Normal;
            var timestamp = lastModified == default ? DateTime.UtcNow : lastModified.UtcDateTime;

            return virtualizationInstance.WritePlaceholderInfo(
                relativePath: relativePath,
                creationTime: timestamp,
                lastAccessTime: timestamp,
                lastWriteTime: timestamp,
                changeTime: timestamp,
                fileAttributes: attrs,
                endOfFile: size,
                isDirectory: isDirectory,
                contentId: KeyPath.BuildContentId(etag),
                providerId: ProviderId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetPlaceholderInfo({RelativePath})", relativePath);
            return HResult.InternalError;
        }
    }

    /// <summary>
    /// ProjFS callback: streams a byte range from the backend into the virtualization
    /// instance's write buffer to hydrate the placeholder.
    /// </summary>
    public HResult GetFileDataCallback(
        int commandId,
        string relativePath,
        ulong byteOffset,
        uint length,
        Guid dataStreamId,
        byte[] contentId,
        byte[] providerId,
        uint triggeringProcessId,
        string triggeringProcessImageFileName)
    {
        try
        {
            using var buffer = virtualizationInstance.CreateWriteBuffer(length);
            backend.ReadRangeAsync(relativePath, (long)byteOffset, length, buffer.Stream, CancellationToken.None)
                .GetAwaiter().GetResult();

            return virtualizationInstance.WriteFileData(dataStreamId, buffer, byteOffset, length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetFileData({RelativePath})", relativePath);
            return HResult.InternalError;
        }
    }

    /// <summary>
    /// Notification handler: uploads a locally-modified file to the backend and records
    /// the new ETag with the change watcher.
    /// </summary>
    public void HandleFileModified(string relativePath, bool isDirectory)
    {
        if (Options.ReadOnly || isDirectory || string.IsNullOrEmpty(relativePath)) return;
        if (IsInLostAndFound(relativePath)) return;

        var fullPath = Path.Combine(syncRootPath, relativePath);
        if (!File.Exists(fullPath))
        {
            logger.LogDebug("Modified notification but file missing: {RelativePath}", relativePath);
            return;
        }

        var objectKey = KeyPath.ToObjectKey(relativePath);
        using var _ = changeWatcher?.BeginLocalKeyChange(objectKey);

        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.SequentialScan);

            var result = backend.UploadAsync(relativePath, stream, ifMatchETag: null, CancellationToken.None)
                .GetAwaiter().GetResult();
            changeWatcher?.RecordLocalUpload(objectKey, result.ETag, result.Size, result.LastModified);

            logger.LogInformation("Uploaded {RelativePath} ({Size} bytes)", relativePath, stream.Length);
        }
        catch (FileNotFoundException)
        {
            logger.LogDebug("File vanished before upload: {RelativePath}", relativePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upload failed for {RelativePath}", relativePath);
        }
    }

    /// <summary>
    /// Notification handler: deletes the corresponding remote object (or whole prefix
    /// for directories) after a local delete.
    /// </summary>
    public void HandleFileDeleted(string relativePath, bool isDirectory)
    {
        if (Options.ReadOnly || string.IsNullOrEmpty(relativePath)) return;
        if (IsInLostAndFound(relativePath)) return;

        var objectKey = KeyPath.ToObjectKey(relativePath);

        try
        {
            if (isDirectory)
            {
                using var _ = changeWatcher?.BeginLocalPrefixChange(objectKey);
                backend.DeletePrefixAsync(relativePath, CancellationToken.None)
                    .GetAwaiter().GetResult();
                changeWatcher?.RecordLocalDeletePrefix(objectKey);
                logger.LogInformation("Deleted prefix {RelativePath}/", relativePath);
            }
            else
            {
                using var _ = changeWatcher?.BeginLocalKeyChange(objectKey);
                backend.DeleteAsync(relativePath, CancellationToken.None)
                    .GetAwaiter().GetResult();
                changeWatcher?.RecordLocalDelete(objectKey);
                logger.LogInformation("Deleted {RelativePath}", relativePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete failed for {RelativePath}", relativePath);
        }
    }

    /// <summary>
    /// Notification handler: renames the corresponding remote object or prefix and
    /// updates the watcher's snapshot.
    /// </summary>
    public void HandleFileRenamed(string oldRelativePath, string newRelativePath, bool isDirectory)
    {
        if (Options.ReadOnly) return;
        if (string.IsNullOrEmpty(oldRelativePath) || string.IsNullOrEmpty(newRelativePath)) return;
        if (IsInLostAndFound(oldRelativePath) || IsInLostAndFound(newRelativePath)) return;

        var oldKey = KeyPath.ToObjectKey(oldRelativePath);
        var newKey = KeyPath.ToObjectKey(newRelativePath);

        try
        {
            if (isDirectory)
            {
                using var oldGuard = changeWatcher?.BeginLocalPrefixChange(oldKey);
                using var newGuard = changeWatcher?.BeginLocalPrefixChange(newKey);
                backend.RenamePrefixAsync(oldRelativePath, newRelativePath, CancellationToken.None)
                    .GetAwaiter().GetResult();
                changeWatcher?.RecordLocalRenamePrefix(oldKey, newKey);
                logger.LogInformation(
                    "Renamed prefix {Old}/ -> {New}/", oldRelativePath, newRelativePath);
            }
            else
            {
                using var oldGuard = changeWatcher?.BeginLocalKeyChange(oldKey);
                using var newGuard = changeWatcher?.BeginLocalKeyChange(newKey);
                backend.RenameAsync(oldRelativePath, newRelativePath, CancellationToken.None)
                    .GetAwaiter().GetResult();
                changeWatcher?.RecordLocalRename(oldKey, newKey);
                logger.LogInformation("Renamed {Old} -> {New}", oldRelativePath, newRelativePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rename failed for {Old} -> {New}", oldRelativePath, newRelativePath);
        }
    }

    /// <summary>
    /// True when the path resides in the lost+found directory; activity there is
    /// internal bookkeeping and must never be propagated back to the remote store.
    /// </summary>
    private static bool IsInLostAndFound(string relativePath) =>
        relativePath.StartsWith(
            ObjectStoreChangeWatcher.LostAndFoundDirectoryName + "\\",
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            relativePath,
            ObjectStoreChangeWatcher.LostAndFoundDirectoryName,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the change watcher and its collaborators, or returns null when the
    /// configuration disables it (read-only mode or polling-only with a non-positive interval).
    /// </summary>
    private ObjectStoreChangeWatcher? CreateChangeWatcher()
    {
        if (Options.ReadOnly)
        {
            // Read-only mode is for inspection scenarios — we still want to surface external
            // remote changes, but conflict resolution requires write access to lost+found, so
            // we gate the whole watcher off for the simplest correct first impl.
            logger.LogInformation("Read-only mode: object-store change watcher disabled.");
            return null;
        }

        var changeSource = CreateChangeSource();
        if (changeSource is null)
        {
            logger.LogInformation(
                "No active change source for {Mode} (sync interval = {Interval}s); object-store change watcher disabled.",
                Options.ChangeSource, Options.SyncIntervalSeconds);
            return null;
        }

        var sink = new VirtualizationCommandSink(
            virtualizationInstance,
            ProviderId,
            loggerFactory.CreateLogger<VirtualizationCommandSink>());
        var quarantine = new LostAndFoundQuarantine(
            syncRootPath,
            loggerFactory.CreateLogger<LostAndFoundQuarantine>());

        return new ObjectStoreChangeWatcher(
            changeSource,
            sink,
            quarantine,
            loggerFactory.CreateLogger<ObjectStoreChangeWatcher>());
    }

    /// <summary>
    /// Constructs the <see cref="IChangeSource"/> implied by
    /// <c>--change-source</c> and the supporting options. Returns null when the
    /// selected source is itself disabled (polling mode with a zero interval),
    /// so the caller can suppress watcher startup.
    /// </summary>
    private IChangeSource? CreateChangeSource()
    {
        switch (Options.ChangeSource)
        {
            case ChangeSourceKind.Polling:
                if (Options.SyncIntervalSeconds <= 0)
                {
                    logger.LogInformation(
                        "Sync interval is {Interval}s; polling change source disabled.",
                        Options.SyncIntervalSeconds);
                    return null;
                }
                if (Options.SyncMode == SyncMode.Full)
                {
                    logger.LogInformation(
                        "Polling in full mode: re-listing entire bucket every {Interval}s.",
                        Options.SyncIntervalSeconds);
                    return new PollingChangeSource(
                        backend,
                        TimeSpan.FromSeconds(Options.SyncIntervalSeconds),
                        loggerFactory.CreateLogger<PollingChangeSource>());
                }
                logger.LogInformation(
                    "Polling in on-demand mode: re-listing visited directories every {Interval}s.",
                    Options.SyncIntervalSeconds);
                return new OnDemandPollingChangeSource(
                    backend,
                    TimeSpan.FromSeconds(Options.SyncIntervalSeconds),
                    loggerFactory.CreateLogger<OnDemandPollingChangeSource>());

            case ChangeSourceKind.Events:
                if (string.IsNullOrEmpty(Options.EventQueue))
                {
                    // Program.cs validates this earlier; defensive guard for tests / library callers.
                    throw new InvalidOperationException(
                        "--change-source 'events' requires an SQS queue (--event-queue).");
                }
                return SqsChangeSourceFactory.Create(
                    Options.EventQueue!,
                    Options.Bucket,
                    Options.KeyPrefix,
                    Options.EndpointUrl,
                    Options.Region,
                    Options.Credentials,
                    loggerFactory.CreateLogger<SqsChangeSource>());

            default:
                throw new InvalidOperationException(
                    $"Unknown change source '{Options.ChangeSource}'.");
        }
    }

    /// <summary>
    /// Creates the virtualization root if missing and marks it as a ProjFS root,
    /// tolerating "already a vroot" return codes from earlier runs.
    /// </summary>
    private void EnsureVirtualizationRoot()
    {
        if (!Directory.Exists(syncRootPath))
        {
            Directory.CreateDirectory(syncRootPath);
        }

        // MarkDirectoryAsVirtualizationRoot writes a reparse point on the folder. On subsequent
        // runs it returns ReparsePointEncountered (or VirtualizationInvalidOp on older builds),
        // both of which we treat as success — the directory is already a vroot.
        var hr = VirtualizationInstance.MarkDirectoryAsVirtualizationRoot(syncRootPath, Guid.NewGuid());
        if (hr is not (HResult.Ok or HResult.VirtualizationInvalidOp or HResult.ReparsePointEncountered))
        {
            throw new InvalidOperationException($"Failed to mark virtualization root: {hr}");
        }
    }

    /// <summary>
    /// Materializes the immediate-children listing for an enumeration session.
    /// </summary>
    private async Task<List<ObjectInfo>> ListDirectoryAsync(string relativePath)
    {
        var list = new List<ObjectInfo>();
        await foreach (var entry in backend.ListAsync(relativePath, CancellationToken.None).ConfigureAwait(false))
        {
            list.Add(entry);
        }
        return list;
    }
}
