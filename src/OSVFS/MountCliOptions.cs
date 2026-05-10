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
    /// Path to an OSVFS config TOML file. When supplied, its keys overlay the
    /// exe-adjacent <c>osvfs.toml</c> and the user-global
    /// <c>%APPDATA%\OSVFS\config.toml</c>; the file must exist or startup
    /// fails. Useful for switching between several mount profiles without
    /// editing the standard locations.
    /// </summary>
    public Option<string?> ConfigPath { get; } = new("--config")
    {
        Description = "Path to an osvfs.toml file. Highest-priority config source: its keys override the user-global %APPDATA%\\OSVFS\\config.toml and the exe-adjacent osvfs.toml. The file must exist when the flag is supplied.",
    };

    /// <summary>
    /// One-shot OTLP exporter destination. When supplied, the host
    /// builds a TracerProvider / MeterProvider against this endpoint and
    /// emits OSVFS spans + metrics to it. Overrides the
    /// <c>[telemetry] otlp-endpoint</c> key in osvfs.toml when both are
    /// present. Empty string is rejected; pass nothing to leave
    /// telemetry off.
    /// </summary>
    public Option<string?> OtlpEndpoint { get; } = new("--otlp-endpoint")
    {
        Description =
            "OTLP exporter endpoint (e.g. http://localhost:4317 for gRPC, http://localhost:4318 " +
            "for HTTP/Protobuf). When set, OSVFS emits spans and metrics from the 'osvfs.s3' " +
            "source to this endpoint. Overrides 'otlp-endpoint' in [telemetry] when supplied.",
    };

    /// <summary>
    /// Registers every option on <paramref name="command"/>. Used by the root
    /// command and each mount subcommand so the flags are uniformly available.
    /// </summary>
    public void AddTo(Command command)
    {
        command.Options.Add(Verbose);
        command.Options.Add(LogFormat);
        command.Options.Add(ConfigPath);
        command.Options.Add(OtlpEndpoint);
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
