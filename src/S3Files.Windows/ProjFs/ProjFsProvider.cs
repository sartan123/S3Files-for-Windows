using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.ProjFS;
using S3Files.Windows.S3;
using S3Files.Windows.Sync;
using S3Files.Windows.Sync.ProjFs;
using System.Collections.Concurrent;

namespace S3Files.Windows.ProjFs;

internal sealed class ProjFsProvider : IRequiredCallbacks, IDisposable
{
    private static readonly byte[] ProviderId = [1];

    private readonly ILogger<ProjFsProvider> logger;
    private readonly ILoggerFactory loggerFactory;
    private readonly string syncRootPath;
    private readonly VirtualizationInstance virtualizationInstance;
    private readonly S3Backend backend;
    private readonly ConcurrentDictionary<Guid, DirectoryEnumerationSession> activeEnumerations = new();
    private readonly NotificationCallbacks notificationCallbacks;
    private readonly S3ChangeWatcher? changeWatcher;

    private bool virtualizationInstanceStarted;

    public ProjFsProviderOptions Options { get; }

    public ProjFsProvider(ProjFsProviderOptions options, ILogger<ProjFsProvider> logger)
        : this(options, logger, NullLoggerFactory.Instance)
    {
    }

    public ProjFsProvider(
        ProjFsProviderOptions options,
        ILogger<ProjFsProvider> logger,
        ILoggerFactory loggerFactory)
    {
        Options = options;
        this.logger = logger;
        this.loggerFactory = loggerFactory;
        syncRootPath = options.VirtRoot;
        backend = new S3Backend(options.S3Bucket, options.EndpointUrl, options.KeyPrefix, options.Region);

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

    public bool StartVirtualization()
    {
        var hr = virtualizationInstance.StartVirtualizing(this);
        if (hr != HResult.Ok)
        {
            logger.LogError("StartVirtualizing failed: {HResult}", hr);
            return false;
        }
        virtualizationInstanceStarted = true;

        // The watcher is fire-and-forget: it self-cancels on Dispose. Polling only starts
        // after virtualization is up so the command sink is safe to call.
        _ = changeWatcher?.StartAsync(CancellationToken.None);
        return true;
    }

    public HResult StartDirectoryEnumerationCallback(
        int commandId,
        Guid enumerationId,
        string relativePath,
        uint triggeringProcessId,
        string triggeringProcessImageFileName)
    {
        try
        {
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

    public HResult EndDirectoryEnumerationCallback(Guid enumerationId)
    {
        activeEnumerations.TryRemove(enumerationId, out _);
        return HResult.Ok;
    }

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
                contentId: S3Util.BuildContentId(etag),
                providerId: ProviderId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetPlaceholderInfo({RelativePath})", relativePath);
            return HResult.InternalError;
        }
    }

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

        var s3Key = S3Util.ToS3Key(relativePath);
        using var _ = changeWatcher?.BeginLocalKeyChange(s3Key);

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
            changeWatcher?.RecordLocalUpload(s3Key, result.ETag, result.Size, result.LastModified);

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

    public void HandleFileDeleted(string relativePath, bool isDirectory)
    {
        if (Options.ReadOnly || string.IsNullOrEmpty(relativePath)) return;
        if (IsInLostAndFound(relativePath)) return;

        var s3Key = S3Util.ToS3Key(relativePath);

        try
        {
            if (isDirectory)
            {
                using var _ = changeWatcher?.BeginLocalPrefixChange(s3Key);
                backend.DeletePrefixAsync(relativePath, CancellationToken.None)
                    .GetAwaiter().GetResult();
                changeWatcher?.RecordLocalDeletePrefix(s3Key);
                logger.LogInformation("Deleted prefix {RelativePath}/", relativePath);
            }
            else
            {
                using var _ = changeWatcher?.BeginLocalKeyChange(s3Key);
                backend.DeleteAsync(relativePath, CancellationToken.None)
                    .GetAwaiter().GetResult();
                changeWatcher?.RecordLocalDelete(s3Key);
                logger.LogInformation("Deleted {RelativePath}", relativePath);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Delete failed for {RelativePath}", relativePath);
        }
    }

    public void HandleFileRenamed(string oldRelativePath, string newRelativePath, bool isDirectory)
    {
        if (Options.ReadOnly) return;
        if (string.IsNullOrEmpty(oldRelativePath) || string.IsNullOrEmpty(newRelativePath)) return;
        if (IsInLostAndFound(oldRelativePath) || IsInLostAndFound(newRelativePath)) return;

        var oldKey = S3Util.ToS3Key(oldRelativePath);
        var newKey = S3Util.ToS3Key(newRelativePath);

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

    private static bool IsInLostAndFound(string relativePath) =>
        relativePath.StartsWith(
            S3ChangeWatcher.LostAndFoundDirectoryName + "\\",
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            relativePath,
            S3ChangeWatcher.LostAndFoundDirectoryName,
            StringComparison.OrdinalIgnoreCase);

    private S3ChangeWatcher? CreateChangeWatcher()
    {
        if (Options.ReadOnly)
        {
            // Read-only mode is for inspection scenarios — we still want to surface external
            // S3 changes, but conflict resolution requires write access to lost+found, so we
            // gate the whole watcher off for the simplest correct first impl.
            logger.LogInformation("Read-only mode: S3 change watcher disabled.");
            return null;
        }

        if (Options.SyncIntervalSeconds <= 0)
        {
            logger.LogInformation(
                "Sync interval is {Interval}s; S3 change watcher disabled.",
                Options.SyncIntervalSeconds);
            return null;
        }

        var sink = new VirtualizationCommandSink(
            virtualizationInstance,
            ProviderId,
            loggerFactory.CreateLogger<VirtualizationCommandSink>());
        var quarantine = new LostAndFoundQuarantine(
            syncRootPath,
            loggerFactory.CreateLogger<LostAndFoundQuarantine>());

        return new S3ChangeWatcher(
            backend,
            sink,
            quarantine,
            TimeSpan.FromSeconds(Options.SyncIntervalSeconds),
            loggerFactory.CreateLogger<S3ChangeWatcher>());
    }

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

    private async Task<List<S3ObjectInfo>> ListDirectoryAsync(string relativePath)
    {
        var list = new List<S3ObjectInfo>();
        await foreach (var entry in backend.ListAsync(relativePath, CancellationToken.None).ConfigureAwait(false))
        {
            list.Add(entry);
        }
        return list;
    }
}
