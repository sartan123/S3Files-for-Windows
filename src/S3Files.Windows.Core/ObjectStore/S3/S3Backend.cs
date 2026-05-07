using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using System.Net;
using System.Runtime.CompilerServices;

namespace S3Files.Windows.ObjectStore.S3;

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
    /// Streams at or above this size are routed through TransferUtility's multipart
    /// path. Picked to be well above the S3 5 MiB minimum part size so the multipart overhead
    /// is worth paying.
    /// </summary>
    public const long MultipartThresholdBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Per-part size for multipart uploads. Must be ≥ 5 MiB to satisfy the S3
    /// minimum; the last part is allowed to be smaller.
    /// </summary>
    public const long MultipartPartSizeBytes = 5L * 1024 * 1024;

    private readonly string bucketName;

    private readonly string keyPrefix;

    private readonly AmazonS3Client client;

    private readonly TransferUtility transferUtility;

    /// <summary>
    /// Creates a backend bound to <paramref name="bucketName"/>. <paramref name="endpointUrl"/>
    /// switches the client into path-style addressing (LocalStack/MinIO); <paramref name="region"/>
    /// drives request signing.
    /// </summary>
    public S3Backend(string bucketName, string? endpointUrl = null, string? keyPrefix = null, string? region = null)
    {
        this.bucketName = bucketName;
        this.keyPrefix = KeyPath.NormalizeKeyPrefix(keyPrefix);
        client = CreateClient(endpointUrl, region);
        // Share a single TransferUtility per backend: it's documented thread-safe, holds no
        // upload-specific state, and disposes only its internally-created client (not ours).
        transferUtility = new TransferUtility(client, new TransferUtilityConfig
        {
            MinSizeBeforePartUpload = MultipartThresholdBytes,
        });
    }

    /// <summary>
    /// Disposes the shared TransferUtility and underlying S3 client.
    /// </summary>
    public void Dispose()
    {
        transferUtility.Dispose();
        client.Dispose();
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
                IsDirectory: false);
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
        await resp.ResponseStream.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<UploadResult> UploadAsync(
        string relativePath, Stream content, string? ifMatchETag, CancellationToken ct)
    {
        var relKey = KeyPath.ToObjectKey(relativePath);
        if (string.IsNullOrEmpty(relKey))
        {
            throw new ArgumentException("Cannot upload to empty key.", nameof(relativePath));
        }

        var fullKey = KeyPath.FullKey(keyPrefix, relKey);
        // IfMatch lives on PutObject; on a multipart upload preconditions move to Complete
        // and behave differently. Honor IfMatch on the simple path; let TransferUtility
        // handle every other case (it auto-splits at MinSizeBeforePartUpload, parallelizes
        // parts, and aborts cleanly on failure).
        return string.IsNullOrEmpty(ifMatchETag)
            ? await MultiPartPutAsync(fullKey, content, ct).ConfigureAwait(false)
            : await SinglePutAsync(fullKey, content, ifMatchETag, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Single-shot PutObject path that honors the IfMatch precondition.
    /// </summary>
    private async Task<UploadResult> SinglePutAsync(
        string fullKey, Stream content, string ifMatchETag, CancellationToken ct)
    {
        var resp = await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucketName,
            Key = fullKey,
            InputStream = content,
            AutoCloseStream = false,
            IfMatch = ifMatchETag,
        }, ct).ConfigureAwait(false);

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
        string key, Stream content, CancellationToken ct)
    {
        var resp = await transferUtility.UploadWithResponseAsync(new TransferUtilityUploadRequest
        {
            BucketName = bucketName,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            PartSize = MultipartPartSizeBytes,
        }, ct).ConfigureAwait(false);

        return new(
            ETag: resp.ETag ?? string.Empty,
            VersionId: resp.VersionId ?? string.Empty,
            // resp.Size is null on the small-file path that delegates to PutObject; fall back
            // to the stream length so the watcher's snapshot stays consistent.
            Size: resp.Size ?? (content.CanSeek ? content.Length : 0L),
            LastModified: DateTimeOffset.UtcNow);
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

        await CopyObjectAsync(oldKey, newKey, ct).ConfigureAwait(false);

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
    /// Iterates ListObjectsV2 pages, threading the continuation token automatically.
    /// The caller's <paramref name="request"/> is mutated in place and should not be reused
    /// after enumeration.
    /// </summary>
    private async IAsyncEnumerable<ListObjectsV2Response> ListPagesAsync(
        ListObjectsV2Request request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        do
        {
            var response = await client.ListObjectsV2Async(request, ct).ConfigureAwait(false);
            yield return response;
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (!string.IsNullOrEmpty(request.ContinuationToken));
    }

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
    /// Issues a single CopyObject within the same bucket.
    /// </summary>
    private Task<CopyObjectResponse> CopyObjectAsync(
        string sourceKey, string destinationKey, CancellationToken ct) =>
        client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucketName,
            SourceKey = sourceKey,
            DestinationBucket = bucketName,
            DestinationKey = destinationKey,
        }, ct);

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
    /// </summary>
    private static AmazonS3Client CreateClient(string? endpointUrl, string? region)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = endpointUrl is not null,
        };
        if (!string.IsNullOrEmpty(endpointUrl))
        {
            config.ServiceURL = endpointUrl;
            // AWSSDK v4 defaults to flexible-checksum (CRC32) on every PUT and on multipart
            // parts. Real S3 validates these correctly, but S3-compatible servers (LocalStack,
            // MinIO) don't always implement the composite-checksum check on Complete and reject
            // the upload with "Checksum Type mismatch". Only relax for endpoint-override mode.
            config.RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED;
        }
        if (!string.IsNullOrEmpty(region))
        {
            // ServiceURL takes precedence over RegionEndpoint when both are set, so honoring
            // an explicit --region alongside --endpoint-url still routes traffic to the override
            // while letting the region drive request signing.
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        }
        return new AmazonS3Client(config);
    }
}
