namespace OSVFS.Sync;

/// <summary>
/// Saves a copy of a locally-modified file before the watcher overwrites it with the S3 version.
/// Per the S3 Files spec, conflicting local copies are preserved under
/// <c>.s3files-lost+found-{filesystemId}</c> in the file system root so the user can recover them.
/// </summary>
internal interface ILostAndFoundQuarantine
{
    /// <summary>
    /// Copies the file at <paramref name="relativePath"/> (relative to the virtualization root)
    /// into the lost+found area. Returns false if the source file no longer exists or the
    /// quarantine attempt failed; callers should still proceed with replacing the local copy
    /// (matching the spec's "S3 is source of truth" guarantee) but will surface a warning.
    /// </summary>
    bool TryQuarantine(string relativePath);
}
