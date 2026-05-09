using Microsoft.Extensions.Logging;
using OSVFS.Sync;

namespace OSVFS.ProjFs;

/// <summary>
/// Walks the virtualization root after restart and registers every existing
/// placeholder directory back into the change watcher's on-demand watch set.
/// ProjFS placeholders are persisted to disk, so the directories the user has
/// previously visited stay materialized across process restarts; replaying them
/// into the watch set restores the same polling coverage they had before exit.
/// Mirrors the AWS S3 Files "metadata stays even when data is evicted"
/// behavior — see README — On-demand sync.
/// </summary>
internal static class WatchSetSeeder
{
    /// <summary>
    /// Registers <paramref name="virtRoot"/> itself plus every directory beneath
    /// it (skipping <c>.osvfs-lost+found</c> and any reparse-point junctions) in
    /// <paramref name="watcher"/>'s watch set. Returns the number of directories
    /// registered (root included).
    /// </summary>
    public static int Seed(string virtRoot, ObjectStoreChangeWatcher watcher, ILogger logger)
    {
        if (!watcher.SupportsDirectoryWatchRegistration) return 0;
        if (!Directory.Exists(virtRoot)) return 0;

        var rootInfo = new DirectoryInfo(virtRoot);
        var registered = 0;

        // Always register the root so a polling pass without any user activity
        // still notices new top-level keys.
        watcher.RegisterWatchedDirectory(string.Empty);
        registered++;

        try
        {
            registered += SeedRecursive(rootInfo, virtRoot, watcher, logger);
        }
        catch (Exception ex)
        {
            // Seeding is best-effort — a partial walk shouldn't block startup.
            // Anything missed will be picked up the next time the user enumerates
            // the directory through ProjFS.
            logger.LogWarning(
                ex, "Watch-set seeding stopped early under '{Root}'; partial coverage only.", virtRoot);
        }

        logger.LogInformation(
            "On-demand watch set seeded with {Count} pre-existing director{Suffix}.",
            registered, registered == 1 ? "y" : "ies");

        return registered;
    }

    /// <summary>
    /// Depth-first walk that registers every subdirectory's relative path into the
    /// watcher. Symlinks/junctions are skipped to avoid loops; the lost+found
    /// directory is skipped because it is an internal-only quarantine area.
    /// </summary>
    private static int SeedRecursive(
        DirectoryInfo current,
        string virtRoot,
        ObjectStoreChangeWatcher watcher,
        ILogger logger)
    {
        var registered = 0;
        IEnumerable<DirectoryInfo> children;
        try
        {
            children = current.EnumerateDirectories();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            logger.LogDebug(
                ex, "Skipping unreadable directory during watch-set seeding: {Path}", current.FullName);
            return 0;
        }

        foreach (var child in children)
        {
            // Reparse points (junctions, symlinks) can loop or escape the virt-root;
            // ProjFS placeholders never have the ReparsePoint attribute.
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            if (string.Equals(
                    child.Name,
                    ObjectStoreChangeWatcher.LostAndFoundDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(virtRoot, child.FullName);
            watcher.RegisterWatchedDirectory(relative);
            registered++;

            registered += SeedRecursive(child, virtRoot, watcher, logger);
        }

        return registered;
    }
}
