using Microsoft.Extensions.Logging;
using OSVFS.Credentials;
using OSVFS.Net;
using OSVFS.ObjectStore;

namespace OSVFS.Configuration;

/// <summary>
/// Materializes a fully-populated <see cref="ProjFsProviderOptions"/> from a
/// parsed <see cref="OsvfsMountConfig"/>, applying defaults and resolving
/// referenced credentials. All per-mount settings are sourced from the config
/// file: the CLI surface no longer accepts mount-level overrides, so the
/// config is the single source of truth.
/// </summary>
internal static class MountOptionsBuilder
{
    /// <summary>
    /// Builds and validates the runtime options for a single mount. Throws
    /// <see cref="OsvfsConfigException"/> when a required field
    /// (<c>bucket</c> / <c>root-folder</c>) is missing, when a referenced AWS
    /// profile cannot be resolved, or when bandwidth / multipart / retry
    /// values fail their bounds checks. The supplied <paramref name="logger"/>
    /// is used only for the credential-store "Using AWS credentials from
    /// profile X" notice.
    /// </summary>
    public static ProjFsProviderOptions Build(
        OsvfsMountConfig mount,
        IAwsCredentialStore credentialStore,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(mount.Bucket))
        {
            throw new OsvfsConfigException(
                $"Mount '{mount.Name}': 'bucket' is required. Set 'bucket' inside the [[mount]] " +
                "table (or at the document root for the legacy single-mount form).");
        }

        if (string.IsNullOrEmpty(mount.RootFolder))
        {
            throw new OsvfsConfigException(
                $"Mount '{mount.Name}': 'root-folder' is required. Set 'root-folder' inside the " +
                "[[mount]] table.");
        }

        var credentials = ResolveCredential(credentialStore, mount.AwsProfile, mount.Name, logger);

        BandwidthLimits bandwidthLimits;
        long? multipartThresholdBytes;
        long? multipartPartSizeBytes;
        try
        {
            bandwidthLimits = new BandwidthLimits(
                UpBytesPerSecond: BandwidthSize.Parse(mount.BandwidthUp),
                DownBytesPerSecond: BandwidthSize.Parse(mount.BandwidthDown));
            multipartThresholdBytes = BandwidthSize.Parse(mount.MultipartThreshold);
            multipartPartSizeBytes = BandwidthSize.Parse(mount.MultipartPartSize);
        }
        catch (FormatException ex)
        {
            throw new OsvfsConfigException($"Mount '{mount.Name}': {ex.Message}", ex);
        }

        if (MultipartSettingsValidator.Validate(multipartThresholdBytes, multipartPartSizeBytes) is { } error)
        {
            throw new OsvfsConfigException($"Mount '{mount.Name}': {error}");
        }

        if (mount.RetryMaxAttempts is < 1)
        {
            throw new OsvfsConfigException(
                $"Mount '{mount.Name}': 'retry-max-attempts' must be at least 1 " +
                $"(got {mount.RetryMaxAttempts}).");
        }

        var changeSource = mount.ChangeSource ?? ChangeSourceKind.Polling;
        if (changeSource is ChangeSourceKind.Events && string.IsNullOrEmpty(mount.EventQueue))
        {
            throw new OsvfsConfigException(
                $"Mount '{mount.Name}': change-source 'events' requires 'event-queue' " +
                "(an SQS queue URL or name). See README for the necessary S3 → EventBridge → SQS setup.");
        }

        return new ProjFsProviderOptions
        {
            Provider = mount.Provider ?? ObjectStoreProvider.S3,
            Bucket = mount.Bucket,
            VirtRoot = mount.RootFolder,
            EndpointUrl = mount.EndpointUrl,
            Region = mount.Region,
            KeyPrefix = mount.Prefix,
            // Verbose is process-level, not per-mount; the CLI host sets it via the
            // top-level options before instantiating the provider.
            Verbose = false,
            ReadOnly = mount.ReadOnly ?? false,
            SyncIntervalSeconds = mount.SyncIntervalSeconds ?? 30,
            ChangeSource = changeSource,
            SyncMode = mount.SyncMode ?? SyncMode.OnDemand,
            EventQueue = mount.EventQueue,
            Credentials = credentials,
            BandwidthLimits = bandwidthLimits,
            MultipartThresholdBytes = multipartThresholdBytes,
            MultipartPartSizeBytes = multipartPartSizeBytes,
            RetryMaxAttempts = mount.RetryMaxAttempts,
            AllowUnversioned = mount.AllowUnversioned ?? false,
        };
    }

    /// <summary>
    /// Resolves an <c>aws-profile</c> name against the credential store. The
    /// resolution failure is rewrapped as <see cref="OsvfsConfigException"/>
    /// with the mount name in the message so multi-mount runs surface which
    /// entry blew up.
    /// </summary>
    private static AwsCredential? ResolveCredential(
        IAwsCredentialStore store, string? profileName, string mountName, ILogger logger)
    {
        if (string.IsNullOrEmpty(profileName)) return null;
        var credential = store.Load(profileName)
            ?? throw new OsvfsConfigException(
                $"Mount '{mountName}': AWS profile '{profileName}' was not found in the OSVFS " +
                "credential store. Run 'osvfs credentials set --profile <name>' to create it.");
        logger.LogInformation(
            "Mount '{Mount}': using AWS credentials from profile '{Profile}'.", mountName, profileName);
        return credential;
    }
}
