using System.Runtime.CompilerServices;

namespace OSVFS.Diagnostics;

/// <summary>
/// Helpers that wrap an <see cref="IAsyncEnumerable{T}"/> in an
/// <see cref="OperationScope"/> so the resulting span and metrics cover
/// the entire iteration — first call to last yield, including any
/// exception thrown out of <c>MoveNextAsync</c>. Used for S3 List family
/// methods, which are async iterators (and therefore cannot host their own
/// try/catch around <c>yield</c>).
/// </summary>
internal static class TelemetryAsyncEnumerable
{
    /// <summary>
    /// Iterates <paramref name="source"/> within an S3-pipeline
    /// <see cref="OperationScope"/> named <paramref name="operation"/>.
    /// <paramref name="setupTags"/>, when non-null, is invoked once after
    /// the scope starts so callers can apply per-call tags (e.g.
    /// <c>relative.directory</c>) to the span. Exceptions thrown from
    /// <c>MoveNextAsync</c> are recorded on the span and bump the error
    /// counter before being rethrown.
    /// </summary>
    public static async IAsyncEnumerable<T> Instrument<T>(
        string operation,
        IAsyncEnumerable<T> source,
        Action<OperationScope>? setupTags,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var scope = OsvfsTelemetry.StartS3Operation(operation);
        setupTags?.Invoke(scope);

        await using var enumerator = source.GetAsyncEnumerator(ct);
        while (true)
        {
            bool hasMore;
            try
            {
                hasMore = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                scope.Fail(ex);
                throw;
            }

            if (!hasMore) yield break;
            yield return enumerator.Current;
        }
    }
}
