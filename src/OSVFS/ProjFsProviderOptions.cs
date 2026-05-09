using OSVFS.Configuration;
using OSVFS.Net;
using OSVFS.ObjectStore;

namespace OSVFS;

/// <summary>
/// Parsed command-line options that drive a <see cref="ProjFs.ProjFsProvider"/>
/// instance. Modeled as a record so individual fields can be replaced with
/// the <c>with</c> expression when the host needs to overlay process-level
/// settings on top of a mount-derived options object.
/// </summary>
internal sealed record ProjFsProviderOptions
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

    /// <summary>
    /// Which change-detection strategy the watcher uses. Default polling preserves
    /// the historical behavior; <c>Events</c> requires <see cref="EventQueue"/>.
    /// </summary>
    public ChangeSourceKind ChangeSource { get; init; } = ChangeSourceKind.Polling;

    /// <summary>
    /// Polling reconciliation strategy. <see cref="SyncMode.OnDemand"/> (the
    /// default) re-lists only the directories the user has actually visited;
    /// <see cref="SyncMode.Full"/> re-lists the whole bucket each tick. Only
    /// consulted when <see cref="ChangeSource"/> is
    /// <see cref="ChangeSourceKind.Polling"/>.
    /// </summary>
    public SyncMode SyncMode { get; init; } = SyncMode.OnDemand;

    /// <summary>
    /// SQS queue URL or queue name carrying EventBridge S3 notifications. Required
    /// when <see cref="ChangeSource"/> is <see cref="ChangeSourceKind.Events"/>.
    /// </summary>
    public string? EventQueue { get; init; }

    /// <summary>
    /// Optional static credentials resolved from the OSVFS credential store; null falls
    /// back to the SDK's default credential chain.
    /// </summary>
    public AwsCredential? Credentials { get; init; }

    /// <summary>
    /// Optional per-direction bandwidth ceilings. Each component null means
    /// "no limit on that direction".
    /// </summary>
    public BandwidthLimits BandwidthLimits { get; init; }

    /// <summary>
    /// Stream size at or above which uploads are routed through the multipart path.
    /// Null falls back to the backend default.
    /// </summary>
    public long? MultipartThresholdBytes { get; init; }

    /// <summary>
    /// Per-part size used by the multipart upload path. Null falls back to the backend default.
    /// </summary>
    public long? MultipartPartSizeBytes { get; init; }

    /// <summary>
    /// Total attempts (initial + retries) the AWS SDK makes on transient
    /// failures. Null falls back to the backend default; <c>1</c> disables
    /// retries.
    /// </summary>
    public int? RetryMaxAttempts { get; init; }

    /// <summary>
    /// When true, skip the bucket-versioning safety check and instead emit a
    /// repeated warning. Intended for CI / disposable buckets only.
    /// </summary>
    public bool AllowUnversioned { get; init; }
}
