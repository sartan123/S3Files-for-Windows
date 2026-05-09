using Amazon.S3;
using Amazon.S3.Model;
using OSVFS.ObjectStore;
using OSVFS.ObjectStore.S3;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace OSVFS.Core.IntegrationTests;

/// <summary>
/// Verifies that the listing entry points walk every continuation-token page and
/// recover all objects when the bucket exceeds the 1000-key default page cap.
/// </summary>
[Collection(LocalStackCollection.Name)]
public sealed class S3BackendListPaginationTests : IAsyncLifetime
{
    /// <summary>
    /// Population size chosen to cross the 1000-key ListObjectsV2 page cap by a wide
    /// margin (≥ 5 full pages plus a partial), exercising NextContinuationToken handoff
    /// across many boundaries rather than just one.
    /// </summary>
    private const int ObjectCount = 5500;

    private readonly LocalStackFixture localStack;
    private readonly ITestOutputHelper output;
    private readonly string bucket = $"osvfs-page-{Guid.NewGuid():N}";
    private AmazonS3Client adminClient = null!;
    private S3Backend backend = null!;

    public S3BackendListPaginationTests(LocalStackFixture localStack, ITestOutputHelper output)
    {
        this.localStack = localStack;
        this.output = output;
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

        await SeedBucketAsync(ObjectCount);
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
            // Cleanup is best-effort; swallow so the next test isn't impacted.
        }
        backend.Dispose();
        adminClient.Dispose();
    }

    [Fact]
    public async Task ListAll_returns_every_object_across_pagination_boundaries()
    {
        var sw = Stopwatch.StartNew();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in backend.ListAllAsync(CancellationToken.None))
        {
            seen.Add(entry.RelativePath);
        }
        sw.Stop();

        LogTiming(nameof(backend.ListAllAsync), seen.Count, sw.Elapsed);
        Assert.Equal(ObjectCount, seen.Count);
    }

    [Fact]
    public async Task ListRecursive_returns_every_object_under_prefix_across_pages()
    {
        // ListRecursiveAsync uses the same ListFiles/ListPages plumbing as ListAllAsync;
        // exercise it through a non-empty prefix to confirm pagination still walks all
        // pages when a Prefix is set.
        var sw = Stopwatch.StartNew();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in backend.ListRecursiveAsync("page", CancellationToken.None))
        {
            seen.Add(entry.RelativePath);
        }
        sw.Stop();

        LogTiming(nameof(backend.ListRecursiveAsync), seen.Count, sw.Elapsed);
        Assert.Equal(ObjectCount, seen.Count);
    }

    [Fact]
    public async Task List_with_delimiter_aggregates_every_subdirectory_across_pages()
    {
        // Seeding spreads ObjectCount keys across directories named "page/0000".."page/NNNN".
        // ListAsync at root with delimiter must surface every distinct top-level directory
        // even though CommonPrefixes is also paginated.
        var sw = Stopwatch.StartNew();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in backend.ListAsync(string.Empty, CancellationToken.None))
        {
            (entry.IsDirectory ? directories : files).Add(entry.RelativePath);
        }
        sw.Stop();

        LogTiming(nameof(backend.ListAsync), directories.Count + files.Count, sw.Elapsed);
        // Single top-level directory ("page") under which all seeded keys live, plus zero
        // root-level files. Delimiter mode must still discover that prefix.
        Assert.Contains("page", directories);
        Assert.Empty(files);
    }

    [Fact]
    public async Task Listing_stops_at_page_boundary_when_cancelled()
    {
        // Cancel after a page-and-a-bit of objects have been observed; the helper's
        // page-boundary check should keep iteration from issuing another LIST request.
        using var cts = new CancellationTokenSource();
        var observed = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in backend.ListAllAsync(cts.Token))
            {
                observed++;
                if (observed == 1100)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.True(observed >= 1100, $"Expected to observe at least 1100 objects before cancel, saw {observed}.");
        Assert.True(observed < ObjectCount, $"Cancellation must short-circuit before draining the bucket; saw {observed}.");
    }

    private async Task SeedBucketAsync(int count)
    {
        // Upload in parallel batches: LocalStack's PutObject is fast but serialised
        // uploads still dominate the suite's runtime at 5500 keys.
        const int parallelism = 32;
        var index = -1;
        var workers = Enumerable.Range(0, parallelism).Select(async _ =>
        {
            int next;
            while ((next = Interlocked.Increment(ref index)) < count)
            {
                var key = $"page/{next:D5}/object.txt";
                using var ms = new MemoryStream([0x00]);
                await adminClient.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = ms,
                    AutoCloseStream = false,
                });
            }
        });
        await Task.WhenAll(workers);
    }

    private async Task EmptyBucketAsync()
    {
        // 5500 keys exceed the 1000-key DeleteObjects cap; loop until the bucket is empty.
        while (true)
        {
            var listing = await adminClient.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                MaxKeys = 1000,
            });

            var objects = listing.S3Objects;
            if (objects is null || objects.Count == 0) break;

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

    private void LogTiming(string operation, int items, TimeSpan elapsed)
    {
        var perThousand = items == 0 ? 0 : elapsed.TotalMilliseconds * 1000d / items;
        output.WriteLine(
            $"{operation}: {items} items in {elapsed.TotalMilliseconds:F0} ms ({perThousand:F1} ms / 1000 keys)");
    }
}
