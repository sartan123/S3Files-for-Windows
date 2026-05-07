using Microsoft.Extensions.Logging;
using System.CommandLine;
using S3Files.Windows;
using S3Files.Windows.ObjectStore;
using S3Files.Windows.ProjFs;

const int ExitSuccess = 0;
const int ExitGeneralException = 2;

var providerOption = new Option<ObjectStoreProvider>("--provider")
{
    Description = "Object-store provider backing the virtualization root. Currently only 's3' is fully implemented; 'gcs' and 'azureblob' fail at startup.",
    DefaultValueFactory = _ => ObjectStoreProvider.S3,
};

var bucketOption = new Option<string>("--bucket")
{
    Description = "Bucket (S3/GCS) or container (Azure) that will be accessible through the file system.",
    Required = true,
};

var rootFolderOption = new Option<string>("--root-folder")
{
    Description = "Path to the virtualization root.",
    Required = true,
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

var syncIntervalOption = new Option<int>("--sync-interval-seconds")
{
    Description = "Polling interval (seconds) for detecting external S3 changes. 0 disables.",
    DefaultValueFactory = _ => 30,
};

var rootCommand = new RootCommand("Windows port of AWS S3 Files: mount a cloud object-store bucket/container as a local folder via ProjFS.")
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
};

rootCommand.SetAction(parseResult =>
{
    var options = new ProjFsProviderOptions
    {
        Provider = parseResult.GetValue(providerOption),
        Bucket = parseResult.GetValue(bucketOption)!,
        VirtRoot = parseResult.GetValue(rootFolderOption)!,
        EndpointUrl = parseResult.GetValue(endpointUrlOption),
        Region = parseResult.GetValue(regionOption),
        KeyPrefix = parseResult.GetValue(prefixOption),
        Verbose = parseResult.GetValue(verboseOption),
        ReadOnly = parseResult.GetValue(readOnlyOption),
        SyncIntervalSeconds = parseResult.GetValue(syncIntervalOption),
    };

    using var loggerFactory = LoggerFactory.Create(builder => builder
        .SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information)
        .AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        }));

    var logger = loggerFactory.CreateLogger("S3Files.Windows");

    try
    {
        return RunProvider(options, loggerFactory, logger);
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "fatal");
        return ExitGeneralException;
    }
});

return rootCommand.Parse(args).Invoke();

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
