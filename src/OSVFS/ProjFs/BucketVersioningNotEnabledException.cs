using OSVFS.ObjectStore;

namespace OSVFS.ProjFs;

/// <summary>
/// Thrown when the configured bucket has versioning disabled (or suspended) and
/// the operator has not opted out via <c>--allow-unversioned</c>. Carries a
/// human-readable, copy-pasteable remediation message so the operator can fix
/// the bucket without leaving the terminal.
/// </summary>
internal sealed class BucketVersioningNotEnabledException : Exception
{
    /// <summary>
    /// Bucket name the failing safety check ran against.
    /// </summary>
    public string Bucket { get; }

    /// <summary>
    /// Versioning status the backend reported (typically <see cref="BucketVersioningStatus.NotEnabled"/>).
    /// </summary>
    public BucketVersioningStatus Status { get; }

    /// <summary>
    /// Initializes the exception with a multi-line message containing the bucket
    /// name, the AWS CLI fix command, the README anchor, and the <c>--allow-unversioned</c>
    /// escape hatch.
    /// </summary>
    public BucketVersioningNotEnabledException(string bucket, BucketVersioningStatus status)
        : base(BuildMessage(bucket, status))
    {
        Bucket = bucket;
        Status = status;
    }

    /// <summary>
    /// Renders the operator-facing remediation message. Kept on a static helper
    /// so unit tests can assert against the exact wording without instantiating
    /// the exception.
    /// </summary>
    public static string BuildMessage(string bucket, BucketVersioningStatus status) =>
        $"Bucket versioning must be Enabled on '{bucket}' (current: {status}). " +
        "OSVFS refuses to start because local edits and deletes propagate to the " +
        "object store as overwrites and DeleteObject calls, and versioning is what " +
        "makes those recoverable." + Environment.NewLine +
        Environment.NewLine +
        "Enable it with:" + Environment.NewLine +
        $"  aws s3api put-bucket-versioning --bucket {bucket} --versioning-configuration Status=Enabled" +
        Environment.NewLine +
        Environment.NewLine +
        "To bypass this check (CI / disposable buckets only), pass --allow-unversioned.";
}
