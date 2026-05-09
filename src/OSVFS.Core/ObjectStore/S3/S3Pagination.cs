using Amazon.S3.Model;
using System.Runtime.CompilerServices;

namespace OSVFS.ObjectStore.S3;

/// <summary>
/// Helpers for iterating ListObjectsV2 result pages by threading the
/// <c>NextContinuationToken</c> back into successive requests until the bucket is exhausted.
/// </summary>
internal static class S3Pagination
{
    /// <summary>
    /// Yields each <see cref="ListObjectsV2Response"/> page in sequence, threading the
    /// continuation token automatically and short-circuiting at page boundaries when
    /// <paramref name="ct"/> is cancelled. The supplied <paramref name="request"/> is
    /// mutated in place and should not be reused after enumeration.
    /// </summary>
    public static async IAsyncEnumerable<ListObjectsV2Response> ListPagesAsync(
        Func<ListObjectsV2Request, CancellationToken, Task<ListObjectsV2Response>> listObjectsAsync,
        ListObjectsV2Request request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        do
        {
            // Stop before issuing the next page request when cancellation has already been
            // signalled — guarantees a clean break on page boundaries even if the SDK call
            // would otherwise be in flight.
            ct.ThrowIfCancellationRequested();
            var response = await listObjectsAsync(request, ct).ConfigureAwait(false);
            yield return response;
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (!string.IsNullOrEmpty(request.ContinuationToken));
    }
}
