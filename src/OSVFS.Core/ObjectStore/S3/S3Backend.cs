using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using OSVFS.Net;
using System.Net;
using System.Runtime.CompilerServices;

namespace OSVFS.ObjectStore.S3;

/// <summary>
/// AWS SDK-backed implementation of <see cref="IObjectStoreBackend"/>. Owns a single
/// <see cref="AmazonS3Client"/> and a shared <see cref="TransferUtility"/> for the lifetime of
/// the backend.
/// </summary>
internal sealed class S3Backend : IObjectStoreBackend, IDisposable
{
    /// <summary>
    /// S3 caps a single DeleteObjects request at 1000 keys.
    /// </summary>
    private const int DeleteBatchLimit = 1000;

    /// <summary>
    /// Default for streams routed through TransferUtility's multipart path. Picked
    /// to be well above the S3 5 MiB minimum part size so the multipart overhead
    /// is worth paying. Used when the host does not pass an explicit override.
    /// </summary>
    public const long DefaultMultipartThresholdBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Default per-part size for multipart uploads (5 MiB — the S3 minimum). Used
    /// when the host does not pass an explicit override.
    /// </summary>
    public const long DefaultMultipartPartSizeBytes = 5L * 1024 * 1024;

    /// <summary>
    /// Smallest per-part size accepted by S3. Parts smaller than this fail the
    /// CompleteMultipartUpload call (the last part is exempt).
    /// </summary>
    public const long MinMultipartPartSizeBytes = 5L * 1024 * 1024;

    /// <summary>
    /// Largest per-part size accepted by S3. Parts above this exceed the
    /// single-part 5 GiB ceiling.
    /// </summary>
    public const long MaxMultipartPartSizeBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Hard cap on the number of parts a single multipart upload may have. S3
    /// rejects uploads with more parts at CompleteMultipartUpload.
    /// </summary>
    public const int MaxMultipartPartCount = 10_000;

    private readonly string bucketName;

    private readonly string keyPrefix;

    private readonly long multipartPartSize;

    private readonly AmazonS3Client client;

    private readonly TransferUtility transferUtility;

    /// <summary>
    /// Optional throttle applied to upload payload streams. Owned by the backend.
    /// </summary>
    private readonly IRateLimiter? upLimiter;

    /// <summary>
    /// Optional throttle applied to download response streams. Owned by the backend.
    /// </summary>
    private readonly IRateLimiter? downLimiter;

    /// <summary>
    /// Default attempt count when the host does not pass an explicit override.
    /// Matches the SDK's historical default (one initial attempt + two retries).
    /// </summary>
    public const int DefaultRetryMaxAttempts = 3;

    /// <summary>
    /// Creates a backend bound to <paramref name="bucketName"/>. <paramref name="endpointUrl"/>
    /// switches the client into path-style addressing (LocalStack/MinIO); <paramref name="region"/>
    /// drives request signing; <paramref name="credentials"/> short-circuits the SDK chain.
    /// <paramref name="upLimiter"/> / <paramref name="downLimiter"/> apply per-direction
    /// bandwidth ceilings; either may be null to disable that direction.
    /// <paramref name="multipartThresholdBytes"/> / <paramref name="multipartPartSizeBytes"/>
    /// override the multipart routing knobs; null falls back to the defaults.
    /// <paramref name="retryMaxAttempts"/> sets the total attempt count the SDK
    /// will make for transient failures (initial + retries). Null falls back to
    /// <see cref="DefaultRetryMaxAttempts"/>; <c>1</c> disables retries.
    /// </summary>
    public S3Backend(
        string bucketName,
        string? endpointUrl = null,
        string? keyPrefix = null,
        string? region = null,
        AwsCredentialSource? credentials = null,
        IRateLimiter? upLimiter = null,
        IRateLimiter? downLimiter = null,
        long? multipartThresholdBytes = null,
        long? multipartPartSizeBytes = null,
        int? retryMaxAttempts = null)
    {
        this.bucketName = bucketName;
        this.keyPrefix = KeyPath.NormalizeKeyPrefix(keyPrefix);
        this.upLimiter = upLimiter;
        this.downLimiter = downLimiter;
        var threshold = multipartThresholdBytes ?? DefaultMultipartThresholdBytes;
        multipartPartSize = multipartPartSizeBytes ?? DefaultMultipartPartSizeBytes;
        var attempts = retryMaxAttempts ?? DefaultRetryMaxAttempts;
        client = CreateClient(endpointUrl, region, credentials, attempts);
        // Share a single TransferUtility per backend: it's documented thread-safe, holds no
        // upload-specific state, and disposes only its internally-created client (not ours).
        transferUtility = new TransferUtility(client, new TransferUtilityConfig
        {
            MinSizeBeforePartUpload = threshold,
        });
    }

    /// <summary>
    /// Disposes the shared TransferUtility, underlying S3 client, and any limiters
    /// the backend owns.
    /// </summary>
    public void Dispose()
    {
        transferUtility.Dispose();
        client.Dispose();
        (upLimiter as IDisposable)?.Dispose();
        (downLimiter as IDisposable)?.Dispose();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectInfo> ListAsync(
        string relativeDirectory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var fullPrefix = KeyPath.FullPrefix(keyPrefix, relativeDirectory);
        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = fullPrefix,
            Delimiter = "/",
        };

        await foreach (var response in ListPagesAsync(request, ct).ConfigureAwait(false))
        {
            if (response.CommonPrefixes is { } commonPrefixes)
            {
                foreach (var common in commonPrefixes)
                {
                    var name = common[fullPrefix.Length..].TrimEnd('/');
                    if (name.Length == 0) continue;
                    yield return CreateDirectoryInfo(common);
                }
            }

            if (response.S3Objects is { } s3Objects)
            {
                foreach (var obj in s3Objects)
                {
                    if (string.IsNullOrEmpty(obj.Key) || obj.Key.EndsWith('/')) continue;
                    if (obj.Key.Length == fullPrefix.Length) continue;
                    yield return CreateFileInfo(obj);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectInfo> ListAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            // When a prefix is configured, only list objects under it; the rest of the bucket
            // is intentionally invisible to the virtualization root.
            Prefix = keyPrefix.Length > 0 ? keyPrefix : null,
        };

        await foreach (var obj in ListFilesAsync(request, ct).ConfigureAwait(false))
        {
            yield return obj;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectInfo> ListRecursiveAsync(
        string relativeDirectory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = KeyPath.FullPrefix(keyPrefix, relativeDirectory),
        };

        await foreach (var obj in ListFilesAsync(request, ct).ConfigureAwait(false))
        {
            yield return obj;
        }
    }

    /// <inheritdoc/>
    public async Task<BucketVersioningStatus> GetBucketVersioningStatusAsync(CancellationToken ct)
    {
        var resp = await client.GetBucketVersioningAsync(new GetBucketVersioningRequest
        {
            BucketName = bucketName,
        }, ct).ConfigureAwait(false);

        // S3 returns no Status element until versioning has ever been configured. Anything
        // other than the explicit "Enabled" state (never configured, or suspended after
        // having been enabled) is collapsed into NotEnabled — the only state we care about
        // for the startup safety check is "is versioning actively protecting this bucket".
        var status = resp.VersioningConfig?.Status?.Value;
        return status == "Enabled" ? BucketVersioningStatus.Enabled : BucketVersioningStatus.NotEnabled;
    }

    /// <inheritdoc/>
    public async Task<ObjectInfo?> HeadAsync(string relativePath, CancellationToken ct)
    {
        var relKey = KeyPath.ToObjectKey(relativePath);
        if (relKey.Length == 0)
        {
            return new ObjectInfo(string.Empty, string.Empty, 0, default, string.Empty, IsDirectory: true);
        }

        var fullKey = KeyPath.FullKey(keyPrefix, relKey);

        try
        {
            var resp = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucketName,
                Key = fullKey,
            }, ct).ConfigureAwait(false);

            return new ObjectInfo(
                Key: relKey,
                RelativePath: KeyPath.ToRelativePath(relKey),
                Size: resp.ContentLength,
                LastModified: resp.LastModified ?? default,
                ETag: resp.ETag ?? string.Empty,
                IsDirectory: false,
                UserMetadata: ExtractUserMetadata(resp.Metadata));
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            var dirPrefix = fullKey.EndsWith('/') ? fullKey : fullKey + '/';
            var listResp = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucketName,
                Prefix = dirPrefix,
                MaxKeys = 1,
            }, ct).ConfigureAwait(false);

            if ((listResp.S3Objects?.Count ?? 0) > 0 || (listResp.CommonPrefixes?.Count ?? 0) > 0)
            {
                return new ObjectInfo(
                    Key: relKey,
                    RelativePath: KeyPath.ToRelativePath(relKey),
                    Size: 0,
                    LastModified: default,
                    ETag: string.Empty,
                    IsDirectory: true);
            }
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task ReadRangeAsync(
        string relativePath, long offset, long length, Stream destination, CancellationToken ct)
    {
        if (length == 0) return;

        using var resp = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucketName,
            Key = KeyPath.FullKey(keyPrefix, KeyPath.ToObjectKey(relativePath)),
            ByteRange = new ByteRange(offset, offset + length - 1),
        }, ct).ConfigureAwait(false);
        // Wrap the response body in a rate-limited view when a download ceiling is configured;
        // CopyToAsync then only pulls bytes as fast as the limiter releases them.
        var source = downLimiter is null
            ? resp.ResponseStream
            : new RateLimitedStream(resp.ResponseStream, downLimiter);
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<UploadResult> UploadAsync(
        string relativePath,
        Stream content,
        string? ifMatchETag,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? userMetadata = null)
    {
        var relKey = KeyPath.ToObjectKey(relativePath);
        if (string.IsNullOrEmpty(relKey))
        {
            throw new ArgumentException("Cannot upload to empty key.", nameof(relativePath));
        }

        // Validate the user-metadata size up front so the operator gets a clear error
        // instead of S3's opaque "metadata headers exceed maximum" 400 at the end of
        // a (potentially multi-GiB) upload.
        var normalizedMetadata = ObjectStore.UserMetadata.Normalize(userMetadata);
        ObjectStore.UserMetadata.EnsureWithinSizeLimit(normalizedMetadata);

        var fullKey = KeyPath.FullKey(keyPrefix, relKey);
        // Wrap the upload payload so the SDK's pulls (single PUT or multipart workers) are
        // paced by the shared limiter. The wrapper preserves CanSeek/Length, so TransferUtility
        // can still pick the multipart vs single-PUT path correctly.
        var paced = upLimiter is null ? content : new RateLimitedStream(content, upLimiter);
        // IfMatch lives on PutObject; on a multipart upload preconditions move to Complete
        // and behave differently. Honor IfMatch on the simple path; let TransferUtility
        // handle every other case (it auto-splits at MinSizeBeforePartUpload, parallelizes
        // parts, and aborts cleanly on failure).
        return string.IsNullOrEmpty(ifMatchETag)
            ? await MultiPartPutAsync(fullKey, paced, normalizedMetadata, ct).ConfigureAwait(false)
            : await SinglePutAsync(fullKey, paced, ifMatchETag, normalizedMetadata, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Single-shot PutObject path that honors the IfMatch precondition.
    /// </summary>
    private async Task<UploadResult> SinglePutAsync(
        string fullKey,
        Stream content,
        string ifMatchETag,
        IReadOnlyDictionary<string, string> userMetadata,
        CancellationToken ct)
    {
        var request = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = fullKey,
            InputStream = content,
            AutoCloseStream = false,
            IfMatch = ifMatchETag,
        };
        ApplyUserMetadata(request.Metadata, userMetadata);

        var resp = await client.PutObjectAsync(request, ct).ConfigureAwait(false);

        return new(
            ETag: resp.ETag ?? string.Empty,
            VersionId: resp.VersionId ?? string.Empty,
            // PutObjectResponse carries no payload size — fall back to the stream length.
            Size: content.CanSeek ? content.Length : 0L,
            LastModified: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// TransferUtility-driven path that auto-splits at the multipart threshold and
    /// parallelizes parts.
    /// </summary>
    private async Task<UploadResult> MultiPartPutAsync(
        string key,
        Stream content,
        IReadOnlyDictionary<string, string> userMetadata,
        CancellationToken ct)
    {
        var request = new TransferUtilityUploadRequest
        {
            BucketName = bucketName,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            PartSize = multipartPartSize,
        };
        ApplyUserMetadata(request.Metadata, userMetadata);

        var resp = await transferUtility.UploadWithResponseAsync(request, ct).ConfigureAwait(false);

        return new(
            ETag: resp.ETag ?? string.Empty,
            VersionId: resp.VersionId ?? string.Empty,
            // resp.Size is null on the small-file path that delegates to PutObject; fall back
            // to the stream length so the watcher's snapshot stays consistent.
            Size: resp.Size ?? (content.CanSeek ? content.Length : 0L),
            LastModified: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Copies a normalized user-metadata map into an SDK
    /// <see cref="MetadataCollection"/>. The SDK adds the <c>x-amz-meta-</c>
    /// prefix on the wire, so the keys we hand over must be the bare names.
    /// </summary>
    private static void ApplyUserMetadata(
        MetadataCollection target, IReadOnlyDictionary<string, string> userMetadata)
    {
        if (userMetadata.Count == 0) return;
        foreach (var (name, value) in userMetadata)
        {
            target.Add(name, value);
        }
    }

    /// <summary>
    /// Materializes an <see cref="ObjectInfo.UserMetadata"/> map from a HEAD
    /// response. The SDK exposes user metadata via <c>metadata["x-amz-meta-foo"]</c>,
    /// so we strip the prefix and lowercase to match the wire form.
    /// </summary>
    private static Dictionary<string, string>? ExtractUserMetadata(MetadataCollection? metadata)
    {
        if (metadata is null || metadata.Keys.Count == 0) return null;

        var dict = new Dictionary<string, string>(metadata.Keys.Count, StringComparer.Ordinal);
        foreach (var rawKey in metadata.Keys)
        {
            if (string.IsNullOrEmpty(rawKey)) continue;
            // The SDK stores user metadata with the "x-amz-meta-" prefix included; strip it
            // so callers see the same names they handed to UploadAsync.
            const string Prefix = "x-amz-meta-";
            var name = rawKey.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                ? rawKey[Prefix.Length..]
                : rawKey;
            if (name.Length == 0) continue;
            dict[name.ToLowerInvariant()] = metadata[rawKey] ?? string.Empty;
        }
        return dict.Count == 0 ? null : dict;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        var relKey = KeyPath.ToObjectKey(relativePath);
        if (string.IsNullOrEmpty(relKey)) return;

        try
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucketName,
                Key = KeyPath.FullKey(keyPrefix, relKey),
            }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone — treat as success.
        }
    }

    /// <inheritdoc/>
    public async Task DeletePrefixAsync(string relativeDirectory, CancellationToken ct)
    {
        var relPrefix = KeyPath.NormalizePrefix(relativeDirectory);
        if (relPrefix.Length == 0) return;

        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = keyPrefix + relPrefix,
        };

        await BatchDeleteKeysAsync(EnumerateKeysAsync(request, ct), ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RenameAsync(string oldRelativePath, string newRelativePath, CancellationToken ct)
    {
        var oldRelKey = KeyPath.ToObjectKey(oldRelativePath);
        var newRelKey = KeyPath.ToObjectKey(newRelativePath);
        if (string.IsNullOrEmpty(oldRelKey) || string.IsNullOrEmpty(newRelKey)) return;
        if (string.Equals(oldRelKey, newRelKey, StringComparison.Ordinal)) return;

        var oldKey = KeyPath.FullKey(keyPrefix, oldRelKey);
        var newKey = KeyPath.FullKey(keyPrefix, newRelKey);

        // Atomic-replace pattern (editor writes to a temp key, then renames it
        // over an existing target) would otherwise lose the destination's
        // x-amz-meta-* because the default CopyObject directive copies the
        // source's (empty) metadata. Carry the destination's existing user
        // metadata across the replace so the round-trip stays intact.
        var preservedMetadata = await TryHeadUserMetadataAsync(newKey, ct).ConfigureAwait(false);

        await CopyObjectAsync(oldKey, newKey, ct, preservedMetadata).ConfigureAwait(false);

        await client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = bucketName,
            Key = oldKey,
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RenamePrefixAsync(
        string oldRelativeDirectory, string newRelativeDirectory, CancellationToken ct)
    {
        var oldRelPrefix = KeyPath.NormalizePrefix(oldRelativeDirectory);
        var newRelPrefix = KeyPath.NormalizePrefix(newRelativeDirectory);
        if (oldRelPrefix.Length == 0 || newRelPrefix.Length == 0) return;
        if (string.Equals(oldRelPrefix, newRelPrefix, StringComparison.Ordinal)) return;

        var oldFullPrefix = keyPrefix + oldRelPrefix;
        var newFullPrefix = keyPrefix + newRelPrefix;

        var keys = new List<string>();
        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = oldFullPrefix,
        };

        await foreach (var key in EnumerateKeysAsync(request, ct).ConfigureAwait(false))
        {
            keys.Add(key);
            await CopyObjectAsync(key, newFullPrefix + key[oldFullPrefix.Length..], ct).ConfigureAwait(false);
        }

        await BatchDeleteKeysAsync(keys, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Iterates ListObjectsV2 pages on this backend's client. Delegates the
    /// continuation-token plumbing to <see cref="S3Pagination.ListPagesAsync"/>.
    /// </summary>
    private IAsyncEnumerable<ListObjectsV2Response> ListPagesAsync(
        ListObjectsV2Request request,
        CancellationToken ct) =>
        S3Pagination.ListPagesAsync(client.ListObjectsV2Async, request, ct);

    /// <summary>
    /// Yields one <see cref="ObjectInfo"/> per real (non-marker) object across all
    /// pages of <paramref name="request"/>.
    /// </summary>
    private async IAsyncEnumerable<ObjectInfo> ListFilesAsync(
        ListObjectsV2Request request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var response in ListPagesAsync(request, ct).ConfigureAwait(false))
        {
            if (response.S3Objects is not { } s3Objects) continue;
            foreach (var obj in s3Objects)
            {
                if (string.IsNullOrEmpty(obj.Key) || obj.Key.EndsWith('/')) continue;
                var relKey = KeyPath.StripPrefix(keyPrefix, obj.Key);
                if (relKey.Length == 0) continue;
                yield return CreateFileInfo(obj);
            }
        }
    }

    /// <summary>
    /// Yields the raw (full) key of every object across all pages of <paramref name="request"/>.
    /// </summary>
    private async IAsyncEnumerable<string> EnumerateKeysAsync(
        ListObjectsV2Request request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var response in ListPagesAsync(request, ct).ConfigureAwait(false))
        {
            if (response.S3Objects is not { } s3Objects) continue;
            foreach (var obj in s3Objects)
            {
                if (!string.IsNullOrEmpty(obj.Key)) yield return obj.Key;
            }
        }
    }

    /// <summary>
    /// Builds an <see cref="ObjectInfo"/> file entry from an <see cref="S3Object"/>,
    /// stripping the linked prefix and tolerating null SDK fields.
    /// </summary>
    private ObjectInfo CreateFileInfo(S3Object obj)
    {
        var relKey = KeyPath.StripPrefix(keyPrefix, obj.Key);
        return new ObjectInfo(
            Key: relKey,
            RelativePath: KeyPath.ToRelativePath(relKey),
            Size: obj.Size ?? 0,
            LastModified: obj.LastModified ?? default,
            ETag: obj.ETag ?? string.Empty,
            IsDirectory: false);
    }

    /// <summary>
    /// Builds an <see cref="ObjectInfo"/> directory entry from a CommonPrefixes
    /// string returned by ListObjectsV2 with a delimiter.
    /// </summary>
    private ObjectInfo CreateDirectoryInfo(string commonPrefix)
    {
        var relKey = KeyPath.StripPrefix(keyPrefix, commonPrefix.TrimEnd('/'));
        return new ObjectInfo(
            Key: relKey,
            RelativePath: KeyPath.ToRelativePath(relKey),
            Size: 0,
            LastModified: default,
            ETag: string.Empty,
            IsDirectory: true);
    }

    /// <summary>
    /// Issues a single CopyObject within the same bucket. When
    /// <paramref name="metadataOverride"/> is non-null the request switches to
    /// REPLACE so the destination ends up with that exact metadata instead of
    /// inheriting the source's; null leaves the SDK default (COPY from source).
    /// </summary>
    private Task<CopyObjectResponse> CopyObjectAsync(
        string sourceKey,
        string destinationKey,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadataOverride = null)
    {
        var request = new CopyObjectRequest
        {
            SourceBucket = bucketName,
            SourceKey = sourceKey,
            DestinationBucket = bucketName,
            DestinationKey = destinationKey,
        };
        if (metadataOverride is { Count: > 0 })
        {
            request.MetadataDirective = S3MetadataDirective.REPLACE;
            ApplyUserMetadata(request.Metadata, metadataOverride);
        }
        return client.CopyObjectAsync(request, ct);
    }

    /// <summary>
    /// HEAD-only fetch of user metadata for a full bucket key. Returns null
    /// when the object is missing or carries no <c>x-amz-meta-*</c>; any other
    /// error is rethrown so the caller can decide whether to abort.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> TryHeadUserMetadataAsync(
        string fullKey, CancellationToken ct)
    {
        try
        {
            var resp = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucketName,
                Key = fullKey,
            }, ct).ConfigureAwait(false);
            return ExtractUserMetadata(resp.Metadata);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Streams keys into 1000-key DeleteObjects batches.
    /// </summary>
    private async Task BatchDeleteKeysAsync(IAsyncEnumerable<string> keys, CancellationToken ct)
    {
        var batch = new List<KeyVersion>(capacity: DeleteBatchLimit);
        await foreach (var key in keys.ConfigureAwait(false))
        {
            await AddAndMaybeFlushAsync(batch, key, ct).ConfigureAwait(false);
        }
        if (batch.Count > 0)
        {
            await FlushDeleteBatchAsync(batch, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Synchronous-source overload of <see cref="BatchDeleteKeysAsync(IAsyncEnumerable{string}, CancellationToken)"/>.
    /// </summary>
    private async Task BatchDeleteKeysAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        var batch = new List<KeyVersion>(capacity: DeleteBatchLimit);
        foreach (var key in keys)
        {
            await AddAndMaybeFlushAsync(batch, key, ct).ConfigureAwait(false);
        }
        if (batch.Count > 0)
        {
            await FlushDeleteBatchAsync(batch, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Appends a key to the batch and flushes when the S3 1000-key cap is reached.
    /// </summary>
    private async Task AddAndMaybeFlushAsync(List<KeyVersion> batch, string key, CancellationToken ct)
    {
        batch.Add(new KeyVersion { Key = key });
        if (batch.Count == DeleteBatchLimit)
        {
            await FlushDeleteBatchAsync(batch, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a single DeleteObjects request for the accumulated batch and clears it.
    /// </summary>
    private async Task FlushDeleteBatchAsync(List<KeyVersion> batch, CancellationToken ct)
    {
        await client.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = bucketName,
            Objects = batch,
            Quiet = true,
        }, ct).ConfigureAwait(false);
        batch.Clear();
    }

    /// <summary>
    /// Builds the underlying S3 client. Endpoint overrides flip on path-style
    /// addressing and relax the v4 SDK's checksum negotiation for S3-compatible servers.
    /// Retry behavior is delegated to the SDK pipeline: <see cref="RequestRetryMode.Adaptive"/>
    /// uses the SDK's adaptive client-side throttling (token-bucket) policy and
    /// <see cref="ClientConfig.MaxErrorRetry"/> caps the number of retries the
    /// SDK performs on transient failures (5xx, throttling, network errors).
    /// </summary>
    private static AmazonS3Client CreateClient(
        string? endpointUrl,
        string? region,
        AwsCredentialSource? credentials,
        int retryMaxAttempts)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = endpointUrl is not null,
            // Adaptive enables the SDK's client-side throttling token bucket on top of
            // the standard retry classifier (5xx / RequestTimeout / Throttling / SlowDown
            // / network errors). 4xx responses bypass the retry handler.
            RetryMode = RequestRetryMode.Adaptive,
            // MaxErrorRetry counts retries AFTER the first attempt, so the total attempt
            // budget (initial + retries) is MaxErrorRetry + 1. Clamp to >= 0 so a caller
            // passing 1 (= retries disabled) cannot underflow the SDK's expected range.
            MaxErrorRetry = Math.Max(0, retryMaxAttempts - 1),
        };
        if (!string.IsNullOrEmpty(endpointUrl))
        {
            config.ServiceURL = endpointUrl;
            // AWSSDK v4 defaults to flexible-checksum (CRC32) on every PUT and on multipart
            // parts. Real S3 validates these correctly, but S3-compatible servers (LocalStack,
            // MinIO) don't always implement the composite-checksum check on Complete and reject
            // the upload with "Checksum Type mismatch". Only relax for endpoint-override mode.
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }
        if (!string.IsNullOrEmpty(region))
        {
            // ServiceURL takes precedence over RegionEndpoint when both are set, so honoring
            // an explicit --region alongside --endpoint-url still routes traffic to the override
            // while letting the region drive request signing.
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        }
        if (credentials is null)
        {
            return new AmazonS3Client(config);
        }
        // Materialize once: the static branch builds a fresh BasicAWS/SessionAWS pair,
        // the SDK branch returns the refreshing wrapper supplied by the caller (e.g. a
        // ProcessAWSCredentials around `aws configure export-credentials`).
        return new AmazonS3Client(credentials.Materialize(), config);
    }
}
