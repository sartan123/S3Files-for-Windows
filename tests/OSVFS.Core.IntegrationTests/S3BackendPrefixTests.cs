using Amazon.S3;
using Amazon.S3.Model;
using OSVFS.ObjectStore;
using OSVFS.ObjectStore.S3;
using Xunit;

namespace OSVFS.Core.IntegrationTests;

/// <summary>
/// Verifies that an S3Backend constructed with a key prefix scopes every operation to that
/// prefix: lists ignore objects outside it, reads/writes go to keys under it, and the
/// returned ObjectInfo carries prefix-relative keys so downstream consumers (e.g. the
/// change-watcher snapshot) stay correctly keyed.
/// </summary>
[Collection(LocalStackCollection.Name)]
public sealed class S3BackendPrefixTests : IAsyncLifetime
{
    private readonly LocalStackFixture localStack;
    private readonly string bucket = $"osvfs-{Guid.NewGuid():N}";
    private const string Prefix = "linked/root";
    private AmazonS3Client adminClient = null!;
    private S3Backend backend = null!;

    public S3BackendPrefixTests(LocalStackFixture localStack)
    {
        this.localStack = localStack;
    }

    public async Task InitializeAsync()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = localStack.ServiceUrl,
            ForcePathStyle = true,
        };
        adminClient = new AmazonS3Client(config);
        await adminClient.PutBucketAsync(new PutBucketRequest { BucketName = bucket });

        backend = new S3Backend(bucket, localStack.ServiceUrl, Prefix);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await EmptyBucketAsync();
            await adminClient.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket });
        }
        catch (AmazonS3Exception)
        {
            // Best-effort cleanup.
        }
        backend.Dispose();
        adminClient.Dispose();
    }

    [Fact]
    public async Task Upload_writes_under_linked_prefix()
    {
        var payload = "hello"u8.ToArray();
        using var ms = new MemoryStream(payload);

        await backend.UploadAsync("docs\\readme.txt", ms, ifMatchETag: null, CancellationToken.None);

        // The actual key in S3 includes the prefix...
        var raw = await adminClient.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = $"{Prefix}/docs/readme.txt",
        });
        using var ms2 = new MemoryStream();
        await raw.ResponseStream.CopyToAsync(ms2);
        Assert.Equal(payload, ms2.ToArray());

        // ...but the backend surfaces a prefix-relative key.
        var head = await backend.HeadAsync("docs\\readme.txt", CancellationToken.None);
        Assert.NotNull(head);
        Assert.Equal("docs/readme.txt", head!.Value.Key);
        Assert.Equal("docs\\readme.txt", head.Value.RelativePath);
    }

    [Fact]
    public async Task List_ignores_objects_outside_linked_prefix()
    {
        // Inside the prefix:
        await PutAtRawKeyAsync($"{Prefix}/a.txt", "a"u8.ToArray());
        await PutAtRawKeyAsync($"{Prefix}/sub/b.txt", "b"u8.ToArray());
        // Outside the prefix — the backend must not see these.
        await PutAtRawKeyAsync("sibling/c.txt", "c"u8.ToArray());
        await PutAtRawKeyAsync("other.txt", "o"u8.ToArray());

        var entries = new List<ObjectInfo>();
        await foreach (var e in backend.ListAsync(string.Empty, CancellationToken.None))
        {
            entries.Add(e);
        }

        Assert.Contains(entries, e => e is { IsDirectory: false, RelativePath: "a.txt" });
        Assert.Contains(entries, e => e is { IsDirectory: true, RelativePath: "sub" });
        Assert.DoesNotContain(entries, e => e.RelativePath.Contains("sibling", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.RelativePath == "other.txt");
    }

    [Fact]
    public async Task ListAll_returns_only_prefix_relative_keys()
    {
        await PutAtRawKeyAsync($"{Prefix}/x/y.txt", "y"u8.ToArray());
        await PutAtRawKeyAsync($"{Prefix}/top.txt", "t"u8.ToArray());
        await PutAtRawKeyAsync("outside.txt", "z"u8.ToArray());

        var keys = new List<string>();
        await foreach (var e in backend.ListAllAsync(CancellationToken.None))
        {
            keys.Add(e.Key);
        }

        Assert.Contains("x/y.txt", keys);
        Assert.Contains("top.txt", keys);
        Assert.DoesNotContain("outside.txt", keys);
        Assert.DoesNotContain(keys, k => k.Contains(Prefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_only_removes_objects_under_linked_prefix()
    {
        await PutAtRawKeyAsync($"{Prefix}/doomed.txt", "x"u8.ToArray());
        await PutAtRawKeyAsync("doomed.txt", "y"u8.ToArray());

        await backend.DeleteAsync("doomed.txt", CancellationToken.None);

        // Same-named sibling outside the prefix must survive.
        var survivor = await adminClient.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = bucket,
            Key = "doomed.txt",
        });
        Assert.NotNull(survivor);

        // Inside the prefix is gone.
        await Assert.ThrowsAsync<AmazonS3Exception>(async () =>
            await adminClient.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = $"{Prefix}/doomed.txt",
            }));
    }

    [Fact]
    public async Task Rename_translates_keys_under_linked_prefix()
    {
        var payload = "rename me"u8.ToArray();
        await PutAtRawKeyAsync($"{Prefix}/old/name.txt", payload);

        await backend.RenameAsync("old\\name.txt", "new\\name.txt", CancellationToken.None);

        // Source removed from the prefix.
        await Assert.ThrowsAsync<AmazonS3Exception>(async () =>
            await adminClient.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = $"{Prefix}/old/name.txt",
            }));

        // Destination present at prefix-relative new key.
        var moved = await adminClient.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = $"{Prefix}/new/name.txt",
        });
        using var ms = new MemoryStream();
        await moved.ResponseStream.CopyToAsync(ms);
        Assert.Equal(payload, ms.ToArray());
    }

    [Fact]
    public async Task Head_recognizes_directory_under_linked_prefix()
    {
        await PutAtRawKeyAsync($"{Prefix}/dir/inside.txt", "x"u8.ToArray());

        var head = await backend.HeadAsync("dir", CancellationToken.None);
        Assert.NotNull(head);
        Assert.True(head!.Value.IsDirectory);
    }

    [Theory]
    [InlineData("/linked/root/")]
    [InlineData("linked/root")]
    [InlineData("\\linked\\root\\")]
    public async Task Prefix_normalization_is_tolerant_of_slashes(string prefixForm)
    {
        // Each form should resolve to the same effective prefix and reach the same key.
        using var b = new S3Backend(bucket, localStack.ServiceUrl, prefixForm);
        var payload = "x"u8.ToArray();
        using var ms = new MemoryStream(payload);

        await b.UploadAsync("normalized.txt", ms, ifMatchETag: null, CancellationToken.None);

        var resp = await adminClient.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = $"{Prefix}/normalized.txt",
        });
        using var roundtrip = new MemoryStream();
        await resp.ResponseStream.CopyToAsync(roundtrip);
        Assert.Equal(payload, roundtrip.ToArray());
    }

    private async Task PutAtRawKeyAsync(string s3Key, byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        await adminClient.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = s3Key,
            InputStream = ms,
            AutoCloseStream = false,
        });
    }

    private async Task EmptyBucketAsync()
    {
        var listing = await adminClient.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
        });

        var objects = listing.S3Objects;
        if (objects is null || objects.Count == 0) return;

        await adminClient.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = objects
                .Where(o => !string.IsNullOrEmpty(o.Key))
                .Select(o => new KeyVersion { Key = o.Key })
                .ToList(),
            Quiet = true,
        });
    }
}
