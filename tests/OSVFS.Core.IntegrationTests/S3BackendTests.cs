using Amazon.S3;
using Amazon.S3.Model;
using OSVFS.ObjectStore;
using OSVFS.ObjectStore.S3;
using System.Text;
using Xunit;

namespace OSVFS.Core.IntegrationTests;

[Collection(LocalStackCollection.Name)]
public sealed class S3BackendTests : IAsyncLifetime
{
    private readonly LocalStackFixture localStack;
    private readonly string bucket = $"osvfs-{Guid.NewGuid():N}";
    private AmazonS3Client adminClient = null!;
    private S3Backend backend = null!;

    public S3BackendTests(LocalStackFixture localStack)
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

        backend = new S3Backend(bucket, localStack.ServiceUrl);
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
            // Bucket cleanup is best-effort; swallow so the next test is unaffected.
        }
        backend.Dispose();
        adminClient.Dispose();
    }

    [Fact]
    public async Task GetBucketVersioningStatus_returns_NotEnabled_for_fresh_bucket()
    {
        var status = await backend.GetBucketVersioningStatusAsync(CancellationToken.None);
        Assert.Equal(BucketVersioningStatus.NotEnabled, status);
    }

    [Fact]
    public async Task GetBucketVersioningStatus_returns_Enabled_when_versioning_turned_on()
    {
        await adminClient.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

        var status = await backend.GetBucketVersioningStatusAsync(CancellationToken.None);
        Assert.Equal(BucketVersioningStatus.Enabled, status);
    }

    [Fact]
    public async Task GetBucketVersioningStatus_treats_Suspended_as_NotEnabled()
    {
        // Suspended is not "actively protecting writes", so the backend collapses it into
        // NotEnabled — the same state the startup safety check refuses to run against.
        await adminClient.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        await adminClient.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Suspended },
        });

        var status = await backend.GetBucketVersioningStatusAsync(CancellationToken.None);
        Assert.Equal(BucketVersioningStatus.NotEnabled, status);
    }

    [Fact]
    public async Task Upload_writes_content_retrievable_via_Head_and_ReadRange()
    {
        var payload = "hello, s3"u8.ToArray();
        await UploadBytesAsync("docs/readme.txt", payload);

        var head = await backend.HeadAsync("docs\\readme.txt", CancellationToken.None);
        Assert.NotNull(head);
        Assert.Equal(payload.Length, head!.Value.Size);
        Assert.False(head.Value.IsDirectory);

        using var ms = new MemoryStream();
        await backend.ReadRangeAsync("docs\\readme.txt", 0, payload.Length, ms, CancellationToken.None);
        Assert.Equal(payload, ms.ToArray());
    }

    [Fact]
    public async Task ReadRange_reads_partial_content()
    {
        var payload = Encoding.UTF8.GetBytes("0123456789ABCDEF");
        await UploadBytesAsync("blob.bin", payload);

        using var ms = new MemoryStream();
        await backend.ReadRangeAsync("blob.bin", 4, 6, ms, CancellationToken.None);
        Assert.Equal("456789", Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task Head_returns_null_for_missing_object()
    {
        var head = await backend.HeadAsync("does\\not\\exist.txt", CancellationToken.None);
        Assert.Null(head);
    }

    [Fact]
    public async Task Head_recognizes_directory_prefix()
    {
        await UploadBytesAsync("dir/inner.txt", "x"u8.ToArray());

        var head = await backend.HeadAsync("dir", CancellationToken.None);
        Assert.NotNull(head);
        Assert.True(head!.Value.IsDirectory);
    }

    [Fact]
    public async Task List_returns_immediate_children_and_subdirectories()
    {
        await UploadBytesAsync("a.txt", "a"u8.ToArray());
        await UploadBytesAsync("b.txt", "b"u8.ToArray());
        await UploadBytesAsync("dir/inside.txt", "c"u8.ToArray());
        await UploadBytesAsync("dir/nested/deep.txt", "d"u8.ToArray());

        var entries = new List<ObjectInfo>();
        await foreach (var e in backend.ListAsync(string.Empty, CancellationToken.None))
        {
            entries.Add(e);
        }

        Assert.Contains(entries, e => e is { IsDirectory: false, RelativePath: "a.txt" });
        Assert.Contains(entries, e => e is { IsDirectory: false, RelativePath: "b.txt" });
        Assert.Contains(entries, e => e is { IsDirectory: true, RelativePath: "dir" });
        // Listing the root with delimiter should NOT recurse into nested dirs.
        Assert.DoesNotContain(entries, e => e.RelativePath.Contains("nested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListRecursive_returns_all_keys_under_prefix()
    {
        await UploadBytesAsync("dir/a.txt", "a"u8.ToArray());
        await UploadBytesAsync("dir/sub/b.txt", "b"u8.ToArray());
        await UploadBytesAsync("other/c.txt", "c"u8.ToArray());

        var entries = new List<ObjectInfo>();
        await foreach (var e in backend.ListRecursiveAsync("dir", CancellationToken.None))
        {
            entries.Add(e);
        }

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.RelativePath == "dir\\a.txt");
        Assert.Contains(entries, e => e.RelativePath == "dir\\sub\\b.txt");
        Assert.DoesNotContain(entries, e => e.RelativePath.StartsWith("other", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListAll_returns_every_key_in_bucket()
    {
        await UploadBytesAsync("x/y/z.txt", "z"u8.ToArray());
        await UploadBytesAsync("top.txt", "t"u8.ToArray());

        var paths = new List<string>();
        await foreach (var e in backend.ListAllAsync(CancellationToken.None))
        {
            paths.Add(e.RelativePath);
        }

        Assert.Contains("x\\y\\z.txt", paths);
        Assert.Contains("top.txt", paths);
    }

    [Fact]
    public async Task Delete_removes_existing_object()
    {
        await UploadBytesAsync("doomed.txt", "bye"u8.ToArray());

        await backend.DeleteAsync("doomed.txt", CancellationToken.None);

        Assert.Null(await backend.HeadAsync("doomed.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_missing_object_is_idempotent()
    {
        // Should not throw — backend tolerates 404s.
        await backend.DeleteAsync("ghost.txt", CancellationToken.None);
    }

    [Fact]
    public async Task DeletePrefix_removes_all_objects_under_prefix()
    {
        await UploadBytesAsync("trash/a.txt", "a"u8.ToArray());
        await UploadBytesAsync("trash/b/c.txt", "c"u8.ToArray());
        await UploadBytesAsync("keep/keep.txt", "k"u8.ToArray());

        await backend.DeletePrefixAsync("trash", CancellationToken.None);

        Assert.Null(await backend.HeadAsync("trash\\a.txt", CancellationToken.None));
        Assert.Null(await backend.HeadAsync("trash\\b\\c.txt", CancellationToken.None));
        Assert.NotNull(await backend.HeadAsync("keep\\keep.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Rename_moves_single_object()
    {
        var payload = "rename me"u8.ToArray();
        await UploadBytesAsync("old/name.txt", payload);

        await backend.RenameAsync("old\\name.txt", "new\\name.txt", CancellationToken.None);

        Assert.Null(await backend.HeadAsync("old\\name.txt", CancellationToken.None));
        var moved = await backend.HeadAsync("new\\name.txt", CancellationToken.None);
        Assert.NotNull(moved);
        Assert.Equal(payload.Length, moved!.Value.Size);

        using var ms = new MemoryStream();
        await backend.ReadRangeAsync("new\\name.txt", 0, payload.Length, ms, CancellationToken.None);
        Assert.Equal(payload, ms.ToArray());
    }

    [Fact]
    public async Task Rename_to_same_path_is_noop()
    {
        await UploadBytesAsync("same.txt", "x"u8.ToArray());

        await backend.RenameAsync("same.txt", "same.txt", CancellationToken.None);

        Assert.NotNull(await backend.HeadAsync("same.txt", CancellationToken.None));
    }

    [Fact]
    public async Task RenamePrefix_moves_entire_subtree()
    {
        await UploadBytesAsync("src/a.txt", "a"u8.ToArray());
        await UploadBytesAsync("src/sub/b.txt", "b"u8.ToArray());
        await UploadBytesAsync("untouched.txt", "u"u8.ToArray());

        await backend.RenamePrefixAsync("src", "dst", CancellationToken.None);

        Assert.Null(await backend.HeadAsync("src\\a.txt", CancellationToken.None));
        Assert.Null(await backend.HeadAsync("src\\sub\\b.txt", CancellationToken.None));
        Assert.NotNull(await backend.HeadAsync("dst\\a.txt", CancellationToken.None));
        Assert.NotNull(await backend.HeadAsync("dst\\sub\\b.txt", CancellationToken.None));
        Assert.NotNull(await backend.HeadAsync("untouched.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Upload_with_matching_IfMatch_replaces_existing_object()
    {
        // Establish a baseline ETag.
        await UploadBytesAsync("etag.txt", "v1"u8.ToArray());
        var head = await backend.HeadAsync("etag.txt", CancellationToken.None);
        Assert.NotNull(head);
        var etag = head!.Value.ETag;

        using var ms = new MemoryStream("v2 contents"u8.ToArray());
        await backend.UploadAsync("etag.txt", ms, ifMatchETag: etag, CancellationToken.None);

        var updated = await backend.HeadAsync("etag.txt", CancellationToken.None);
        Assert.NotNull(updated);
        Assert.Equal("v2 contents".Length, updated!.Value.Size);

        // Note: We don't assert the stale-ETag failure path here because LocalStack's
        // community edition does not always enforce If-Match preconditions on PUT.
        // The mainline negative path is covered by deployment tests against real S3.
    }

    [Fact]
    public async Task Upload_creates_object_when_key_did_not_exist()
    {
        var payload = "fresh"u8.ToArray();
        using var ms = new MemoryStream(payload);
        var result = await backend.UploadAsync("brand/new.txt", ms, ifMatchETag: null, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(result.ETag));
        var head = await backend.HeadAsync("brand\\new.txt", CancellationToken.None);
        Assert.NotNull(head);
        Assert.Equal(payload.Length, head!.Value.Size);
    }

    [Fact]
    public async Task Upload_round_trips_user_metadata_via_Head()
    {
        // Issue #19: x-amz-meta-* round-trip. UploadAsync should reattach the same
        // user metadata that HeadAsync surfaces back to the caller.
        var metadata = new Dictionary<string, string>
        {
            ["tag"] = "hello",
            ["author"] = "alice",
        };

        using var ms = new MemoryStream("payload"u8.ToArray());
        await backend.UploadAsync(
            "meta/file.txt",
            ms,
            ifMatchETag: null,
            CancellationToken.None,
            userMetadata: metadata);

        var head = await backend.HeadAsync("meta\\file.txt", CancellationToken.None);
        Assert.NotNull(head);
        Assert.NotNull(head!.Value.UserMetadata);
        Assert.Equal("hello", head.Value.UserMetadata!["tag"]);
        Assert.Equal("alice", head.Value.UserMetadata["author"]);
    }

    [Fact]
    public async Task Upload_then_re_upload_preserves_user_metadata_round_trip()
    {
        // Mirrors the issue's full round-trip: download → re-upload (passing the
        // same metadata dictionary back) preserves every header.
        var initial = new Dictionary<string, string>
        {
            ["tag"] = "alpha",
            ["env"] = "prod",
        };

        using (var ms = new MemoryStream("v1"u8.ToArray()))
        {
            await backend.UploadAsync(
                "meta/loop.txt", ms, ifMatchETag: null, CancellationToken.None,
                userMetadata: initial);
        }

        var firstHead = await backend.HeadAsync("meta\\loop.txt", CancellationToken.None);
        Assert.NotNull(firstHead);
        var recovered = firstHead!.Value.UserMetadata;
        Assert.NotNull(recovered);

        using (var ms2 = new MemoryStream("v2"u8.ToArray()))
        {
            await backend.UploadAsync(
                "meta/loop.txt", ms2, ifMatchETag: null, CancellationToken.None,
                userMetadata: recovered);
        }

        var secondHead = await backend.HeadAsync("meta\\loop.txt", CancellationToken.None);
        Assert.NotNull(secondHead);
        var afterRoundTrip = secondHead!.Value.UserMetadata;
        Assert.NotNull(afterRoundTrip);
        Assert.Equal("alpha", afterRoundTrip!["tag"]);
        Assert.Equal("prod", afterRoundTrip["env"]);
    }

    [Fact]
    public async Task Upload_normalizes_user_metadata_keys_to_lowercase()
    {
        // S3 lowercases header names on the wire; the backend should pre-normalize
        // so the local snapshot matches what comes back on HeadAsync.
        var metadata = new Dictionary<string, string>
        {
            ["MixedCase"] = "value",
            ["UPPER"] = "loud",
        };

        using var ms = new MemoryStream("body"u8.ToArray());
        await backend.UploadAsync(
            "meta/case.txt", ms, ifMatchETag: null, CancellationToken.None,
            userMetadata: metadata);

        var head = await backend.HeadAsync("meta\\case.txt", CancellationToken.None);
        Assert.NotNull(head);
        Assert.NotNull(head!.Value.UserMetadata);
        Assert.True(head.Value.UserMetadata!.ContainsKey("mixedcase"));
        Assert.True(head.Value.UserMetadata.ContainsKey("upper"));
        Assert.Equal("value", head.Value.UserMetadata["mixedcase"]);
    }

    [Fact]
    public async Task Upload_rejects_user_metadata_above_2KiB_limit()
    {
        // Construct a payload whose combined name+value byte count exceeds the
        // AWS-documented 2 KiB cap. The backend should fail fast instead of
        // forwarding the request to S3.
        var oversized = new Dictionary<string, string>
        {
            [new string('k', 1024)] = new string('v', 1025),
        };

        using var ms = new MemoryStream("data"u8.ToArray());
        await Assert.ThrowsAsync<UserMetadataTooLargeException>(async () =>
        {
            await backend.UploadAsync(
                "meta/oversized.txt", ms, ifMatchETag: null, CancellationToken.None,
                userMetadata: oversized);
        });
    }

    [Fact]
    public async Task Rename_preserves_destination_user_metadata_on_atomic_replace()
    {
        // Reproduces the editor "atomic-replace save" pattern:
        //   1. existing file.txt has user metadata,
        //   2. editor uploads new content as file.txt~ with no metadata,
        //   3. editor renames file.txt~ over file.txt.
        // Without metadata preservation the destination's x-amz-meta-* would be
        // wiped because CopyObject defaults to copying the source's (empty) metadata.
        var originalMetadata = new Dictionary<string, string>
        {
            ["tag"] = "keep-me",
            ["author"] = "alice",
        };
        using (var ms = new MemoryStream("v1"u8.ToArray()))
        {
            await backend.UploadAsync(
                "atomic/file.txt", ms, ifMatchETag: null, CancellationToken.None,
                userMetadata: originalMetadata);
        }

        // Editor uploads the new content as a temp object with no user metadata.
        using (var tmp = new MemoryStream("v2-new-content"u8.ToArray()))
        {
            await backend.UploadAsync(
                "atomic/file.txt~", tmp, ifMatchETag: null, CancellationToken.None);
        }

        // Editor performs the atomic-replace via rename.
        await backend.RenameAsync("atomic\\file.txt~", "atomic\\file.txt", CancellationToken.None);

        var head = await backend.HeadAsync("atomic\\file.txt", CancellationToken.None);
        Assert.NotNull(head);
        // Content reflects the new (temp) bytes.
        using (var rt = new MemoryStream())
        {
            await backend.ReadRangeAsync(
                "atomic\\file.txt", 0, head!.Value.Size, rt, CancellationToken.None);
            Assert.Equal("v2-new-content", System.Text.Encoding.UTF8.GetString(rt.ToArray()));
        }
        // Metadata reflects the destination's pre-existing headers, not the empty source.
        Assert.NotNull(head!.Value.UserMetadata);
        Assert.Equal("keep-me", head.Value.UserMetadata!["tag"]);
        Assert.Equal("alice", head.Value.UserMetadata["author"]);
    }

    [Fact]
    public async Task Rename_to_brand_new_destination_keeps_source_user_metadata()
    {
        // When the destination doesn't already exist, the default CopyObject directive
        // should still carry the source's metadata over (no override applied).
        var sourceMetadata = new Dictionary<string, string>
        {
            ["origin"] = "source",
        };
        using (var ms = new MemoryStream("body"u8.ToArray()))
        {
            await backend.UploadAsync(
                "rename/from.txt", ms, ifMatchETag: null, CancellationToken.None,
                userMetadata: sourceMetadata);
        }

        await backend.RenameAsync("rename\\from.txt", "rename\\to.txt", CancellationToken.None);

        var head = await backend.HeadAsync("rename\\to.txt", CancellationToken.None);
        Assert.NotNull(head);
        Assert.NotNull(head!.Value.UserMetadata);
        Assert.Equal("source", head.Value.UserMetadata!["origin"]);
    }

    [Fact]
    public async Task Upload_uses_multipart_for_streams_above_threshold()
    {
        // 20 MiB is above the 16 MiB multipart threshold and exercises the part-loop path
        // (5 × 4 = 4 parts at the default 5 MiB part size).
        const int totalSize = 20 * 1024 * 1024;
        var payload = new byte[totalSize];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        using var ms = new MemoryStream(payload);
        var result = await backend.UploadAsync("big/blob.bin", ms, ifMatchETag: null, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(result.ETag));
        Assert.Equal(totalSize, result.Size);

        var head = await backend.HeadAsync("big\\blob.bin", CancellationToken.None);
        Assert.NotNull(head);
        Assert.Equal(totalSize, head!.Value.Size);

        // S3 multipart ETags are <md5>-<partCount>; the trailing -N is how we (and S3 clients
        // generally) identify multipart-uploaded objects.
        Assert.Contains('-', head.Value.ETag);

        using var roundtrip = new MemoryStream();
        await backend.ReadRangeAsync("big\\blob.bin", 0, totalSize, roundtrip, CancellationToken.None);
        Assert.Equal(payload, roundtrip.ToArray());
    }

    [Fact]
    public async Task Upload_honors_custom_multipart_part_size()
    {
        // Override the per-part size to 16 MiB and push a 100 MiB payload so the
        // multipart path must be used. The ETag should carry a 7-part suffix
        // (7 = ceil(100/16)) because S3 multipart ETags encode the part count.
        using var customBackend = new S3Backend(
            bucket,
            localStack.ServiceUrl,
            multipartThresholdBytes: 8L * 1024 * 1024,
            multipartPartSizeBytes: 16L * 1024 * 1024);

        const int totalSize = 100 * 1024 * 1024;
        var payload = new byte[totalSize];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        using var ms = new MemoryStream(payload);
        var result = await customBackend.UploadAsync(
            "big/custom.bin", ms, ifMatchETag: null, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(result.ETag));
        Assert.Equal(totalSize, result.Size);

        var head = await customBackend.HeadAsync("big\\custom.bin", CancellationToken.None);
        Assert.NotNull(head);
        Assert.Equal(totalSize, head!.Value.Size);

        // ETag of a multipart-uploaded object is <hex>-<partCount>. We assert both
        // the dash (composite ETag marker) and that the suffix matches the number of
        // 16 MiB parts a 100 MiB payload would split into.
        var etag = head.Value.ETag.Trim('"');
        Assert.Contains('-', etag);
        var partCount = etag[(etag.IndexOf('-') + 1)..];
        Assert.Equal("7", partCount);
    }

    private async Task UploadBytesAsync(string s3Key, byte[] payload)
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
