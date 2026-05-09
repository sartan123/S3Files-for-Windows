namespace OSVFS.Configuration;

/// <summary>
/// Console log output format selected via <c>--log-format</c>.
/// </summary>
internal enum LogFormat
{
    /// <summary>
    /// Human-readable single-line console output (the default).
    /// </summary>
    Text,

    /// <summary>
    /// Structured JSON, one line per log entry, intended for log shippers
    /// such as Datadog or Loki.
    /// </summary>
    Json,
}
