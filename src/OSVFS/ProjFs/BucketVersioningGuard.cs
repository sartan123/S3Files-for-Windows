using Microsoft.Extensions.Logging;
using OSVFS.ObjectStore;

namespace OSVFS.ProjFs;

/// <summary>
/// Pure validation helper for the bucket-versioning safety check. Lives apart
/// from <see cref="ProjFsProvider"/> so unit tests can exercise the decision
/// table without touching ProjFS.
/// </summary>
internal static class BucketVersioningGuard
{
    /// <summary>
    /// Inspects <paramref name="status"/> and either: returns silently when
    /// versioning is enabled; logs a warning when versioning is disabled but the
    /// operator opted in via <paramref name="allowUnversioned"/>; or throws
    /// <see cref="BucketVersioningNotEnabledException"/> otherwise.
    /// </summary>
    public static void Validate(
        BucketVersioningStatus status,
        string bucket,
        bool allowUnversioned,
        ILogger logger)
    {
        if (status == BucketVersioningStatus.Enabled)
        {
            logger.LogInformation("Bucket versioning is enabled on {Bucket}.", bucket);
            return;
        }

        if (!allowUnversioned)
        {
            throw new BucketVersioningNotEnabledException(bucket, status);
        }

        // The operator has chosen to bypass the safety check; surface the danger
        // loudly at startup. The repeating nag is driven separately by the host.
        logger.LogWarning(
            "DANGER: Bucket versioning is {Status} on {Bucket}. Running with --allow-unversioned: " +
            "local edits and deletes are NOT recoverable.",
            status, bucket);
    }
}
