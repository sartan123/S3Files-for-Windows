using Microsoft.Extensions.Logging;
using Microsoft.Windows.ProjFS;
using OSVFS.Sync;

namespace OSVFS.Sync.ProjFs;

/// <summary>
/// Adapts <see cref="VirtualizationInstance"/> to <see cref="IProjFsCommandSink"/>. All ProjFS
/// HRESULTs and <see cref="UpdateFailureCause"/> values are translated into the small set of
/// outcomes the watcher reasons about.
/// </summary>
internal sealed class VirtualizationCommandSink(
    VirtualizationInstance instance,
    byte[] providerId,
    ILogger<VirtualizationCommandSink> logger)
    : IProjFsCommandSink
{
    /// <inheritdoc/>
    public bool TryWritePlaceholder(
        string relativePath, long size, DateTimeOffset lastModified, byte[] contentId, bool isDirectory)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;

        var ts = lastModified == default ? DateTime.UtcNow : lastModified.UtcDateTime;
        var attrs = isDirectory ? FileAttributes.Directory : FileAttributes.Normal;
        try
        {
            var hr = instance.WritePlaceholderInfo(
                relativePath: relativePath,
                creationTime: ts,
                lastAccessTime: ts,
                lastWriteTime: ts,
                changeTime: ts,
                fileAttributes: attrs,
                endOfFile: size,
                isDirectory: isDirectory,
                contentId: contentId,
                providerId: providerId);
            if (hr == HResult.Ok) return true;

            // VirtualizationInvalidOp is the typical "parent not materialized" / "already exists"
            // signal — log at debug since it's expected on best-effort placeholder injection.
            logger.LogDebug(
                "WritePlaceholderInfo({Path}) returned {HResult}.", relativePath, hr);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "WritePlaceholderInfo({Path}) threw.", relativePath);
            return false;
        }
    }

    /// <inheritdoc/>
    public ProjFsUpdateOutcome TryUpdateFile(
        string relativePath, long size, DateTimeOffset lastModified, byte[] contentId)
    {
        if (string.IsNullOrEmpty(relativePath)) return ProjFsUpdateOutcome.Failed;

        var ts = lastModified == default ? DateTime.UtcNow : lastModified.UtcDateTime;
        try
        {
            // Allow dirty *metadata* (timestamps/attrs only): replacing a placeholder when only
            // metadata diverged doesn't lose user data, and matches the spec's treatment of
            // conflicts as data-level events. Dirty *data* surfaces as DirtyConflict so the
            // caller can quarantine before retrying.
            var hr = instance.UpdateFileIfNeeded(
                relativePath: relativePath,
                creationTime: ts,
                lastAccessTime: ts,
                lastWriteTime: ts,
                changeTime: ts,
                fileAttributes: FileAttributes.Normal,
                endOfFile: size,
                contentId: contentId,
                providerId: providerId,
                updateFlags: UpdateType.AllowDirtyMetadata,
                failureReason: out var cause);
            return Translate(hr, cause);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UpdateFileIfNeeded({Path}) threw.", relativePath);
            return ProjFsUpdateOutcome.Failed;
        }
    }

    /// <inheritdoc/>
    public ProjFsUpdateOutcome TryDeleteFile(string relativePath, bool allowDirty)
    {
        if (string.IsNullOrEmpty(relativePath)) return ProjFsUpdateOutcome.Failed;

        try
        {
            // For a normal "S3 deleted, ours is clean" delete we tolerate dirty metadata only.
            // For conflict resolution after quarantine the caller passes allowDirty=true to
            // overwrite the dirty data placeholder.
            var flags = allowDirty
                ? UpdateType.AllowDirtyData
                  | UpdateType.AllowDirtyMetadata
                  | UpdateType.AllowReadOnly
                  | UpdateType.AllowTombstone
                : UpdateType.AllowDirtyMetadata | UpdateType.AllowTombstone;

            var hr = instance.DeleteFile(relativePath, flags, out var cause);
            return Translate(hr, cause);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeleteFile({Path}) threw.", relativePath);
            return ProjFsUpdateOutcome.Failed;
        }
    }

    /// <summary>
    /// Maps a ProjFS HRESULT plus failure-cause flags to the small outcome enum the
    /// watcher reasons about.
    /// </summary>
    private static ProjFsUpdateOutcome Translate(HResult hr, UpdateFailureCause cause)
    {
        if (hr == HResult.Ok) return ProjFsUpdateOutcome.Updated;
        if (hr is HResult.FileNotFound or HResult.PathNotFound)
        {
            return ProjFsUpdateOutcome.NotFound;
        }
        if (cause.HasFlag(UpdateFailureCause.DirtyData)
            || cause.HasFlag(UpdateFailureCause.DirtyMetadata))
        {
            return ProjFsUpdateOutcome.DirtyConflict;
        }
        return ProjFsUpdateOutcome.Failed;
    }
}
