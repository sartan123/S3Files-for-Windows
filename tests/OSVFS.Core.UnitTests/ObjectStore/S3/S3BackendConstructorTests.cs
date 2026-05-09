using OSVFS.ObjectStore.S3;
using Xunit;

namespace OSVFS.Core.UnitTests.ObjectStore.S3;

/// <summary>
/// Smoke tests that exercise the new <c>retryMaxAttempts</c> constructor
/// parameter. The tests do not perform any S3 traffic — they confirm the
/// client builds cleanly across the bounds the production code threads
/// through (default, explicit, and the lower bound of <c>1</c>).
/// An <c>endpointUrl</c> is supplied throughout because <see cref="Amazon.S3.AmazonS3Config"/>
/// validates that either an endpoint URL or a <c>RegionEndpoint</c> is
/// present at client construction; CI hosts without an AWS region in their
/// environment would otherwise fail the constructor before the parameter
/// under test is even consulted.
/// </summary>
public class S3BackendConstructorTests
{
    private const string LocalEndpoint = "http://localhost:4566";

    [Fact]
    public void Builds_with_default_retry_attempts()
    {
        using var backend = new S3Backend("test-bucket", endpointUrl: LocalEndpoint);
        Assert.NotNull(backend);
    }

    [Fact]
    public void Builds_with_explicit_retry_attempts()
    {
        using var backend = new S3Backend(
            "test-bucket",
            endpointUrl: LocalEndpoint,
            retryMaxAttempts: 5);
        Assert.NotNull(backend);
    }

    [Fact]
    public void Builds_with_attempts_one_disables_retries_without_throwing()
    {
        // 1 means "one attempt, no retries"; the SDK is configured with
        // MaxErrorRetry = max(0, 1 - 1) = 0. Verify the clamp keeps the SDK
        // happy even at the lower bound.
        using var backend = new S3Backend(
            "test-bucket",
            endpointUrl: LocalEndpoint,
            retryMaxAttempts: 1);
        Assert.NotNull(backend);
    }
}
