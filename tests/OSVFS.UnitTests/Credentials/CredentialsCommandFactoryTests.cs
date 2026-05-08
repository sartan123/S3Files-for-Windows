using System.CommandLine;
using OSVFS.Credentials;
using OSVFS.ObjectStore;
using Xunit;

namespace OSVFS.UnitTests.Credentials;

/// <summary>
/// Drives the <c>credentials</c> command tree against a <see cref="FakeCredentialStore"/>
/// so the CLI wiring (option binding, dispatch, exit codes, console output) is
/// validated without touching real Windows infrastructure. Tests are sequential
/// because they redirect <see cref="Console.Out"/> / <see cref="Console.Error"/>.
/// </summary>
[Collection(nameof(CredentialsCommandFactoryTests))]
[CollectionDefinition(nameof(CredentialsCommandFactoryTests), DisableParallelization = true)]
public class CredentialsCommandFactoryTests
{
    [Fact]
    public void Set_with_explicit_args_persists_credential()
    {
        var store = new FakeCredentialStore();
        var (exit, stdout, _) = Run(store, "set",
            "--profile", "p1",
            "--access-key", "AKIA",
            "--secret-key", "secret-value");

        Assert.Equal(0, exit);
        Assert.Single(store.Entries);
        var saved = store.Entries["p1"];
        Assert.Equal("AKIA", saved.AccessKeyId);
        Assert.Equal("secret-value", saved.SecretAccessKey);
        Assert.Null(saved.SessionToken);
        Assert.Contains("Saved profile 'p1'", stdout);
    }

    [Fact]
    public void Set_with_session_token_persists_session_token()
    {
        var store = new FakeCredentialStore();
        var (exit, _, _) = Run(store, "set",
            "--profile", "p1",
            "--access-key", "k",
            "--secret-key", "s",
            "--session-token", "tok");

        Assert.Equal(0, exit);
        Assert.Equal("tok", store.Entries["p1"].SessionToken);
    }

    [Fact]
    public void Set_replaces_existing_entry()
    {
        var store = new FakeCredentialStore();
        store.Save("p1", new AwsCredential { AccessKeyId = "old", SecretAccessKey = "old-s" });

        var (exit, _, _) = Run(store, "set",
            "--profile", "p1",
            "--access-key", "new",
            "--secret-key", "new-s");

        Assert.Equal(0, exit);
        Assert.Equal("new", store.Entries["p1"].AccessKeyId);
        Assert.Equal("new-s", store.Entries["p1"].SecretAccessKey);
    }

    [Fact]
    public void Get_existing_profile_prints_metadata_without_revealing_secret()
    {
        var store = new FakeCredentialStore();
        store.Save("p1", new AwsCredential
        {
            AccessKeyId = "AKIAABC",
            SecretAccessKey = "supersecret-value",
        });

        var (exit, stdout, _) = Run(store, "get", "--profile", "p1");

        Assert.Equal(0, exit);
        Assert.Contains("AKIAABC", stdout);
        Assert.DoesNotContain("supersecret-value", stdout);
        Assert.Contains("(none)", stdout);
    }

    [Fact]
    public void Get_existing_profile_with_session_token_prints_present_marker()
    {
        var store = new FakeCredentialStore();
        store.Save("p1", new AwsCredential
        {
            AccessKeyId = "k",
            SecretAccessKey = "s",
            SessionToken = "tok",
        });

        var (_, stdout, _) = Run(store, "get", "--profile", "p1");

        Assert.Contains("(present)", stdout);
        Assert.DoesNotContain("tok", stdout);
    }

    [Fact]
    public void Get_missing_profile_returns_one_and_writes_stderr()
    {
        var (exit, _, stderr) = Run(new FakeCredentialStore(), "get", "--profile", "missing");

        Assert.Equal(1, exit);
        Assert.Contains("missing", stderr);
    }

    [Fact]
    public void List_prints_each_stored_profile_in_sorted_order()
    {
        var store = new FakeCredentialStore();
        store.Save("beta", new AwsCredential { AccessKeyId = "k", SecretAccessKey = "s" });
        store.Save("alpha", new AwsCredential { AccessKeyId = "k", SecretAccessKey = "s" });

        var (exit, stdout, _) = Run(store, "list");

        Assert.Equal(0, exit);
        var alphaIdx = stdout.IndexOf("alpha", StringComparison.Ordinal);
        var betaIdx = stdout.IndexOf("beta", StringComparison.Ordinal);
        Assert.True(alphaIdx >= 0 && betaIdx >= 0);
        Assert.True(alphaIdx < betaIdx, "list output should be alphabetically sorted");
    }

    [Fact]
    public void List_empty_prints_placeholder()
    {
        var (exit, stdout, _) = Run(new FakeCredentialStore(), "list");

        Assert.Equal(0, exit);
        Assert.Contains("no profiles stored", stdout);
    }

    [Fact]
    public void Remove_existing_returns_zero_and_deletes_entry()
    {
        var store = new FakeCredentialStore();
        store.Save("p1", new AwsCredential { AccessKeyId = "k", SecretAccessKey = "s" });

        var (exit, stdout, _) = Run(store, "remove", "--profile", "p1");

        Assert.Equal(0, exit);
        Assert.Empty(store.Entries);
        Assert.Contains("Removed profile 'p1'", stdout);
    }

    [Fact]
    public void Remove_missing_returns_one()
    {
        var (exit, _, stderr) = Run(new FakeCredentialStore(), "remove", "--profile", "missing");

        Assert.Equal(1, exit);
        Assert.Contains("missing", stderr);
    }

    /// <summary>
    /// Builds the credentials command tree against <paramref name="store"/>, parses
    /// <paramref name="args"/>, captures stdout/stderr, and returns the action's exit code.
    /// </summary>
    private static (int ExitCode, string Stdout, string Stderr) Run(IAwsCredentialStore store, params string[] args)
    {
        var command = CredentialsCommandFactory.Build(store);
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = command.Parse(args).Invoke();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }
}
