using Microsoft.Windows.ProjFS;

namespace S3Files.Windows.ProjFs;

internal sealed class NotificationCallbacks
{
    private readonly ProjFsProvider provider;

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

    private bool ReadOnly => provider.Options.ReadOnly;

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

    public void OnNewFileCreated(
        string relativePath,
        bool isDirectory,
        uint triggeringProcessId,
        string triggeringProcessImageFileName,
        out NotificationType notificationMask)
    {
        notificationMask = NotificationType.UseExistingMask;
    }

    public void OnFileOverwritten(
        string relativePath,
        bool isDirectory,
        uint triggeringProcessId,
        string triggeringProcessImageFileName,
        out NotificationType notificationMask)
    {
        notificationMask = NotificationType.UseExistingMask;
    }

    public bool OnPreDelete(
        string relativePath,
        bool isDirectory,
        uint triggeringProcessId,
        string triggeringProcessImageFileName) => !ReadOnly;

    public bool OnPreRename(
        string relativePath,
        string destinationPath,
        uint triggeringProcessId,
        string triggeringProcessImageFileName) => !ReadOnly;

    public bool OnPreCreateHardlink(
        string relativePath,
        string destinationPath,
        uint triggeringProcessId,
        string triggeringProcessImageFileName) => !ReadOnly;

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

    public void OnHardlinkCreated(
        string relativePath,
        string destinationPath,
        uint triggeringProcessId,
        string triggeringProcessImageFileName)
    {
    }

    public void OnFileHandleClosedNoModification(
        string relativePath,
        bool isDirectory,
        uint triggeringProcessId,
        string triggeringProcessImageFileName)
    {
    }

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

    public bool OnFilePreConvertToFull(
        string relativePath,
        uint triggeringProcessId,
        string triggeringProcessImageFileName) => !ReadOnly;
}
