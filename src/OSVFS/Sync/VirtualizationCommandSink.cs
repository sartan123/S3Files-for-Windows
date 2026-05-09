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
    /// Number of attempts <see cref="TryConvertFullToPlaceholder"/> makes when
    /// ProjFS rejects the conversion with <see cref="HResult.AccessDenied"/>.
    /// AV scanners and the Windows search indexer routinely grab a brief handle
    /// against a freshly-written file; a handful of short retries clears that
    /// contention window without busy-waiting for long.
    /// </summary>
    private const int ConvertToPlaceholderMaxAttempts = 5;

    /// <summary>
    /// Back-off between conversion retries. Empirically enough to clear typical
    /// AV/indexer handles without making the upload-handler block noticeably.
    /// </summary>
    private static readonly TimeSpan ConvertToPlaceholderRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <inheritdoc/>
    public bool TryConvertFullToPlaceholder(
        string relativePath, long size, DateTimeOffset lastModified, byte[] contentId)
    {
        if (string.IsNullOrEmpty(relativePath)) return false;

        var ts = lastModified == default ? DateTime.UtcNow : lastModified.UtcDateTime;

        var lastHr = HResult.InternalError;
        var lastCause = UpdateFailureCause.NoFailure;
        for (var attempt = 0; attempt < ConvertToPlaceholderMaxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(ConvertToPlaceholderRetryDelay);
            }

            try
            {
                // After a successful local upload, the file on disk matches the bucket exactly,
                // so dropping the dirty flags and re-binding it as a placeholder for the new
                // ETag is safe: any future read just hydrates from the backend.
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
                    updateFlags: UpdateType.AllowDirtyData
                        | UpdateType.AllowDirtyMetadata
                        | UpdateType.AllowReadOnly,
                    failureReason: out var cause);
                if (hr == HResult.Ok) return true;

                lastHr = hr;
                lastCause = cause;

                // Only AccessDenied is plausibly transient; other HRESULTs won't be helped by waiting.
                if (hr != HResult.AccessDenied) break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "UpdateFileIfNeeded({Path}) for full→placeholder conversion threw.", relativePath);
                return false;
            }
        }

        logger.LogDebug(
            "UpdateFileIfNeeded({Path}) for full→placeholder conversion returned {HResult} (cause={Cause}) after {Attempts} attempts.",
            relativePath, lastHr, lastCause, ConvertToPlaceholderMaxAttempts);
        return false;
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
