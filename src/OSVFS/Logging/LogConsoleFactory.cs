using Microsoft.Extensions.Logging;
using OSVFS.Configuration;
using System.Text.Json;

namespace OSVFS.Logging;

/// <summary>
/// Builds the process-wide <see cref="ILoggerFactory"/> for the OSVFS host,
/// honoring <c>--verbose</c> and <c>--log-format</c>. Extracted from
/// <c>Program.cs</c> so tests can exercise the formatter selection without
/// going through <c>System.CommandLine</c>.
/// </summary>
internal static class LogConsoleFactory
{
    /// <summary>
    /// Creates a console logger factory configured for the requested verbosity and format.
    /// <see cref="LogFormat.Text"/> keeps the legacy single-line console formatter;
    /// <see cref="LogFormat.Json"/> emits one UTF-8 JSON object per line, with UTC
    /// timestamps suitable for ingestion by log shippers (Datadog, Loki, ...).
    /// </summary>
    public static ILoggerFactory Create(bool verbose, LogFormat logFormat)
        => LoggerFactory.Create(builder => Configure(builder, verbose, logFormat));

    /// <summary>
    /// Applies the OSVFS console formatter selection to an existing
    /// <see cref="ILoggingBuilder"/>. Exposed for tests that need to inject
    /// their own <see cref="System.IO.TextWriter"/>.
    /// </summary>
    public static void Configure(ILoggingBuilder builder, bool verbose, LogFormat logFormat)
    {
        builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        switch (logFormat)
        {
            case LogFormat.Json:
                builder.AddJsonConsole(o =>
                {
                    o.IncludeScopes = false;
                    o.UseUtcTimestamp = true;
                    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
                    o.JsonWriterOptions = new JsonWriterOptions
                    {
                        Indented = false,
                    };
                });
                break;
            case LogFormat.Text:
            default:
                builder.AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss ";
                });
                break;
        }
    }
}
