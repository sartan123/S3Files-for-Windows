using Amazon.Runtime;
using OSVFS.ObjectStore;
using Xunit;

namespace OSVFS.Core.UnitTests.ObjectStore;

/// <summary>
/// Verifies <see cref="AwsCredentialSource"/> materialization: the static branch
/// builds the appropriate <see cref="BasicAWSCredentials"/> /
/// <see cref="SessionAWSCredentials"/> based on session-token presence, and the
/// SDK branch returns the wrapped instance verbatim so refresh behavior survives.
/// </summary>
public class AwsCredentialSourceTests
{
    [Fact]
    public void FromStatic_without_session_token_materializes_BasicAWSCredentials()
    {
        var source = AwsCredentialSource.FromStatic(
            new AwsCredential { AccessKeyId = "AKIA", SecretAccessKey = "secret" },
            "OSVFS profile 'prod'");

        var aws = source.Materialize();
        var immutable = aws.GetCredentials();

        Assert.IsType<BasicAWSCredentials>(aws);
        Assert.Equal("AKIA", immutable.AccessKey);
        Assert.Equal("secret", immutable.SecretKey);
        Assert.True(string.IsNullOrEmpty(immutable.Token));
        Assert.Equal("OSVFS profile 'prod'", source.Description);
    }

    [Fact]
    public void FromStatic_with_session_token_materializes_SessionAWSCredentials()
    {
        var source = AwsCredentialSource.FromStatic(
            new AwsCredential
            {
                AccessKeyId = "ASIA",
                SecretAccessKey = "secret",
                SessionToken = "token",
            },
            "OSVFS profile 'temp'");

        var aws = source.Materialize();
        var immutable = aws.GetCredentials();

        Assert.IsType<SessionAWSCredentials>(aws);
        Assert.Equal("ASIA", immutable.AccessKey);
        Assert.Equal("token", immutable.Token);
    }

    [Fact]
    public void FromSdk_returns_wrapped_credentials_unchanged()
    {
        var wrapped = new BasicAWSCredentials("ASIASHARED", "shared-secret");
        var source = AwsCredentialSource.FromSdk(
            wrapped, "shared profile 'osvfs-login' (credential_process)");

        Assert.Same(wrapped, source.Materialize());
        Assert.Same(wrapped, source.Sdk);
        Assert.Null(source.Static);
    }
}
