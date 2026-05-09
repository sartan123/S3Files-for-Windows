using OSVFS.ObjectStore;

namespace OSVFS.Configuration;

/// <summary>
/// Strongly-typed view of an <c>osvfs.toml</c> file. Every property is nullable
/// because settings absent from the file fall back to the next source in the
/// CLI &gt; project file &gt; user file &gt; built-in default chain.
/// </summary>
internal sealed class OsvfsConfigFile
{
    /// <summary>
    /// Object-store provider backing the virtualization root.
    /// </summary>
    public ObjectStoreProvider? Provider { get; init; }

    /// <summary>
    /// Bucket (S3/GCS) or container (Azure) projected into the virtualization root.
    /// </summary>
    public string? Bucket { get; init; }

    /// <summary>
    /// Local directory marked as the ProjFS virtualization root.
    /// </summary>
    public string? RootFolder { get; init; }

    /// <summary>
    /// Endpoint override for S3-compatible servers (LocalStack, MinIO).
    /// </summary>
    public string? EndpointUrl { get; init; }

    /// <summary>
    /// AWS region; null falls back to the SDK's default resolution chain.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Optional key prefix; only objects beneath it are projected into the root.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// When true, raises log verbosity to Debug.
    /// </summary>
    public bool? Verbose { get; init; }

    /// <summary>
    /// When true, ignore notification-driven local mutations and disable the change watcher.
    /// </summary>
    public bool? ReadOnly { get; init; }

    /// <summary>
    /// Polling interval (seconds) for the change watcher; zero disables it.
    /// </summary>
    public int? SyncIntervalSeconds { get; init; }

    /// <summary>
    /// Which change-detection strategy the watcher uses (polling or events).
    /// </summary>
    public ChangeSourceKind? ChangeSource { get; init; }

    /// <summary>
    /// SQS queue URL or queue name carrying EventBridge S3 notifications.
    /// </summary>
    public string? EventQueue { get; init; }

    /// <summary>
    /// Profile name in the OSVFS credential store; null means use the SDK's default chain.
    /// </summary>
    public string? AwsProfile { get; init; }

    /// <summary>
    /// rclone-style upload bandwidth ceiling (e.g. <c>"5M"</c>). Null disables.
    /// </summary>
    public string? BandwidthUp { get; init; }

    /// <summary>
    /// rclone-style download bandwidth ceiling (e.g. <c>"10M"</c>). Null disables.
    /// </summary>
    public string? BandwidthDown { get; init; }

    /// <summary>
    /// Stream size at or above which uploads are routed through the multipart
    /// path (e.g. <c>"16M"</c>). Null falls back to the backend default.
    /// </summary>
    public string? MultipartThreshold { get; init; }

    /// <summary>
    /// Per-part size used by multipart uploads (e.g. <c>"16M"</c>). Null falls
    /// back to the backend default.
    /// </summary>
    public string? MultipartPartSize { get; init; }

    /// <summary>
    /// Console log output format. Null falls back to <see cref="LogFormat.Text"/>.
    /// </summary>
    public LogFormat? LogFormat { get; init; }

    /// <summary>
    /// When true, skip the bucket-versioning safety check and instead emit a
    /// repeated warning. Intended for CI / disposable buckets only.
    /// </summary>
    public bool? AllowUnversioned { get; init; }

    /// <summary>
    /// Returns a copy of this config with values from <paramref name="overlay"/> taking
    /// precedence wherever they are non-null. Used to fold the project-local
    /// <c>osvfs.toml</c> on top of the user-global <c>%APPDATA%/OSVFS/config.toml</c>.
    /// </summary>
    public OsvfsConfigFile MergeOverlay(OsvfsConfigFile overlay) => new()
    {
        Provider = overlay.Provider ?? Provider,
        Bucket = overlay.Bucket ?? Bucket,
        RootFolder = overlay.RootFolder ?? RootFolder,
        EndpointUrl = overlay.EndpointUrl ?? EndpointUrl,
        Region = overlay.Region ?? Region,
        Prefix = overlay.Prefix ?? Prefix,
        Verbose = overlay.Verbose ?? Verbose,
        ReadOnly = overlay.ReadOnly ?? ReadOnly,
        SyncIntervalSeconds = overlay.SyncIntervalSeconds ?? SyncIntervalSeconds,
        ChangeSource = overlay.ChangeSource ?? ChangeSource,
        EventQueue = overlay.EventQueue ?? EventQueue,
        AwsProfile = overlay.AwsProfile ?? AwsProfile,
        BandwidthUp = overlay.BandwidthUp ?? BandwidthUp,
        BandwidthDown = overlay.BandwidthDown ?? BandwidthDown,
        MultipartThreshold = overlay.MultipartThreshold ?? MultipartThreshold,
        MultipartPartSize = overlay.MultipartPartSize ?? MultipartPartSize,
        LogFormat = overlay.LogFormat ?? LogFormat,
        AllowUnversioned = overlay.AllowUnversioned ?? AllowUnversioned,
    };
}
