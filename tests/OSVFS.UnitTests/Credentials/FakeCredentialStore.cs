using OSVFS.Credentials;
using OSVFS.ObjectStore;

namespace OSVFS.UnitTests.Credentials;

/// <summary>
/// In-memory <see cref="IAwsCredentialStore"/> for exercising the CLI factory
/// without touching the real Cred Manager.
/// </summary>
internal sealed class FakeCredentialStore : IAwsCredentialStore
{
    private readonly Dictionary<string, AwsCredential> entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Snapshot of the currently stored credentials, keyed by profile name.
    /// </summary>
    public IReadOnlyDictionary<string, AwsCredential> Entries => entries;

    /// <inheritdoc/>
    public void Save(string profileName, AwsCredential credential) =>
        entries[profileName] = credential;

    /// <inheritdoc/>
    public AwsCredential? Load(string profileName) =>
        entries.TryGetValue(profileName, out var credential) ? credential : null;

    /// <inheritdoc/>
    public bool Delete(string profileName) => entries.Remove(profileName);

    /// <inheritdoc/>
    public IReadOnlyList<string> List() =>
        [.. entries.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];
}
