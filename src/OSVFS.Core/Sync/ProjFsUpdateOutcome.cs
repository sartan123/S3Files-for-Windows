namespace OSVFS.Sync;

/// <summary>
/// Outcome of an attempt to mutate the ProjFS-managed view of a file in response to an S3 change.
/// Mirrors the small subset of ProjFS HRESULT/UpdateFailureCause values we care about.
/// </summary>
internal enum ProjFsUpdateOutcome
{
    /// <summary>
    /// The file system was updated successfully.
    /// </summary>
    Updated,

    /// <summary>
    /// No placeholder/file existed at the target path; nothing to do.
    /// </summary>
    NotFound,

    /// <summary>
    /// The local copy has user-data modifications that haven't been synced back to S3 yet.
    /// Per the S3 Files spec, the S3 bucket is the source of truth, so the caller must
    /// quarantine the local copy and replace it with the S3 version.
    /// </summary>
    DirtyConflict,

    /// <summary>
    /// The operation failed for an unexpected reason.
    /// </summary>
    Failed,
}
