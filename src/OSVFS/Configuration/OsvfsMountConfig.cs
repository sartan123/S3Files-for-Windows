using OSVFS.ObjectStore;

namespace OSVFS.Configuration;

/// <summary>
/// Strongly-typed view of a single mount entry inside <c>osvfs.toml</c>.
/// Each entry corresponds to one virtualization root and one ProjFs provider
/// instance at runtime. Fields are nullable because they fall back to the
/// CLI / built-in defaults when omitted from the file.
/// </summary>
internal sealed class OsvfsMountConfig
{
    /// <summary>
    /// Name used to address this mount on the CLI (<c>osvfs mount --name &lt;name&gt;</c>)
    /// and in log entries. Must be unique within a config file. Defaults to
    /// <c>"default"</c> when the legacy single-mount form is used.
    /// </summary>
    public string Name { get; init; } = "default";

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
    /// Polling reconciliation strategy — on-demand (per visited directory) or
    /// full (whole bucket). Default <see cref="SyncMode.OnDemand"/>; only
    /// consulted when <see cref="ChangeSource"/> is <see cref="ChangeSourceKind.Polling"/>.
    /// </summary>
    public SyncMode? SyncMode { get; init; }

    /// <summary>
    /// SQS queue URL or queue name carrying EventBridge S3 notifications.
    /// </summary>
    public string? EventQueue { get; init; }

    /// <summary>
    /// Profile name in the OSVFS credential store; null means use the SDK's default chain.
    /// </summary>
    public string? AwsProfile { get; init; }

    /// <summary>
    /// Azure Storage connection string (only consulted when
    /// <see cref="Provider"/> is <see cref="ObjectStoreProvider.AzureBlob"/>).
    /// Carries account name, key, and endpoints in one TOML value — the
    /// shape Azurite hands out by default and what most operators paste from
    /// the Azure Portal "Access keys" blade. SAS / Managed Identity /
    /// <c>DefaultAzureCredential</c> get their own keys in Step 2B (#52).
    /// </summary>
    public string? ConnectionString { get; init; }

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
    /// Total attempts (initial + retries) the AWS SDK makes on transient
    /// failures. Null falls back to the backend default.
    /// </summary>
    public int? RetryMaxAttempts { get; init; }

    /// <summary>
    /// Maximum number of in-flight upload API calls. Null falls back to the
    /// backend default. Mapped to <c>max-concurrent-uploads</c> in TOML.
    /// </summary>
    public int? MaxConcurrentUploads { get; init; }

    /// <summary>
    /// Maximum number of in-flight download API calls. Null falls back to the
    /// backend default. Mapped to <c>max-concurrent-downloads</c> in TOML.
    /// </summary>
    public int? MaxConcurrentDownloads { get; init; }

    /// <summary>
    /// Maximum number of multipart parts uploaded in parallel inside a single
    /// upload. Null falls back to the backend default. Mapped to
    /// <c>max-multipart-parts</c> in TOML.
    /// </summary>
    public int? MaxMultipartParts { get; init; }

    /// <summary>
    /// When true, skip the bucket-versioning safety check and instead emit a
    /// repeated warning. Intended for CI / disposable buckets only.
    /// </summary>
    public bool? AllowUnversioned { get; init; }

    /// <summary>
    /// Returns a copy of this mount with non-null values from <paramref name="overlay"/>
    /// taking precedence per key. The <see cref="Name"/> is taken from
    /// <paramref name="overlay"/> when non-empty so a project-local override can
    /// rename a user-global mount entry.
    /// </summary>
    public OsvfsMountConfig MergeOverlay(OsvfsMountConfig overlay) => new()
    {
        Name = string.IsNullOrEmpty(overlay.Name) ? Name : overlay.Name,
        Provider = overlay.Provider ?? Provider,
        Bucket = overlay.Bucket ?? Bucket,
        RootFolder = overlay.RootFolder ?? RootFolder,
        EndpointUrl = overlay.EndpointUrl ?? EndpointUrl,
        Region = overlay.Region ?? Region,
        Prefix = overlay.Prefix ?? Prefix,
        ReadOnly = overlay.ReadOnly ?? ReadOnly,
        SyncIntervalSeconds = overlay.SyncIntervalSeconds ?? SyncIntervalSeconds,
        ChangeSource = overlay.ChangeSource ?? ChangeSource,
        SyncMode = overlay.SyncMode ?? SyncMode,
        EventQueue = overlay.EventQueue ?? EventQueue,
        AwsProfile = overlay.AwsProfile ?? AwsProfile,
        ConnectionString = overlay.ConnectionString ?? ConnectionString,
        BandwidthUp = overlay.BandwidthUp ?? BandwidthUp,
        BandwidthDown = overlay.BandwidthDown ?? BandwidthDown,
        MultipartThreshold = overlay.MultipartThreshold ?? MultipartThreshold,
        MultipartPartSize = overlay.MultipartPartSize ?? MultipartPartSize,
        RetryMaxAttempts = overlay.RetryMaxAttempts ?? RetryMaxAttempts,
        MaxConcurrentUploads = overlay.MaxConcurrentUploads ?? MaxConcurrentUploads,
        MaxConcurrentDownloads = overlay.MaxConcurrentDownloads ?? MaxConcurrentDownloads,
        MaxMultipartParts = overlay.MaxMultipartParts ?? MaxMultipartParts,
        AllowUnversioned = overlay.AllowUnversioned ?? AllowUnversioned,
    };
}
