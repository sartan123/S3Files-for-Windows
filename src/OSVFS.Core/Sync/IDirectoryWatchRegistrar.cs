namespace OSVFS.Sync;

/// <summary>
/// Optional facet implemented by change sources that maintain a per-directory
/// watch set (currently <c>OnDemandPollingChangeSource</c>). The host calls
/// <see cref="RegisterWatchedDirectory"/> from its ProjFS
/// <c>StartDirectoryEnumeration</c> callback so the next polling tick re-lists
/// only the directories the user has actually visited — the AWS S3 Files
/// "metadata import on first access" behavior. Sources that don't track per
/// directory state (full polling, SQS) leave this interface unimplemented.
/// </summary>
internal interface IDirectoryWatchRegistrar
{
    /// <summary>
    /// Registers <paramref name="relativeDirectory"/> (Windows-style, backslash
    /// separated; empty string for the root) and every ancestor on its way back
    /// to the root. Subsequent polling ticks reconcile each registered directory
    /// using a delimited <c>ListObjectsV2</c>. Calls are idempotent and the
    /// watch set is monotonic — directories are never evicted.
    /// </summary>
    void RegisterWatchedDirectory(string relativeDirectory);

    /// <summary>
    /// Number of directories currently in the watch set. Exposed so the host
    /// can log the watch-set size after restart-time seeding without exposing
    /// the underlying collection.
    /// </summary>
    int WatchedDirectoryCount { get; }
}
