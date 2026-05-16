using Azure.Identity;
using Microsoft.Extensions.Logging;
using OSVFS.Credentials;
using OSVFS.Net;
using OSVFS.ObjectStore;
using OSVFS.ObjectStore.AzureBlob;

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
        ILogger logger,
        ICredentialRefreshNotifier? refreshNotifier = null) =>
        Build(mount, credentialStore, DefaultSharedProfileResolver.Instance, logger, refreshNotifier);

    /// <summary>
    /// Test-friendly overload that lets callers swap out the SDK shared-profile
    /// lookup. Production code uses <see cref="DefaultSharedProfileResolver"/>.
    /// </summary>
    internal static ProjFsProviderOptions Build(
        OsvfsMountConfig mount,
        IAwsCredentialStore credentialStore,
        ISharedProfileResolver sharedProfileResolver,
        ILogger logger,
        ICredentialRefreshNotifier? refreshNotifier = null)
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

        IObjectStoreCredentialSource? credentials = (mount.Provider ?? ObjectStoreProvider.S3) switch
        {
            ObjectStoreProvider.AzureBlob => ResolveAzureCredential(mount, logger),
            _ => ResolveCredential(
                credentialStore, sharedProfileResolver, mount.AwsProfile, mount.Name, logger),
        };

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

        var multipartCapabilities = MultipartCapabilities.For(mount.Provider ?? ObjectStoreProvider.S3);
        if (MultipartSettingsValidator.Validate(
                multipartThresholdBytes, multipartPartSizeBytes, multipartCapabilities) is { } error)
        {
            throw new OsvfsConfigException($"Mount '{mount.Name}': {error}");
        }

        if (mount.RetryMaxAttempts is < 1)
        {
            throw new OsvfsConfigException(
                $"Mount '{mount.Name}': 'retry-max-attempts' must be at least 1 " +
                $"(got {mount.RetryMaxAttempts}).");
        }

        if (mount.MaxConcurrentUploads is < 1)
        {
            throw new OsvfsConfigException(
                $"Mount '{mount.Name}': 'max-concurrent-uploads' must be at least 1 " +
                $"(got {mount.MaxConcurrentUploads}).");
        }

        if (mount.MaxConcurrentDownloads is < 1)
        {
            throw new OsvfsConfigException(
                $"Mount '{mount.Name}': 'max-concurrent-downloads' must be at least 1 " +
                $"(got {mount.MaxConcurrentDownloads}).");
        }

        if (mount.MaxMultipartParts is < 1)
        {
            throw new OsvfsConfigException(
                $"Mount '{mount.Name}': 'max-multipart-parts' must be at least 1 " +
                $"(got {mount.MaxMultipartParts}).");
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
            MaxConcurrentUploads = mount.MaxConcurrentUploads,
            MaxConcurrentDownloads = mount.MaxConcurrentDownloads,
            MaxMultipartParts = mount.MaxMultipartParts,
            AllowUnversioned = mount.AllowUnversioned ?? false,
            RefreshNotifier = refreshNotifier,
        };
    }

    /// <summary>
    /// Resolves an <c>aws-profile</c> name in two steps:
    /// 1. The OSVFS DPAPI store (encrypted static credentials managed by
    ///    <c>osvfs credentials set</c>).
    /// 2. The shared AWS profile store via <paramref name="sharedProfileResolver"/>
    ///    (covers static keys in <c>~/.aws/credentials</c>, <c>credential_process</c>
    ///    profiles produced by <c>aws login</c>, <c>sso_session</c> profiles
    ///    written by <c>aws configure sso</c>, and assume-role chains). The SDK
    ///    handles refresh for these natively.
    /// Both misses are reported as a single <see cref="OsvfsConfigException"/> so
    /// multi-mount runs surface which entry blew up.
    /// </summary>
    /// <summary>
    /// Resolves the Azure Blob credential source from the mount config. Picks
    /// exactly one of the four supported branches:
    /// <list type="bullet">
    ///   <item><c>connection-string</c></item>
    ///   <item><c>account-name</c> + <c>sas</c></item>
    ///   <item><c>account-name</c> + <c>managed-identity = true</c></item>
    ///   <item><c>account-name</c> + <c>default-azure-credential = true</c></item>
    /// </list>
    /// Throws <see cref="OsvfsConfigException"/> when none of the branches
    /// match or when more than one is present, so the operator gets a clear
    /// startup error instead of an opaque downstream Azure SDK failure.
    /// </summary>
    private static AzureCredentialSource? ResolveAzureCredential(
        OsvfsMountConfig mount, ILogger logger)
    {
        var hasConnectionString = !string.IsNullOrEmpty(mount.ConnectionString);
        var hasSas = !string.IsNullOrEmpty(mount.Sas);
        var hasManagedIdentity = mount.ManagedIdentity == true;
        var hasDefaultAzureCredential = mount.DefaultAzureCredential == true;

        var configured =
            (hasConnectionString ? 1 : 0) +
            (hasSas ? 1 : 0) +
            (hasManagedIdentity ? 1 : 0) +
            (hasDefaultAzureCredential ? 1 : 0);

        if (configured == 0)
        {
            throw new OsvfsConfigException(
                $"Mount '{mount.Name}': Azure Blob backend requires exactly one of " +
                "'connection-string', 'sas', 'managed-identity', or 'default-azure-credential' " +
                "in osvfs.toml.");
        }
        if (configured > 1)
        {
            throw new OsvfsConfigException(
                $"Mount '{mount.Name}': Azure Blob backend accepts exactly one credential branch — " +
                "only one of 'connection-string', 'sas', 'managed-identity', or 'default-azure-credential' " +
                "may be set per mount.");
        }

        // Connection string carries its own account name + endpoint.
        if (hasConnectionString)
        {
            logger.LogInformation(
                "Mount '{Mount}': using Azure credentials from connection string.", mount.Name);
            return AzureCredentialSource.FromConnectionString(
                mount.ConnectionString!, "Azure connection string");
        }

        // The remaining three branches need an explicit account name to build
        // the service endpoint URL.
        if (string.IsNullOrEmpty(mount.AccountName))
        {
            throw new OsvfsConfigException(
                $"Mount '{mount.Name}': Azure Blob backend requires 'account-name' alongside " +
                "'sas' / 'managed-identity' / 'default-azure-credential'.");
        }

        if (hasSas)
        {
            var description = $"Azure SAS for '{mount.AccountName}'";
            logger.LogInformation(
                "Mount '{Mount}': using Azure credentials from {Source}.", mount.Name, description);
            return AzureCredentialSource.FromSas(mount.AccountName, mount.Sas!, description);
        }

        if (hasManagedIdentity)
        {
            var description = $"Azure Managed Identity for '{mount.AccountName}'";
            logger.LogInformation(
                "Mount '{Mount}': using Azure credentials from {Source}.", mount.Name, description);
            // Azure.Identity 1.21 deprecated the legacy parameterless / clientId
            // constructors in favour of the typed ManagedIdentityId factories.
            // Pass SystemAssigned explicitly — it matches the previous default
            // behaviour without tripping the CS0618 obsolete warning.
            return AzureCredentialSource.FromTokenCredential(
                mount.AccountName,
                new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned),
                description);
        }

        // hasDefaultAzureCredential
        {
            var description = $"Azure DefaultAzureCredential chain for '{mount.AccountName}'";
            logger.LogInformation(
                "Mount '{Mount}': using Azure credentials from {Source}.", mount.Name, description);
            return AzureCredentialSource.FromTokenCredential(
                mount.AccountName, new DefaultAzureCredential(), description);
        }
    }

    private static AwsCredentialSource? ResolveCredential(
        IAwsCredentialStore store,
        ISharedProfileResolver sharedProfileResolver,
        string? profileName,
        string mountName,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(profileName)) return null;

        var stored = store.Load(profileName);
        if (stored is not null)
        {
            var description = $"OSVFS profile '{profileName}'";
            logger.LogInformation(
                "Mount '{Mount}': using AWS credentials from {Source}.", mountName, description);
            return AwsCredentialSource.FromStatic(stored, description);
        }

        var shared = sharedProfileResolver.Resolve(profileName);
        if (shared is not null)
        {
            logger.LogInformation(
                "Mount '{Mount}': using AWS credentials from {Source}.", mountName, shared.Description);
            return AwsCredentialSource.FromSdk(shared.Credentials, shared.Description);
        }

        throw new OsvfsConfigException(
            $"Mount '{mountName}': AWS profile '{profileName}' was not found in the OSVFS " +
            "credential store or in the shared AWS profile store (~/.aws/config, " +
            "~/.aws/credentials). Run 'osvfs credentials set --profile <name>' to create an " +
            "OSVFS-managed entry, or configure the profile in the shared AWS files (e.g. via " +
            "'aws configure sso --profile <name>' for IAM Identity Center, or " +
            "'aws login --profile <name>' for AWS CLI 2.32+).");
    }
}
