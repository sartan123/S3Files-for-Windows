using OSVFS.Credentials;
using OSVFS.ObjectStore;
using Xunit;

namespace OSVFS.UnitTests.Credentials;

/// <summary>
/// Exercises <see cref="WindowsCredentialStore"/> against the real Windows Credential
/// Manager + DPAPI on the test machine. Each test reserves a fresh GUID-suffixed
/// profile and cleans it up in <see cref="Dispose"/> so the host's existing
/// credentials are never touched.
/// </summary>
public sealed class WindowsCredentialStoreTests : IDisposable
{
    private readonly WindowsCredentialStore store = new();

    private readonly List<string> reservedProfiles = [];

    /// <summary>
    /// Reserves a unique profile name and tracks it for teardown.
    /// </summary>
    private string NewProfile()
    {
        var name = $"unit-test-{Guid.NewGuid():N}";
        reservedProfiles.Add(name);
        return name;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var profile in reservedProfiles)
        {
            try
            {
                store.Delete(profile);
            }
            catch
            {
                // Best-effort cleanup: if the entry was never created, or Cred Manager
                // is in an unexpected state, swallow so a single broken test doesn't
                // mask the real failure for sibling tests.
            }
        }
    }

    [Fact]
    public void Save_then_Load_roundtrips_every_field()
    {
        var profile = NewProfile();
        store.Save(profile, new AwsCredential
        {
            AccessKeyId = "AKIAEXAMPLE",
            SecretAccessKey = "secret-value-1234",
            SessionToken = "token-xyz",
        });

        var loaded = store.Load(profile);

        Assert.NotNull(loaded);
        Assert.Equal("AKIAEXAMPLE", loaded.AccessKeyId);
        Assert.Equal("secret-value-1234", loaded.SecretAccessKey);
        Assert.Equal("token-xyz", loaded.SessionToken);
    }

    [Fact]
    public void Save_without_session_token_yields_null_session_token_on_load()
    {
        var profile = NewProfile();
        store.Save(profile, new AwsCredential
        {
            AccessKeyId = "AKIA",
            SecretAccessKey = "secret",
        });

        var loaded = store.Load(profile);

        Assert.NotNull(loaded);
        Assert.Null(loaded.SessionToken);
    }

    [Fact]
    public void Save_overwrites_an_existing_entry()
    {
        var profile = NewProfile();
        store.Save(profile, new AwsCredential
        {
            AccessKeyId = "first",
            SecretAccessKey = "first-secret",
        });
        store.Save(profile, new AwsCredential
        {
            AccessKeyId = "second",
            SecretAccessKey = "second-secret",
            SessionToken = "second-token",
        });

        var loaded = store.Load(profile);

        Assert.NotNull(loaded);
        Assert.Equal("second", loaded.AccessKeyId);
        Assert.Equal("second-secret", loaded.SecretAccessKey);
        Assert.Equal("second-token", loaded.SessionToken);
    }

    [Fact]
    public void Load_returns_null_for_missing_profile()
    {
        Assert.Null(store.Load($"unit-test-missing-{Guid.NewGuid():N}"));
    }

    [Fact]
    public void Delete_returns_true_for_existing_and_false_for_missing()
    {
        var profile = NewProfile();
        store.Save(profile, new AwsCredential
        {
            AccessKeyId = "k",
            SecretAccessKey = "s",
        });

        Assert.True(store.Delete(profile));
        Assert.False(store.Delete(profile));
    }

    [Fact]
    public void List_contains_saved_profile_and_omits_after_delete()
    {
        var profile = NewProfile();
        store.Save(profile, new AwsCredential
        {
            AccessKeyId = "k",
            SecretAccessKey = "s",
        });

        Assert.Contains(profile, store.List());

        store.Delete(profile);

        Assert.DoesNotContain(profile, store.List());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("foo*bar")]
    [InlineData("foo?bar")]
    public void Save_rejects_invalid_profile_name(string profileName)
    {
        var credential = new AwsCredential
        {
            AccessKeyId = "k",
            SecretAccessKey = "s",
        };

        Assert.ThrowsAny<ArgumentException>(() => store.Save(profileName, credential));
    }

    [Fact]
    public void Save_rejects_credential_with_empty_secret()
    {
        Assert.ThrowsAny<ArgumentException>(() => store.Save(
            NewProfile(),
            new AwsCredential
            {
                AccessKeyId = "k",
                SecretAccessKey = string.Empty,
            }));
    }
}
