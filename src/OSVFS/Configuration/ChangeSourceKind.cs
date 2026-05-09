namespace OSVFS.Configuration;

/// <summary>
/// Selects which change-detection strategy <c>ObjectStoreChangeWatcher</c> uses.
/// </summary>
internal enum ChangeSourceKind
{
    /// <summary>
    /// Re-list the bucket every <c>--sync-interval-seconds</c> and diff against an
    /// in-memory snapshot. Default; needs no AWS-side configuration.
    /// </summary>
    Polling,

    /// <summary>
    /// Long-poll an SQS queue carrying EventBridge S3 notifications. Requires the
    /// queue to be configured server-side via <c>--event-queue</c>.
    /// </summary>
    Events,
}
