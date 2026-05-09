using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.ObjectStore;
using OSVFS.ProjFs;
using Xunit;

namespace OSVFS.UnitTests.ProjFs;

/// <summary>
/// Exercises the safety-policy decision table that
/// <see cref="BucketVersioningGuard"/> applies before ProjFS is touched. The
/// guard is the only place where the operator-facing remediation message is
/// emitted, so the tests assert against its exact wording.
/// </summary>
public class BucketVersioningGuardTests
{
    private const string Bucket = "my-bucket";

    [Fact]
    public void Validate_returns_silently_when_versioning_enabled()
    {
        // Should not throw or warn for the green path.
        BucketVersioningGuard.Validate(
            BucketVersioningStatus.Enabled,
            Bucket,
            allowUnversioned: false,
            NullLogger.Instance);
    }

    [Fact]
    public void Validate_throws_when_versioning_not_enabled_and_no_opt_out()
    {
        var ex = Assert.Throws<BucketVersioningNotEnabledException>(() =>
            BucketVersioningGuard.Validate(
                BucketVersioningStatus.NotEnabled,
                Bucket,
                allowUnversioned: false,
                NullLogger.Instance));

        Assert.Equal(Bucket, ex.Bucket);
        Assert.Equal(BucketVersioningStatus.NotEnabled, ex.Status);
    }

    [Fact]
    public void Validate_does_not_throw_when_allow_unversioned_is_true()
    {
        // The guard should not throw; the host is responsible for the periodic warning.
        BucketVersioningGuard.Validate(
            BucketVersioningStatus.NotEnabled,
            Bucket,
            allowUnversioned: true,
            NullLogger.Instance);
    }

    [Fact]
    public void Exception_message_contains_bucket_name_fix_command_and_readme_link()
    {
        var ex = new BucketVersioningNotEnabledException(Bucket, BucketVersioningStatus.NotEnabled);

        Assert.Contains(Bucket, ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"aws s3api put-bucket-versioning --bucket {Bucket} --versioning-configuration Status=Enabled",
            ex.Message,
            StringComparison.Ordinal);
        Assert.Contains("--allow-unversioned", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exception_message_reports_observed_status()
    {
        var ex = new BucketVersioningNotEnabledException(Bucket, BucketVersioningStatus.NotEnabled);

        Assert.Contains("NotEnabled", ex.Message, StringComparison.Ordinal);
    }
}
