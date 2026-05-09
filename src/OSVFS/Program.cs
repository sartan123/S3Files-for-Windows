using Microsoft.Extensions.Logging;
using System.CommandLine;
using OSVFS;
using OSVFS.Configuration;
using OSVFS.Credentials;
using OSVFS.Logging;
using OSVFS.Net;
using OSVFS.ObjectStore;
using OSVFS.ProjFs;

const int ExitSuccess = 0;
const int ExitGeneralException = 2;

var providerOption = new Option<ObjectStoreProvider?>("--provider")
{
    Description = "Object-store provider backing the virtualization root. Currently only 's3' is fully implemented; 'gcs' and 'azureblob' fail at startup.",
};

var bucketOption = new Option<string?>("--bucket")
{
    Description = "Bucket (S3/GCS) or container (Azure) that will be accessible through the file system. Required, but may also be supplied via osvfs.toml.",
};

var rootFolderOption = new Option<string?>("--root-folder")
{
    Description = "Path to the virtualization root. Required, but may also be supplied via osvfs.toml.",
};

var endpointUrlOption = new Option<string?>("--endpoint-url")
{
    Description = "Override command's default URL with the given URL.",
};

var regionOption = new Option<string?>("--region")
{
    Description = "AWS region (e.g. us-east-1, ap-northeast-1). When omitted, the SDK falls back to the standard credential/region resolution chain (env vars, profile, IMDS).",
};

var prefixOption = new Option<string?>("--prefix")
{
    Description = "Optional key prefix within the bucket. When set, only objects under this prefix are projected into the virtualization root.",
};

var verboseOption = new Option<bool>("--verbose")
{
    Description = "Use verbose log level.",
};

var readOnlyOption = new Option<bool>("--read-only")
{
    Description = "Read-only mode.",
    Hidden = true,
};

var syncIntervalOption = new Option<int?>("--sync-interval-seconds")
{
    Description = "Polling interval (seconds) for detecting external S3 changes. 0 disables.",
};

var awsProfileOption = new Option<string?>("--aws-profile")
{
    Description = "Use credentials previously saved by 'osvfs credentials set --profile <name>' (encrypted with DPAPI in Windows Credential Manager).",
};

var bandwidthUpOption = new Option<string?>("--bandwidth-up")
{
    Description = "Upload bandwidth ceiling. Plain bytes/s by default; suffixes K/M/G mean KiB/s, MiB/s, GiB/s (e.g. '5M' = 5 MiB/s). Omit or set to 0 to disable.",
};

var bandwidthDownOption = new Option<string?>("--bandwidth-down")
{
    Description = "Download bandwidth ceiling. Plain bytes/s by default; suffixes K/M/G mean KiB/s, MiB/s, GiB/s (e.g. '10M' = 10 MiB/s). Omit or set to 0 to disable.",
};

var multipartThresholdOption = new Option<string?>("--multipart-threshold")
{
    Description = "Stream size at or above which uploads are routed through the multipart path. Same K/M/G suffixes as --bandwidth-up. Defaults to 8M.",
};

var multipartPartSizeOption = new Option<string?>("--multipart-part-size")
{
    Description = "Per-part size used by multipart uploads. Must be between 5M and 5G; the last part may be smaller. Defaults to 5M.",
};

var logFormatOption = new Option<LogFormat?>("--log-format")
{
    Description = "Console log output format. 'text' (default) writes single-line human-readable output; 'json' writes one JSON object per line with UTC timestamps for log shippers (Datadog, Loki, etc.).",
};

var allowUnversionedOption = new Option<bool>("--allow-unversioned")
{
    Description = "DANGER: Skip the bucket-versioning safety check and run against a bucket without versioning. Local edits and deletes become unrecoverable. Intended for CI / disposable buckets only.",
};

var credentialStore = new WindowsCredentialStore();

var rootCommand = new RootCommand("OSVFS — Object Storage Virtual File System for Windows: mount a cloud object-store bucket/container as a local folder via ProjFS.")
{
    providerOption,
    bucketOption,
    rootFolderOption,
    endpointUrlOption,
    regionOption,
    prefixOption,
    verboseOption,
    readOnlyOption,
    syncIntervalOption,
    awsProfileOption,
    bandwidthUpOption,
    bandwidthDownOption,
    multipartThresholdOption,
    multipartPartSizeOption,
    logFormatOption,
    allowUnversionedOption,
};

rootCommand.Subcommands.Add(CredentialsCommandFactory.Build(credentialStore));

rootCommand.SetAction(parseResult =>
{
    OsvfsConfigFile? fileConfig;
    try
    {
        fileConfig = OsvfsConfigFileLoader.LoadFromDefaultLocations();
    }
    catch (OsvfsConfigException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return ExitGeneralException;
    }

    var verbose = GetCliBool(parseResult, verboseOption) ?? fileConfig?.Verbose ?? false;
    var logFormat = parseResult.GetValue(logFormatOption) ?? fileConfig?.LogFormat ?? LogFormat.Text;

    using var loggerFactory = LogConsoleFactory.Create(verbose, logFormat);

    var logger = loggerFactory.CreateLogger("OSVFS");

    var bucket = parseResult.GetValue(bucketOption) ?? fileConfig?.Bucket;
    if (string.IsNullOrEmpty(bucket))
    {
        logger.LogError(
            "--bucket is required. Pass it on the command line or set 'bucket' in osvfs.toml / %APPDATA%/OSVFS/config.toml.");
        return ExitGeneralException;
    }

    var rootFolder = parseResult.GetValue(rootFolderOption) ?? fileConfig?.RootFolder;
    if (string.IsNullOrEmpty(rootFolder))
    {
        logger.LogError(
            "--root-folder is required. Pass it on the command line or set 'root-folder' in osvfs.toml / %APPDATA%/OSVFS/config.toml.");
        return ExitGeneralException;
    }

    var profileName = parseResult.GetValue(awsProfileOption) ?? fileConfig?.AwsProfile;
    AwsCredential? credentials;
    try
    {
        credentials = ResolveCredential(credentialStore, profileName, logger);
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Failed to load AWS profile '{Profile}'", profileName);
        return ExitGeneralException;
    }

    BandwidthLimits bandwidthLimits;
    long? multipartThresholdBytes;
    long? multipartPartSizeBytes;
    try
    {
        bandwidthLimits = new BandwidthLimits(
            UpBytesPerSecond: BandwidthSize.Parse(
                parseResult.GetValue(bandwidthUpOption) ?? fileConfig?.BandwidthUp),
            DownBytesPerSecond: BandwidthSize.Parse(
                parseResult.GetValue(bandwidthDownOption) ?? fileConfig?.BandwidthDown));

        multipartThresholdBytes = BandwidthSize.Parse(
            parseResult.GetValue(multipartThresholdOption) ?? fileConfig?.MultipartThreshold);
        multipartPartSizeBytes = BandwidthSize.Parse(
            parseResult.GetValue(multipartPartSizeOption) ?? fileConfig?.MultipartPartSize);
    }
    catch (FormatException ex)
    {
        logger.LogError("{Message}", ex.Message);
        return ExitGeneralException;
    }

    if (MultipartSettingsValidator.Validate(multipartThresholdBytes, multipartPartSizeBytes) is { } error)
    {
        logger.LogError("{Message}", error);
        return ExitGeneralException;
    }

    var options = new ProjFsProviderOptions
    {
        Provider = parseResult.GetValue(providerOption) ?? fileConfig?.Provider ?? ObjectStoreProvider.S3,
        Bucket = bucket,
        VirtRoot = rootFolder,
        EndpointUrl = parseResult.GetValue(endpointUrlOption) ?? fileConfig?.EndpointUrl,
        Region = parseResult.GetValue(regionOption) ?? fileConfig?.Region,
        KeyPrefix = parseResult.GetValue(prefixOption) ?? fileConfig?.Prefix,
        Verbose = verbose,
        ReadOnly = GetCliBool(parseResult, readOnlyOption) ?? fileConfig?.ReadOnly ?? false,
        SyncIntervalSeconds = parseResult.GetValue(syncIntervalOption) ?? fileConfig?.SyncIntervalSeconds ?? 30,
        Credentials = credentials,
        BandwidthLimits = bandwidthLimits,
        MultipartThresholdBytes = multipartThresholdBytes,
        MultipartPartSizeBytes = multipartPartSizeBytes,
        AllowUnversioned = GetCliBool(parseResult, allowUnversionedOption) ?? fileConfig?.AllowUnversioned ?? false,
    };

    try
    {
        return RunProvider(options, loggerFactory, logger);
    }
    catch (BucketVersioningNotEnabledException ex)
    {
        // The exception's message is the user-facing remediation banner — log it as a
        // single error so the copy-paste fix command lands intact in the terminal,
        // without the stack trace chrome a generic critical log would add.
        logger.LogError("{Message}", ex.Message);
        return ExitGeneralException;
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "fatal");
        return ExitGeneralException;
    }
});

return rootCommand.Parse(args).Invoke();

// Returns the boolean value of a flag-style option, but only when the user
// explicitly passed it; returns null when the option was defaulted, so callers
// can fall through to the TOML config or built-in default.
static bool? GetCliBool(ParseResult parseResult, Option<bool> option)
{
    var result = parseResult.GetResult(option);
    if (result is null || result.Implicit) return null;
    return parseResult.GetValue(option);
}

// Resolves an --aws-profile name against the credential store. Returns null when no
// profile is requested; throws when the requested profile is missing.
static AwsCredential? ResolveCredential(IAwsCredentialStore store, string? profileName, ILogger logger)
{
    if (string.IsNullOrEmpty(profileName)) return null;
    var credential = store.Load(profileName)
        ?? throw new InvalidOperationException(
            $"AWS profile '{profileName}' was not found in the OSVFS credential store. " +
            "Run 'osvfs credentials set --profile <name>' to create it.");
    logger.LogInformation("Using AWS credentials from profile '{Profile}'.", profileName);
    return credential;
}

// Constructs the provider, starts virtualization, and blocks on stdin until the user exits.
static int RunProvider(ProjFsProviderOptions options, ILoggerFactory loggerFactory, ILogger logger)
{
    using var provider = new ProjFsProvider(
        options, loggerFactory.CreateLogger<ProjFsProvider>(), loggerFactory);
    if (!provider.StartVirtualization())
    {
        logger.LogError("Failed to start provider.");
        return ExitGeneralException;
    }

    logger.LogInformation("Virtualizing s3://{Bucket} at {Root}", options.Bucket, options.VirtRoot);
    logger.LogInformation("Press Enter to exit.");
    Console.ReadLine();
    return ExitSuccess;
}
