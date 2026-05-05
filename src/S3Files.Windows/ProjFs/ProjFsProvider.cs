using Microsoft.Extensions.Logging;
using Microsoft.Windows.ProjFS;
using S3Files.Windows.S3;
using System.Collections.Concurrent;
using System.Text;

namespace S3Files.Windows.ProjFs;

internal sealed class ProjFsProvider : IRequiredCallbacks, IDisposable
{
    private static readonly byte[] ProviderId = [1];

    private readonly ILogger<ProjFsProvider> logger;
    private readonly string syncRootPath;
    private readonly VirtualizationInstance virtualizationInstance;
    private readonly S3Backend backend;
    private readonly ConcurrentDictionary<Guid, DirectoryEnumerationSession> activeEnumerations = new();
    private readonly NotificationCallbacks notificationCallbacks;

    private bool virtualizationInstanceStarted;

    public ProjFsProviderOptions Options { get; }

    public ProjFsProvider(ProjFsProviderOptions options, ILogger<ProjFsProvider> logger)
    {
        Options = options;
        this.logger = logger;
        syncRootPath = options.VirtRoot;
        backend = new S3Backend(options.S3Bucket, options.EndpointUrl);

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
    }

    public void Dispose()
    {
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
                contentId: BuildContentId(etag),
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

    private static byte[] BuildContentId(string etag)
    {
        // ProjFS allows up to 128 bytes for contentId. We hash the ETag (or empty bytes if absent)
        // into a fixed 16-byte buffer to keep placeholders comparable across runs.
        var result = new byte[16];
        if (string.IsNullOrEmpty(etag)) return result;

        var trimmed = etag.AsSpan().Trim('"');
        var byteCount = Encoding.UTF8.GetByteCount(trimmed);
        Span<byte> bytes = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(trimmed, bytes);
        bytes[..Math.Min(bytes.Length, result.Length)].CopyTo(result);
        return result;
    }
}
