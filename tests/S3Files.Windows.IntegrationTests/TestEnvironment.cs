using System.Runtime.CompilerServices;

namespace S3Files.Windows.IntegrationTests;

internal static class TestEnvironment
{
    /// <summary>
    /// AWSSDK probes the default credential chain when no credentials are supplied.
    /// LocalStack accepts any non-empty values; we set env vars at module load so the
    /// SDK doesn't try to contact EC2 metadata or SSO during tests.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "test");
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "test");
        Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", "us-east-1");
        Environment.SetEnvironmentVariable("AWS_REGION", "us-east-1");
        Environment.SetEnvironmentVariable("AWS_EC2_METADATA_DISABLED", "true");

        // Disable the Testcontainers resource reaper (Ryuk). Our fixtures dispose
        // their containers explicitly, and Ryuk's shared container can race when
        // multiple test assemblies run concurrently (e.g. `dotnet test` on the slnx).
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }
}
