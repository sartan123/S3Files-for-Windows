using Microsoft.Extensions.Logging;
using System.CommandLine;
using S3Files.Windows;
using S3Files.Windows.ProjFs;

const int ExitSuccess = 0;
const int ExitGeneralException = 2;

var bucketOption = new Option<string>("--bucket")
{
    Description = "S3 bucket that will be accessible through the file system.",
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

var rootCommand = new RootCommand("Windows port of AWS S3 Files: mount an Amazon S3 bucket as a local folder via ProjFS.")
{
    bucketOption,
    rootFolderOption,
    endpointUrlOption,
    verboseOption,
    readOnlyOption,
    syncIntervalOption,
};

rootCommand.SetAction(parseResult =>
{
    var options = new ProjFsProviderOptions
    {
        S3Bucket = parseResult.GetValue(bucketOption)!,
        VirtRoot = parseResult.GetValue(rootFolderOption)!,
        EndpointUrl = parseResult.GetValue(endpointUrlOption),
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

static int RunProvider(ProjFsProviderOptions options, ILoggerFactory loggerFactory, ILogger logger)
{
    using var provider = new ProjFsProvider(
        options, loggerFactory.CreateLogger<ProjFsProvider>(), loggerFactory);
    if (!provider.StartVirtualization())
    {
        logger.LogError("Failed to start provider.");
        return ExitGeneralException;
    }

    logger.LogInformation("Virtualizing s3://{Bucket} at {Root}", options.S3Bucket, options.VirtRoot);
    logger.LogInformation("Press Enter to exit.");
    Console.ReadLine();
    return ExitSuccess;
}
