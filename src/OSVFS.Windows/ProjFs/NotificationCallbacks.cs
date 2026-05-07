using Microsoft.Windows.ProjFS;

namespace OSVFS.ProjFs;

/// <summary>
/// Wires ProjFS notification delegates to <see cref="ProjFsProvider"/> handlers,
/// honoring read-only mode by short-circuiting any write-side notification.
/// </summary>
internal sealed class NotificationCallbacks
{
    private readonly ProjFsProvider provider;

    /// <summary>
    /// Subscribes to whichever notifications the provided mappings request.
    /// </summary>
    public NotificationCallbacks(
        ProjFsProvider provider,
        VirtualizationInstance virtInstance,
        IReadOnlyCollection<NotificationMapping> notificationMappings)
    {
        this.provider = provider;

        var notification = NotificationType.None;
        foreach (var mapping in notificationMappings)
        {
            notification |= mapping.NotificationMask;
        }

        if (notification.HasFlag(NotificationType.FileOpened))
        {
            virtInstance.OnNotifyFileOpened = OnFileOpened;
        }
        if (notification.HasFlag(NotificationType.NewFileCreated))
        {
            virtInstance.OnNotifyNewFileCreated = OnNewFileCreated;
        }
        if (notification.HasFlag(NotificationType.FileOverwritten))
        {
            virtInstance.OnNotifyFileOverwritten = OnFileOverwritten;
        }
        if (notification.HasFlag(NotificationType.PreDelete))
        {
            virtInstance.OnNotifyPreDelete = OnPreDelete;
        }
        if (notification.HasFlag(NotificationType.PreRename))
        {
            virtInstance.OnNotifyPreRename = OnPreRename;
        }
        if (notification.HasFlag(NotificationType.PreCreateHardlink))
        {
            virtInstance.OnNotifyPreCreateHardlink = OnPreCreateHardlink;
        }
        if (notification.HasFlag(NotificationType.FileRenamed))
        {
            virtInstance.OnNotifyFileRenamed = OnFileRenamed;
        }
        if (notification.HasFlag(NotificationType.HardlinkCreated))
        {
            virtInstance.OnNotifyHardlinkCreated = OnHardlinkCreated;
        }
        if (notification.HasFlag(NotificationType.FileHandleClosedNoModification))
        {
            virtInstance.OnNotifyFileHandleClosedNoModification = OnFileHandleClosedNoModification;
        }
        if (notification.HasFlag(NotificationType.FileHandleClosedFileModified) ||
            notification.HasFlag(NotificationType.FileHandleClosedFileDeleted))
        {
            virtInstance.OnNotifyFileHandleClosedFileModifiedOrDeleted = OnFileHandleClosedFileModifiedOrDeleted;
        }
        if (notification.HasFlag(NotificationType.FilePreConvertToFull))
        {
            virtInstance.OnNotifyFilePreConvertToFull = OnFilePreConvertToFull;
        }
    }

    /// <summary>
    /// Mirrors the provider's read-only flag for notification gating.
    /// </summary>
    private bool ReadOnly => provider.Options.ReadOnly;

    /// <summary>
    /// Notification: a placeholder/file was opened; we keep the existing mask.
    /// </summary>
    public bool OnFileOpened(
        string relativePath,
        bool isDirectory,
        uint triggeringProcessId,
        string triggeringProcessImageFileName,
        out NotificationType notificationMask)
    {
        notificationMask = NotificationType.UseExistingMask;
        return true;
    }

    /// <summary>
    /// Notification: a brand-new local file was created; upload happens later on
    /// handle close, so we just preserve the mask here.
    /// </summary>
    public void OnNewFileCreated(
        string relativePath,
        bool isDirectory,
        uint triggeringProcessId,
        string triggeringProcessImageFileName,
        out NotificationType notificationMask)
    {
        notificationMask = NotificationType.UseExistingMask;
    }

    /// <summary>
    /// Notification: a file was overwritten; upload happens on handle close.
    /// </summary>
    public void OnFileOverwritten(
        string relativePath,
        bool isDirectory,
        uint triggeringProcessId,
        string triggeringProcessImageFileName,
        out NotificationType notificationMask)
    {
        notificationMask = NotificationType.UseExistingMask;
    }

    /// <summary>
    /// Notification: blocks deletes when the provider is read-only.
    /// </summary>
    public bool OnPreDelete(
        string relativePath,
        bool isDirectory,
        uint triggeringProcessId,
        string triggeringProcessImageFileName) => !ReadOnly;

    /// <summary>
    /// Notification: blocks renames when the provider is read-only.
    /// </summary>
    public bool OnPreRename(
        string relativePath,
        string destinationPath,
        uint triggeringProcessId,
        string triggeringProcessImageFileName) => !ReadOnly;

    /// <summary>
    /// Notification: blocks hardlink creation when the provider is read-only.
    /// </summary>
    public bool OnPreCreateHardlink(
        string relativePath,
        string destinationPath,
        uint triggeringProcessId,
        string triggeringProcessImageFileName) => !ReadOnly;

    /// <summary>
    /// Notification: forwards rename to the provider so the corresponding S3 object
    /// or prefix is moved, unless read-only.
    /// </summary>
    public void OnFileRenamed(
        string relativePath,
        string destinationPath,
        bool isDirectory,
        uint triggeringProcessId,
        string triggeringProcessImageFileName,
        out NotificationType notificationMask)
    {
        notificationMask = NotificationType.UseExistingMask;
        if (!ReadOnly)
        {
            provider.HandleFileRenamed(relativePath, destinationPath, isDirectory);
        }
    }

    /// <summary>
    /// Notification: hardlink-created carries no state we need to propagate to S3.
    /// </summary>
    public void OnHardlinkCreated(
        string relativePath,
        string destinationPath,
        uint triggeringProcessId,
        string triggeringProcessImageFileName)
    {
    }

    /// <summary>
    /// Notification: nothing to do when a handle closed without modifying the file.
    /// </summary>
    public void OnFileHandleClosedNoModification(
        string relativePath,
        bool isDirectory,
        uint triggeringProcessId,
        string triggeringProcessImageFileName)
    {
    }

    /// <summary>
    /// Notification: routes a closed-with-changes handle to the provider's upload
    /// or delete handler; deletion takes precedence.
    /// </summary>
    public void OnFileHandleClosedFileModifiedOrDeleted(
        string relativePath,
        bool isDirectory,
        bool isFileModified,
        bool isFileDeleted,
        uint triggeringProcessId,
        string triggeringProcessImageFileName)
    {
        if (ReadOnly) return;

        // Deletion takes precedence: a deleted file cannot be uploaded.
        if (isFileDeleted)
        {
            provider.HandleFileDeleted(relativePath, isDirectory);
            return;
        }

        if (isFileModified)
        {
            provider.HandleFileModified(relativePath, isDirectory);
        }
    }

    /// <summary>
    /// Notification: blocks placeholder-to-full conversion when the provider is
    /// read-only, since the resulting full file would diverge from S3.
    /// </summary>
    public bool OnFilePreConvertToFull(
        string relativePath,
        uint triggeringProcessId,
        string triggeringProcessImageFileName) => !ReadOnly;
}
