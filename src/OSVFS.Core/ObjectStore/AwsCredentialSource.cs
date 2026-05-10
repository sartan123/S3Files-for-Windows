using Amazon.Runtime;

namespace OSVFS.ObjectStore;

/// <summary>
/// Discriminated wrapper that carries either OSVFS-managed static credentials or a
/// refreshing <see cref="AWSCredentials"/> instance handed back by the AWS SDK
/// (for shared <c>~/.aws/config</c> profiles, <c>credential_process</c>, SSO,
/// assume-role chains, etc.). Backends call <see cref="Materialize"/> to obtain
/// the SDK credential object to feed into <see cref="AmazonServiceClient"/>s,
/// regardless of which side of the union supplied it.
/// </summary>
internal sealed class AwsCredentialSource : IObjectStoreCredentialSource
{
    /// <summary>
    /// Static AWS credentials originating from the OSVFS DPAPI store, when this
    /// source represents the static branch; null on the SDK-wrapped branch.
    /// </summary>
    public AwsCredential? Static { get; }

    /// <summary>
    /// SDK-resolved credentials (e.g. <see cref="ProcessAWSCredentials"/>), when
    /// this source wraps a refreshing AWSCredentials; null on the static branch.
    /// </summary>
    public AWSCredentials? Sdk { get; }

    /// <summary>
    /// Human-readable description of the resolution path (e.g.
    /// <c>"OSVFS profile 'prod'"</c> or <c>"shared profile 'osvfs-login' (sso)"</c>).
    /// Surfaced by the doctor and the mount-startup log message.
    /// </summary>
    public string Description { get; }

    private AwsCredentialSource(AwsCredential? @static, AWSCredentials? sdk, string description)
    {
        Static = @static;
        Sdk = sdk;
        Description = description;
    }

    /// <summary>
    /// Wraps OSVFS-managed static credentials.
    /// </summary>
    public static AwsCredentialSource FromStatic(AwsCredential credential, string description)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new AwsCredentialSource(credential, sdk: null, description);
    }

    /// <summary>
    /// Wraps SDK-resolved credentials returned by <c>CredentialProfileStoreChain</c>
    /// or any other refreshing provider; the caller keeps ownership of refresh.
    /// </summary>
    public static AwsCredentialSource FromSdk(AWSCredentials credentials, string description)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new AwsCredentialSource(@static: null, credentials, description);
    }

    /// <summary>
    /// Materializes the source into the <see cref="AWSCredentials"/> object the AWS
    /// SDK clients consume. The static branch builds a fresh <see cref="BasicAWSCredentials"/>
    /// or <see cref="SessionAWSCredentials"/>; the SDK branch returns the wrapped instance
    /// so its refresh behavior is preserved.
    /// </summary>
    public AWSCredentials Materialize()
    {
        if (Sdk is not null) return Sdk;
        var c = Static!;
        return string.IsNullOrEmpty(c.SessionToken)
            ? new BasicAWSCredentials(c.AccessKeyId, c.SecretAccessKey)
            : new SessionAWSCredentials(c.AccessKeyId, c.SecretAccessKey, c.SessionToken);
    }
}
