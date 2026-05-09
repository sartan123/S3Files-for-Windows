using Amazon.S3.Model;
using OSVFS.ObjectStore.S3;
using Xunit;

namespace OSVFS.Core.UnitTests.ObjectStore.S3;

public class S3PaginationTests
{
    [Fact]
    public async Task ListPages_threads_ContinuationToken_across_three_pages()
    {
        // Three canned pages: the first two carry NextContinuationToken so the helper
        // should re-issue the request with the matching ContinuationToken; the third
        // returns null/empty to terminate the loop.
        var pages = new[]
        {
            BuildPage(token: "tok-1", keys: ["a/1.txt", "a/2.txt"]),
            BuildPage(token: "tok-2", keys: ["b/3.txt"]),
            BuildPage(token: null, keys: ["c/4.txt", "c/5.txt"]),
        };

        var observedTokens = new List<string?>();
        var pageIndex = 0;
        Task<ListObjectsV2Response> Fake(ListObjectsV2Request request, CancellationToken ct)
        {
            observedTokens.Add(request.ContinuationToken);
            return Task.FromResult(pages[pageIndex++]);
        }

        var request = new ListObjectsV2Request { BucketName = "bucket" };
        var keys = new List<string>();
        await foreach (var page in S3Pagination.ListPagesAsync(Fake, request, CancellationToken.None))
        {
            foreach (var obj in page.S3Objects)
            {
                keys.Add(obj.Key);
            }
        }

        // First request must have no continuation token; subsequent requests must echo
        // the previous response's NextContinuationToken verbatim.
        Assert.Equal(new string?[] { null, "tok-1", "tok-2" }, observedTokens);
        Assert.Equal(["a/1.txt", "a/2.txt", "b/3.txt", "c/4.txt", "c/5.txt"], keys);
        Assert.Equal(3, pageIndex);
    }

    [Fact]
    public async Task ListPages_treats_empty_string_continuation_token_as_terminal()
    {
        // S3 may return an empty string for NextContinuationToken on the final page;
        // the helper must not loop forever in that case.
        var pages = new[]
        {
            BuildPage(token: string.Empty, keys: ["only.txt"]),
        };

        var calls = 0;
        Task<ListObjectsV2Response> Fake(ListObjectsV2Request request, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(pages[0]);
        }

        var request = new ListObjectsV2Request { BucketName = "bucket" };
        await foreach (var _ in S3Pagination.ListPagesAsync(Fake, request, CancellationToken.None))
        {
            // drain
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ListPages_stops_at_page_boundary_when_token_already_cancelled()
    {
        // Cancellation requested before iteration starts: the helper must throw without
        // ever calling the underlying client, so a stalled SDK call cannot leak.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var calls = 0;
        Task<ListObjectsV2Response> Fake(ListObjectsV2Request request, CancellationToken ct)
        {
            calls++;
            return Task.FromResult(BuildPage(token: null, keys: ["x.txt"]));
        }

        var request = new ListObjectsV2Request { BucketName = "bucket" };

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in S3Pagination.ListPagesAsync(Fake, request, cts.Token))
            {
                // drain
            }
        });

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ListPages_stops_between_pages_when_cancelled_after_first_page()
    {
        // Cancellation arriving between pages must short-circuit before the helper
        // issues the next ListObjectsV2 call — the boundary stop the issue specifies.
        using var cts = new CancellationTokenSource();
        var pages = new[]
        {
            BuildPage(token: "tok-1", keys: ["a.txt"]),
            BuildPage(token: null, keys: ["b.txt"]),
        };

        var calls = 0;
        Task<ListObjectsV2Response> Fake(ListObjectsV2Request request, CancellationToken ct)
        {
            calls++;
            // Trip cancellation right after handing back the first page; the helper
            // should detect this on the next loop iteration before calling us again.
            cts.Cancel();
            return Task.FromResult(pages[calls - 1]);
        }

        var request = new ListObjectsV2Request { BucketName = "bucket" };
        var seen = new List<string>();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var page in S3Pagination.ListPagesAsync(Fake, request, cts.Token))
            {
                foreach (var obj in page.S3Objects)
                {
                    seen.Add(obj.Key);
                }
            }
        });

        Assert.Equal(1, calls);
        Assert.Equal(["a.txt"], seen);
    }

    private static ListObjectsV2Response BuildPage(string? token, IReadOnlyList<string> keys)
    {
        var response = new ListObjectsV2Response
        {
            NextContinuationToken = token,
            S3Objects = [],
        };
        foreach (var k in keys)
        {
            response.S3Objects.Add(new S3Object { Key = k, Size = 0 });
        }
        return response;
    }
}
