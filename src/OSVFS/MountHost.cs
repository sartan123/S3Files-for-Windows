using Microsoft.Extensions.Logging;
using OSVFS.ProjFs;

namespace OSVFS;

/// <summary>
/// Hosts one or more <see cref="ProjFsProvider"/> instances inside a single
/// process. Centralizes the start / wait-for-stdin / dispose lifecycle so
/// the root command, <c>mount</c>, and <c>mount-all</c> entry points all run
/// the same shutdown semantics.
/// </summary>
internal static class MountHost
{
    /// <summary>
    /// Exit code used when at least one provider failed to start.
    /// </summary>
    public const int ExitGeneralException = 2;

    /// <summary>
    /// Constructs and starts a provider for each entry in
    /// <paramref name="mounts"/>, then blocks on stdin until the operator
    /// presses Enter. Providers are disposed in reverse-start order so the
    /// last-started mount is torn down first. Returns 0 on clean shutdown
    /// or <see cref="ExitGeneralException"/> when any provider failed to
    /// start (already-running mounts are still disposed cleanly before
    /// returning).
    /// </summary>
    public static int Run(
        IReadOnlyList<MountInvocation> mounts,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        var providers = new List<(string Name, ProjFsProvider Provider)>(mounts.Count);
        var failed = false;
        try
        {
            foreach (var mount in mounts)
            {
                // Use a per-mount logger category so text/JSON formatters surface
                // which mount each line came from. The category is also threaded
                // into the backend so SDK-level log entries inherit it.
                var mountLogger = loggerFactory.CreateLogger($"OSVFS.Mount.{mount.Name}");
                var provider = new ProjFsProvider(
                    mount.Options,
                    loggerFactory.CreateLogger<ProjFsProvider>(),
                    loggerFactory);
                providers.Add((mount.Name, provider));
                if (!provider.StartVirtualization())
                {
                    mountLogger.LogError(
                        "Mount '{Mount}': failed to start virtualization at {Root}.",
                        mount.Name, mount.Options.VirtRoot);
                    failed = true;
                    continue;
                }
                mountLogger.LogInformation(
                    "Mount '{Mount}': virtualizing s3://{Bucket} at {Root}",
                    mount.Name, mount.Options.Bucket, mount.Options.VirtRoot);
            }

            if (failed)
            {
                logger.LogError("One or more mounts failed to start; aborting.");
                return ExitGeneralException;
            }

            // Two log statements rather than a dynamic format string so the
            // analyzer can statically validate the template against its argument
            // count (CA2254).
            if (providers.Count == 1)
            {
                logger.LogInformation("Press Enter to exit.");
            }
            else
            {
                logger.LogInformation(
                    "All {Count} mount(s) started. Press Enter to exit.", providers.Count);
            }
            Console.ReadLine();
            return 0;
        }
        finally
        {
            // Reverse iteration: the last provider started is the first one
            // disposed, mirroring "stack-allocated" lifetime semantics. Each
            // dispose is wrapped in try/catch so a single noisy mount cannot
            // strand later (earlier-started) mounts.
            for (var i = providers.Count - 1; i >= 0; i--)
            {
                try
                {
                    providers[i].Provider.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex, "Mount '{Mount}': error disposing provider", providers[i].Name);
                }
            }
        }
    }

    /// <summary>
    /// Pairing of a mount's name with its fully-resolved options. Threaded
    /// through <see cref="Run"/> so each provider can be addressed in logs by
    /// its declared name independently of the bucket/root values.
    /// </summary>
    public readonly record struct MountInvocation(string Name, ProjFsProviderOptions Options);
}
