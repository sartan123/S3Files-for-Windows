using OSVFS.ObjectStore;
using Xunit;

namespace OSVFS.Core.UnitTests.ObjectStore;

/// <summary>
/// Validates the provider-aware credential casting that
/// <see cref="ObjectStoreBackendFactory"/> applies before constructing a
/// backend. The provider-neutral <see cref="IObjectStoreCredentialSource"/>
/// seam is intentionally opaque, so the factory must reject a mismatch
/// (e.g. a future GCS source handed to an S3 mount) at startup.
/// </summary>
public class ObjectStoreBackendFactoryTests
{
    [Fact]
    public void Create_throws_for_S3_when_credentials_are_not_AwsCredentialSource()
    {
        var foreign = new ForeignCredentialSource();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ObjectStoreBackendFactory.Create(
                ObjectStoreProvider.S3,
                bucket: "my-bucket",
                credentials: foreign));

        Assert.Contains("S3", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(AwsCredentialSource), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ForeignCredentialSource), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_throws_NotSupportedException_for_Gcs()
    {
        // The provider seam is in place but the GCS backend itself is still a
        // Phase-2 placeholder; the factory must surface that explicitly so
        // misconfigured mounts fail loudly at startup.
        Assert.Throws<NotSupportedException>(() =>
            ObjectStoreBackendFactory.Create(
                ObjectStoreProvider.Gcs,
                bucket: "my-bucket"));
    }

    [Fact]
    public void Create_throws_for_AzureBlob_when_credentials_are_missing()
    {
        // Step 2A wires up the AzureBlob arm; without a connection string
        // (today the only supported AzureCredentialSource branch) the
        // backend constructor surfaces a clear InvalidOperationException
        // rather than letting the SDK fail downstream.
        Assert.Throws<InvalidOperationException>(() =>
            ObjectStoreBackendFactory.Create(
                ObjectStoreProvider.AzureBlob,
                bucket: "my-bucket"));
    }

    [Fact]
    public void Create_throws_for_AzureBlob_when_credentials_are_not_AzureCredentialSource()
    {
        var foreign = new ForeignCredentialSource();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ObjectStoreBackendFactory.Create(
                ObjectStoreProvider.AzureBlob,
                bucket: "my-bucket",
                credentials: foreign));

        Assert.Contains("AzureBlob", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AzureCredentialSource", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ForeignCredentialSource), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stand-in for a future non-AWS credential source. The factory should
    /// reject it for the S3 arm even though it implements the seam.
    /// </summary>
    private sealed class ForeignCredentialSource : IObjectStoreCredentialSource
    {
        public string Description => "stand-in";
    }
}
