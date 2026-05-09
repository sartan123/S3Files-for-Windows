namespace OSVFS.Configuration;

/// <summary>
/// Selects how the polling change source decides which keys to re-list each tick.
/// Mirrors the AWS S3 Files synchronization design (see README — On-demand sync).
/// </summary>
internal enum SyncMode
{
    /// <summary>
    /// Re-list only the directories the user has actually visited (tracked through
    /// the ProjFS <c>StartDirectoryEnumeration</c> callback) using
    /// <c>Delimiter='/'</c>. API cost scales with visited directories rather than
    /// bucket size; unvisited subtrees are not polled. Default.
    /// </summary>
    OnDemand,

    /// <summary>
    /// Re-list the entire bucket (or configured prefix) each tick. Higher API cost
    /// but every key is reconciled. Preserves the original Phase&#160;1 behavior.
    /// </summary>
    Full,
}
