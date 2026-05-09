namespace OSVFS.Sync;

/// <summary>
/// Abstracts the subset of ProjFS VirtualizationInstance operations the change-watcher needs
/// to apply S3-side mutations to the local view. Decoupling this from
/// <c>Microsoft.Windows.ProjFS</c> keeps the watcher cross-platform-testable.
/// </summary>
internal interface IProjFsCommandSink
{
    /// <summary>
    /// Tries to write a placeholder for a file or directory. Returns false if the
    /// parent path is not yet materialized in ProjFS — in that case the next directory
    /// enumeration will pick the new entry up naturally.
    /// </summary>
    bool TryWritePlaceholder(
        string relativePath,
        long size,
        DateTimeOffset lastModified,
        byte[] contentId,
        bool isDirectory);

    /// <summary>
    /// Tries to update an existing placeholder/file with new contents/metadata.
    /// If the local copy has user-data dirt the call returns
    /// <see cref="ProjFsUpdateOutcome.DirtyConflict"/> without overwriting.
    /// </summary>
    ProjFsUpdateOutcome TryUpdateFile(
        string relativePath,
        long size,
        DateTimeOffset lastModified,
        byte[] contentId);

    /// <summary>
    /// Tries to delete the placeholder/file. If <paramref name="allowDirty"/> is false,
    /// dirty data causes <see cref="ProjFsUpdateOutcome.DirtyConflict"/>; if true, dirty data is
    /// overwritten (used after the local copy has been quarantined to lost+found).
    /// </summary>
    ProjFsUpdateOutcome TryDeleteFile(string relativePath, bool allowDirty);

    /// <summary>
    /// Converts a locally-created "full file" — one that was just uploaded to the
    /// backend — into a placeholder bound to <paramref name="contentId"/>. ProjFS
    /// otherwise keeps such a file marked DirtyData forever, so a subsequent
    /// remote-side delete would be misread as a conflict and the local copy would
    /// be quarantined even though it matches what we just uploaded.
    /// </summary>
    bool TryConvertFullToPlaceholder(
        string relativePath,
        long size,
        DateTimeOffset lastModified,
        byte[] contentId);
}
