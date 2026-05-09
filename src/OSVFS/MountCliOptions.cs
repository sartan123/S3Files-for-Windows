using OSVFS.Configuration;
using System.CommandLine;

namespace OSVFS;

/// <summary>
/// Process-level CLI flags shared between the root command, <c>mount</c>, and
/// <c>mount-all</c>. Per-mount settings (bucket, region, bandwidth limits, …)
/// are configured exclusively through <c>osvfs.toml</c> /
/// <c>%APPDATA%\OSVFS\config.toml</c>; only these few flags survive on the
/// command line because they are useful as one-off overrides during
/// interactive debugging.
/// </summary>
internal sealed class MountCliOptions
{
    /// <summary>
    /// Raises log verbosity to <c>Debug</c>. Useful for ad-hoc troubleshooting
    /// without editing the config file.
    /// </summary>
    public Option<bool> Verbose { get; } = new("--verbose")
    {
        Description = "Use verbose log level. Overrides 'verbose' in osvfs.toml when supplied.",
    };

    /// <summary>
    /// Selects the console log formatter. Keeps the same enum surface as the
    /// TOML <c>log-format</c> key; the CLI value wins when both are present.
    /// </summary>
    public Option<LogFormat?> LogFormat { get; } = new("--log-format")
    {
        Description = "Console log output format. 'text' (default) writes single-line human-readable output; 'json' writes one JSON object per line with UTC timestamps for log shippers (Datadog, Loki, etc.). Overrides 'log-format' in osvfs.toml when supplied.",
    };

    /// <summary>
    /// Registers every option on <paramref name="command"/>. Used by the root
    /// command and each mount subcommand so the flags are uniformly available.
    /// </summary>
    public void AddTo(Command command)
    {
        command.Options.Add(Verbose);
        command.Options.Add(LogFormat);
    }

    /// <summary>
    /// Returns the <c>--verbose</c> value only when the operator passed it
    /// explicitly; default-supplied values are reported as null so the caller
    /// can fall through to the TOML config.
    /// </summary>
    public bool? GetVerbose(ParseResult parseResult)
    {
        var result = parseResult.GetResult(Verbose);
        if (result is null || result.Implicit) return null;
        return parseResult.GetValue(Verbose);
    }
}
