using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using OSVFS.ObjectStore;
using OSVFS.ObjectStore.AzureBlob;
using System.Text;
using Xunit;

namespace OSVFS.Core.IntegrationTests;

/// <summary>
/// Round-trip tests for the Step 2A Azure Blob backend skeleton, exercised
/// against a real Azurite container. These tests only cover the contract the
/// virtualization layer relies on (List / Head / ReadRange / Upload / Delete /
/// Rename); Versioning + Soft Delete enforcement, Block Blob commit-style
/// uploads, and Event Grid change notifications land in Step 2C / 2D / 2E.
/// </summary>
[Collection(AzuriteCollection.Name)]
public sealed class AzureBlobBackendTests : IAsyncLifetime
{
    private readonly AzuriteFixture azurite;
    private readonly string container = $"osvfs-{Guid.NewGuid():N}";
    private BlobContainerClient adminClient = null!;
    private AzureBlobBackend backend = null!;

    public AzureBlobBackendTests(AzuriteFixture azurite)
    {
        this.azurite = azurite;
    }

    public async Task InitializeAsync()
    {
        // Pin the SDK's x-ms-version on every Azurite-bound client so a
        // Dependabot bump of Azure.Storage.Blobs cannot silently roll the
        // default ServiceVersion past Azurite's max-supported API version
        // (currently 2025-11-05). See AzuriteFixture for the pin contract.
        adminClient = new BlobContainerClient(
            azurite.ConnectionString, container, AzuriteFixture.BuildBlobClientOptions());
        await adminClient.CreateIfNotExistsAsync();
        backend = new AzureBlobBackend(
            container,
            AzureCredentialSource.FromConnectionString(azurite.ConnectionString, "azurite"),
            clientOptions: AzuriteFixture.BuildBlobClientOptions());
    }

    public async Task DisposeAsync()
    {
        try
        {
            await adminClient.DeleteIfExistsAsync();
        }
        catch
        {
            // Best-effort cleanup so the next test runs unaffected.
        }
        backend.Dispose();
    }

    [Fact]
    public void UserMetadataMaxBytes_matches_Azure_8KiB_limit()
    {
        Assert.Equal(8 * 1024, backend.UserMetadataMaxBytes);
    }

    [Fact]
    public async Task GetBucketVersioningStatus_returns_NotEnabled_when_soft_delete_off()
    {
        // Azurite starts with Soft Delete off, so the safety guard must refuse
        // to start until the operator turns it on. The Versioning half of the
        // bar is operator-side (see GetEnableVersioningInstructions); the
        // automatic check covers Soft Delete alone because Versioning state
        // lives on the ARM control plane that OSVFS does not depend on.
        await SetSoftDeleteAsync(enabled: false);
        var status = await backend.GetBucketVersioningStatusAsync(CancellationToken.None);
        Assert.Equal(BucketVersioningStatus.NotEnabled, status);
    }

    [Fact]
    public async Task GetBucketVersioningStatus_returns_Enabled_when_soft_delete_on()
    {
        await SetSoftDeleteAsync(enabled: true);
        var status = await backend.GetBucketVersioningStatusAsync(CancellationToken.None);
        Assert.Equal(BucketVersioningStatus.Enabled, status);
    }

    [Fact]
    public void GetEnableVersioningInstructions_returns_az_cli_command_with_account_name()
    {
        // Azurite reports the well-known dev account name; the instructions must
        // splice it into the az cli command so the operator can copy-paste.
        var instructions = backend.GetEnableVersioningInstructions();
        Assert.Contains("az storage account blob-service-properties update", instructions, StringComparison.Ordinal);
        Assert.Contains("--enable-versioning true", instructions, StringComparison.Ordinal);
        Assert.Contains("--enable-delete-retention true", instructions, StringComparison.Ordinal);
        Assert.Contains(AzuriteFixture.AccountName, instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_then_Head_round_trips_size_and_metadata()
    {
        var payload = "hello, azure"u8.ToArray();
        var metadata = new Dictionary<string, string> { ["tag"] = "alpha" };
        using (var ms = new MemoryStream(payload))
        {
            await backend.UploadAsync(
                "docs/file.txt", ms, ifMatchETag: null, CancellationToken.None,
                userMetadata: metadata);
        }

        var head = await backend.HeadAsync("docs/file.txt", CancellationToken.None);

        Assert.NotNull(head);
        Assert.Equal(payload.Length, head!.Value.Size);
        Assert.False(head.Value.IsDirectory);
        Assert.NotNull(head.Value.UserMetadata);
        Assert.Equal("alpha", head.Value.UserMetadata!["tag"]);
    }

    [Fact]
    public async Task ReadRange_returns_requested_byte_range()
    {
        var payload = Encoding.UTF8.GetBytes("0123456789abcdef");
        using (var ms = new MemoryStream(payload))
        {
            await backend.UploadAsync(
                "range.bin", ms, ifMatchETag: null, CancellationToken.None);
        }

        using var dest = new MemoryStream();
        await backend.ReadRangeAsync("range.bin", offset: 4, length: 6, dest, CancellationToken.None);

        Assert.Equal("456789", Encoding.UTF8.GetString(dest.ToArray()));
    }

    [Fact]
    public async Task ListAsync_yields_immediate_children_and_synthesized_directories()
    {
        await UploadAsync("docs/a.txt", "a");
        await UploadAsync("docs/b.txt", "b");
        await UploadAsync("docs/sub/c.txt", "c");
        await UploadAsync("top.txt", "t");

        var docs = await CollectAsync(backend.ListAsync("docs", CancellationToken.None));

        Assert.Contains(docs, info => info.RelativePath == "docs\\a.txt" && !info.IsDirectory);
        Assert.Contains(docs, info => info.RelativePath == "docs\\b.txt" && !info.IsDirectory);
        Assert.Contains(docs, info => info.RelativePath == "docs\\sub" && info.IsDirectory);
        Assert.DoesNotContain(docs, info => info.RelativePath == "top.txt");
    }

    [Fact]
    public async Task Delete_removes_object()
    {
        await UploadAsync("delete-me.txt", "x");
        await backend.DeleteAsync("delete-me.txt", CancellationToken.None);

        var head = await backend.HeadAsync("delete-me.txt", CancellationToken.None);
        Assert.Null(head);
    }

    [Fact]
    public async Task DeletePrefix_removes_every_object_under_directory()
    {
        await UploadAsync("dir/a.txt", "a");
        await UploadAsync("dir/sub/b.txt", "b");
        await UploadAsync("other.txt", "o");

        await backend.DeletePrefixAsync("dir", CancellationToken.None);

        Assert.Null(await backend.HeadAsync("dir/a.txt", CancellationToken.None));
        Assert.Null(await backend.HeadAsync("dir/sub/b.txt", CancellationToken.None));
        Assert.NotNull(await backend.HeadAsync("other.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Upload_with_multipart_threshold_exceeded_round_trips_via_block_commit()
    {
        // Force the SDK onto the StageBlock + CommitBlockList path by
        // configuring a very small threshold and part size, then uploading
        // a payload that's well above both. The blob should still come back
        // identical to what we wrote — proving the multipart commit path
        // is intact under operator-supplied tuning.
        using var multipartBackend = new AzureBlobBackend(
            container,
            AzureCredentialSource.FromConnectionString(azurite.ConnectionString, "azurite"),
            endpointUrl: null,
            keyPrefix: null,
            upLimiter: null,
            downLimiter: null,
            multipartThresholdBytes: 256 * 1024,    // 256 KiB threshold
            multipartPartSizeBytes: 256 * 1024,     // 256 KiB blocks
            maxConcurrentUploads: 2,
            maxConcurrentDownloads: 2,
            maxMultipartParts: 4,
            clientOptions: AzuriteFixture.BuildBlobClientOptions());

        var payload = new byte[1_500_000]; // ~1.5 MiB across multiple 256 KiB blocks
        new Random(42).NextBytes(payload);

        using (var ms = new MemoryStream(payload))
        {
            await multipartBackend.UploadAsync(
                "big.bin", ms, ifMatchETag: null, CancellationToken.None);
        }

        var head = await multipartBackend.HeadAsync("big.bin", CancellationToken.None);
        Assert.NotNull(head);
        Assert.Equal(payload.Length, head!.Value.Size);

        using var dest = new MemoryStream();
        await multipartBackend.ReadRangeAsync(
            "big.bin", offset: 0, length: payload.Length, dest, CancellationToken.None);
        Assert.Equal(payload, dest.ToArray());
    }

    [Fact]
    public async Task Rename_moves_object_to_new_key()
    {
        await UploadAsync("src.txt", "data");

        await backend.RenameAsync("src.txt", "dst.txt", CancellationToken.None);

        Assert.Null(await backend.HeadAsync("src.txt", CancellationToken.None));
        var dst = await backend.HeadAsync("dst.txt", CancellationToken.None);
        Assert.NotNull(dst);
        Assert.Equal(4, dst!.Value.Size);
    }

    [Fact]
    public async Task Upload_validates_user_metadata_against_8KiB_limit()
    {
        // Combined name+value byte count = 1 + 8200 = 8201 > 8192.
        var oversized = new Dictionary<string, string>
        {
            ["k"] = new string('v', 8200),
        };

        using var ms = new MemoryStream("data"u8.ToArray());
        await Assert.ThrowsAsync<UserMetadataTooLargeException>(async () =>
            await backend.UploadAsync(
                "meta/oversized.txt", ms, ifMatchETag: null, CancellationToken.None,
                userMetadata: oversized));
    }

    /// <summary>
    /// Flips the storage-account-level Soft Delete (blob retention) flag.
    /// Azurite supports <c>SetServiceProperties</c> end-to-end so the IT
    /// can drive the safety-guard decision table directly.
    /// </summary>
    private async Task SetSoftDeleteAsync(bool enabled)
    {
        var serviceClient = new BlobServiceClient(
            azurite.ConnectionString, AzuriteFixture.BuildBlobClientOptions());
        var resp = await serviceClient.GetPropertiesAsync();
        var props = resp.Value;
        props.DeleteRetentionPolicy = new BlobRetentionPolicy
        {
            Enabled = enabled,
            Days = enabled ? 7 : null,
        };
        await serviceClient.SetPropertiesAsync(props);
    }

    private async Task UploadAsync(string relativePath, string body)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(body));
        await backend.UploadAsync(
            relativePath, ms, ifMatchETag: null, CancellationToken.None);
    }

    private static async Task<List<ObjectInfo>> CollectAsync(IAsyncEnumerable<ObjectInfo> source)
    {
        var list = new List<ObjectInfo>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }
}
