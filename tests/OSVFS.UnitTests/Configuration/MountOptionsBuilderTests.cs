using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.Configuration;
using OSVFS.ObjectStore;
using OSVFS.UnitTests.Credentials;
using Xunit;

namespace OSVFS.UnitTests.Configuration;

/// <summary>
/// Unit tests for <see cref="MountOptionsBuilder"/>: validation, defaults,
/// and credential resolution. Mount-level CLI overrides are no longer part of
/// the surface, so the builder is exercised exclusively through
/// <see cref="OsvfsMountConfig"/> values.
/// </summary>
public class MountOptionsBuilderTests
{
    [Fact]
    public void Build_uses_mount_config_values()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            Region = "ap-northeast-1",
            Prefix = "team-a/",
            ReadOnly = true,
            SyncIntervalSeconds = 60,
            ChangeSource = ChangeSourceKind.Polling,
            SyncMode = SyncMode.Full,
            BandwidthUp = "5M",
            BandwidthDown = "10M",
            MultipartThreshold = "16M",
            MultipartPartSize = "16M",
            RetryMaxAttempts = 5,
        };

        var options = MountOptionsBuilder.Build(mount, new FakeCredentialStore(), NullLogger.Instance);

        Assert.Equal("alpha-bucket", options.Bucket);
        Assert.Equal(@"C:\mounts\alpha", options.VirtRoot);
        Assert.Equal("ap-northeast-1", options.Region);
        Assert.Equal("team-a/", options.KeyPrefix);
        Assert.True(options.ReadOnly);
        Assert.Equal(60, options.SyncIntervalSeconds);
        Assert.Equal(SyncMode.Full, options.SyncMode);
        Assert.Equal(5L * 1024 * 1024, options.BandwidthLimits.UpBytesPerSecond);
        Assert.Equal(10L * 1024 * 1024, options.BandwidthLimits.DownBytesPerSecond);
        Assert.Equal(16L * 1024 * 1024, options.MultipartThresholdBytes);
        Assert.Equal(16L * 1024 * 1024, options.MultipartPartSizeBytes);
        Assert.Equal(5, options.RetryMaxAttempts);
    }

    [Fact]
    public void Build_throws_when_bucket_missing()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            RootFolder = @"C:\mounts\alpha",
        };

        var ex = Assert.Throws<OsvfsConfigException>(() =>
            MountOptionsBuilder.Build(mount, new FakeCredentialStore(), NullLogger.Instance));
        Assert.Contains("alpha", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bucket", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_throws_when_root_folder_missing()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
        };

        var ex = Assert.Throws<OsvfsConfigException>(() =>
            MountOptionsBuilder.Build(mount, new FakeCredentialStore(), NullLogger.Instance));
        Assert.Contains("root-folder", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_throws_when_events_change_source_lacks_event_queue()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            ChangeSource = ChangeSourceKind.Events,
        };

        var ex = Assert.Throws<OsvfsConfigException>(() =>
            MountOptionsBuilder.Build(mount, new FakeCredentialStore(), NullLogger.Instance));
        Assert.Contains("event-queue", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_throws_when_retry_max_attempts_below_one()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            RetryMaxAttempts = 0,
        };

        var ex = Assert.Throws<OsvfsConfigException>(() =>
            MountOptionsBuilder.Build(mount, new FakeCredentialStore(), NullLogger.Instance));
        Assert.Contains("retry-max-attempts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_resolves_aws_profile_through_credential_store()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            AwsProfile = "prod",
        };
        var store = new FakeCredentialStore();
        store.Save("prod", new AwsCredential { AccessKeyId = "AKIA", SecretAccessKey = "secret" });

        var options = MountOptionsBuilder.Build(mount, store, NullLogger.Instance);

        Assert.NotNull(options.Credentials);
        Assert.Equal("AKIA", options.Credentials.AccessKeyId);
    }

    [Fact]
    public void Build_throws_when_aws_profile_missing_in_store()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            AwsProfile = "absent",
        };

        var ex = Assert.Throws<OsvfsConfigException>(() =>
            MountOptionsBuilder.Build(mount, new FakeCredentialStore(), NullLogger.Instance));
        Assert.Contains("absent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_applies_built_in_defaults_when_mount_omits_them()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "default",
            Bucket = "b",
            RootFolder = @"C:\b",
        };

        var options = MountOptionsBuilder.Build(mount, new FakeCredentialStore(), NullLogger.Instance);

        Assert.Equal(ObjectStoreProvider.S3, options.Provider);
        Assert.False(options.ReadOnly);
        Assert.Equal(30, options.SyncIntervalSeconds);
        Assert.Equal(ChangeSourceKind.Polling, options.ChangeSource);
        Assert.Equal(SyncMode.OnDemand, options.SyncMode);
        Assert.False(options.AllowUnversioned);
    }
}
