using OSVFS.ObjectStore;

namespace OSVFS;

/// <summary>
/// Parsed command-line options that drive a <see cref="ProjFs.ProjFsProvider"/>
/// instance.
/// </summary>
internal sealed class ProjFsProviderOptions
{
    /// <summary>
    /// Object-store provider backing the virtualization root.
    /// </summary>
    public ObjectStoreProvider Provider { get; init; } = ObjectStoreProvider.S3;

    /// <summary>
    /// Name of the bucket/container projected into the virtualization root.
    /// </summary>
    public required string Bucket { get; init; }

    /// <summary>
    /// Local directory marked as the ProjFS virtualization root.
    /// </summary>
    public required string VirtRoot { get; init; }

    /// <summary>
    /// Optional endpoint override for S3-compatible servers (LocalStack, MinIO).
    /// </summary>
    public string? EndpointUrl { get; init; }

    /// <summary>
    /// Optional region; falls back to the SDK's standard resolution chain when null.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Optional key prefix; only objects beneath it are projected into the root.
    /// </summary>
    public string? KeyPrefix { get; init; }

    /// <summary>
    /// When true, raises log verbosity to Debug.
    /// </summary>
    public bool Verbose { get; init; }

    /// <summary>
    /// When true, ignore notification-driven local mutations and disable the change watcher.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Polling interval (seconds) for the change watcher; zero disables it.
    /// </summary>
    public int SyncIntervalSeconds { get; init; } = 30;
}
