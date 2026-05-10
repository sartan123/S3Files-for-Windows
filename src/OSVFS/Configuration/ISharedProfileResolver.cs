using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;

namespace OSVFS.Configuration;

/// <summary>
/// Resolves a named profile against the shared AWS profile store
/// (<c>~/.aws/config</c> + <c>~/.aws/credentials</c> + the .NET-SDK-only
/// encrypted store). Wrapped behind an interface so the mount builder can be
/// unit-tested without touching the developer's real on-disk profiles.
/// </summary>
internal interface ISharedProfileResolver
{
    /// <summary>
    /// Returns the SDK-resolved credentials for <paramref name="profileName"/> together
    /// with a human-readable description of the credential strategy detected on the
    /// profile (static keys, credential_process, sso_session, login_session, …).
    /// Returns null when the profile is unknown to the shared store.
    /// </summary>
    SharedProfileResolution? Resolve(string profileName);
}

/// <summary>
/// Result of a successful shared-profile resolution.
/// </summary>
/// <param name="Credentials">SDK-managed credentials (often a refreshing wrapper).</param>
/// <param name="Description">Human-readable label, e.g. <c>"shared profile 'foo' (credential_process)"</c>.</param>
internal sealed record SharedProfileResolution(AWSCredentials Credentials, string Description);

/// <summary>
/// Production resolver that defers to <see cref="CredentialProfileStoreChain"/>
/// and tags the result with the credential strategy detected on the profile.
/// </summary>
internal sealed class DefaultSharedProfileResolver : ISharedProfileResolver
{
    /// <summary>Process-wide singleton; the chain is cheap and stateless.</summary>
    public static DefaultSharedProfileResolver Instance { get; } = new();

    private readonly CredentialProfileStoreChain chain = new();

    /// <inheritdoc/>
    public SharedProfileResolution? Resolve(string profileName)
    {
        if (!chain.TryGetAWSCredentials(profileName, out var credentials) || credentials is null)
        {
            return null;
        }
        var strategy = chain.TryGetProfile(profileName, out var profile) && profile is not null
            ? DescribeStrategy(profile)
            : "shared profile";
        return new SharedProfileResolution(credentials, $"shared profile '{profileName}' ({strategy})");
    }

    /// <summary>
    /// Inspects <see cref="CredentialProfileOptions"/> to label the credential strategy
    /// in priority order: explicit keys, credential_process, login_session (aws login),
    /// sso_session / SSO, role assumption, web identity, then a generic fallback.
    /// </summary>
    private static string DescribeStrategy(CredentialProfile profile)
    {
        var options = profile.Options;
        if (!string.IsNullOrEmpty(options.CredentialProcess)) return "credential_process";
        if (!string.IsNullOrEmpty(options.LoginSession)) return "login_session";
        if (!string.IsNullOrEmpty(options.SsoSession) || !string.IsNullOrEmpty(options.SsoStartUrl)) return "sso";
        if (!string.IsNullOrEmpty(options.RoleArn)) return "assume_role";
        if (!string.IsNullOrEmpty(options.WebIdentityTokenFile)) return "web_identity";
        if (!string.IsNullOrEmpty(options.AccessKey)) return "static_keys";
        return "shared";
    }
}
