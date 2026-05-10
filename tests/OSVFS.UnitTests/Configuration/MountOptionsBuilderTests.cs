using Amazon.Runtime;
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_throws_when_max_concurrent_uploads_below_one(int value)
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            MaxConcurrentUploads = value,
        };

        var ex = Assert.Throws<OsvfsConfigException>(() =>
            MountOptionsBuilder.Build(mount, new FakeCredentialStore(), NullLogger.Instance));
        Assert.Contains("max-concurrent-uploads", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_throws_when_max_concurrent_downloads_below_one()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            MaxConcurrentDownloads = 0,
        };

        var ex = Assert.Throws<OsvfsConfigException>(() =>
            MountOptionsBuilder.Build(mount, new FakeCredentialStore(), NullLogger.Instance));
        Assert.Contains("max-concurrent-downloads", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_throws_when_max_multipart_parts_below_one()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            MaxMultipartParts = 0,
        };

        var ex = Assert.Throws<OsvfsConfigException>(() =>
            MountOptionsBuilder.Build(mount, new FakeCredentialStore(), NullLogger.Instance));
        Assert.Contains("max-multipart-parts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_threads_concurrency_settings_through_to_options()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            MaxConcurrentUploads = 6,
            MaxConcurrentDownloads = 12,
            MaxMultipartParts = 20,
        };

        var options = MountOptionsBuilder.Build(mount, new FakeCredentialStore(), NullLogger.Instance);

        Assert.Equal(6, options.MaxConcurrentUploads);
        Assert.Equal(12, options.MaxConcurrentDownloads);
        Assert.Equal(20, options.MaxMultipartParts);
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
        var sharedResolver = new FakeSharedProfileResolver();

        var options = MountOptionsBuilder.Build(mount, store, sharedResolver, NullLogger.Instance);

        Assert.NotNull(options.Credentials);
        // Options expose the provider-neutral seam; the host-side test still has
        // to cast back to AwsCredentialSource to inspect AWS-specific shape.
        var aws = Assert.IsType<AwsCredentialSource>(options.Credentials);
        Assert.NotNull(aws.Static);
        Assert.Equal("AKIA", aws.Static.AccessKeyId);
        Assert.Contains("OSVFS profile 'prod'", options.Credentials.Description);
        Assert.Equal(0, sharedResolver.Calls);
    }

    [Fact]
    public void Build_falls_back_to_shared_profile_when_dpapi_store_misses()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            AwsProfile = "osvfs-login",
        };
        var sdkCredentials = new BasicAWSCredentials("ASIASHARED", "shared-secret");
        var sharedResolver = new FakeSharedProfileResolver
        {
            Result = new SharedProfileResolution(
                sdkCredentials, "shared profile 'osvfs-login' (credential_process)"),
        };

        var options = MountOptionsBuilder.Build(
            mount, new FakeCredentialStore(), sharedResolver, NullLogger.Instance);

        Assert.NotNull(options.Credentials);
        var aws = Assert.IsType<AwsCredentialSource>(options.Credentials);
        Assert.Same(sdkCredentials, aws.Sdk);
        Assert.Contains("credential_process", options.Credentials.Description);
        Assert.Equal(1, sharedResolver.Calls);
        Assert.Equal("osvfs-login", sharedResolver.LastProfileName);
    }

    [Fact]
    public void Build_throws_when_aws_profile_missing_in_both_stores()
    {
        var mount = new OsvfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            AwsProfile = "absent",
        };

        var ex = Assert.Throws<OsvfsConfigException>(() =>
            MountOptionsBuilder.Build(
                mount, new FakeCredentialStore(), new FakeSharedProfileResolver(), NullLogger.Instance));
        Assert.Contains("absent", ex.Message, StringComparison.Ordinal);
        Assert.Contains("aws login", ex.Message, StringComparison.Ordinal);
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
