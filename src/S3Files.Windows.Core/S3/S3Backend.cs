using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using System.Net;
using System.Runtime.CompilerServices;

namespace S3Files.Windows.S3;

internal sealed class S3Backend : IS3Backend, IDisposable
{
    private readonly string bucketName;

    private readonly string keyPrefix;
    
    private readonly AmazonS3Client client;

    private readonly TransferUtility transferUtility;

    public S3Backend(string bucketName, string? endpointUrl = null, string? keyPrefix = null, string? region = null)
    {
        this.bucketName = bucketName;
        this.keyPrefix = S3Util.NormalizeKeyPrefix(keyPrefix);
        client = CreateClient(endpointUrl, region);
        // Share a single TransferUtility per backend: it's documented thread-safe, holds no
        // upload-specific state, and disposes only its internally-created client (not ours).
        transferUtility = new TransferUtility(client, new TransferUtilityConfig
        {
            MinSizeBeforePartUpload = S3Util.MultipartThresholdBytes,
        });
    }

    public void Dispose()
    {
        transferUtility.Dispose();
        client.Dispose();
    }

    public async IAsyncEnumerable<S3ObjectInfo> ListAsync(
        string relativeDirectory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var fullPrefix = S3Util.FullPrefix(keyPrefix, relativeDirectory);
        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = fullPrefix,
            Delimiter = "/",
        };

        do
        {
            var response = await client.ListObjectsV2Async(request, ct).ConfigureAwait(false);

            if (response.CommonPrefixes is { } commonPrefixes)
            {
                foreach (var common in commonPrefixes)
                {
                    var name = common[fullPrefix.Length..].TrimEnd('/');
                    if (name.Length == 0) continue;
                    var relKey = S3Util.StripPrefix(keyPrefix, common.TrimEnd('/'));
                    yield return new S3ObjectInfo(
                        Key: relKey,
                        RelativePath: S3Util.ToRelativePath(relKey),
                        Size: 0,
                        LastModified: default,
                        ETag: string.Empty,
                        IsDirectory: true);
                }
            }

            if (response.S3Objects is { } s3Objects)
            {
                foreach (var obj in s3Objects)
                {
                    if (string.IsNullOrEmpty(obj.Key) || obj.Key.EndsWith('/')) continue;
                    var name = obj.Key[fullPrefix.Length..];
                    if (name.Length == 0) continue;
                    var relKey = S3Util.StripPrefix(keyPrefix, obj.Key);
                    yield return new S3ObjectInfo(
                        Key: relKey,
                        RelativePath: S3Util.ToRelativePath(relKey),
                        Size: obj.Size ?? 0,
                        LastModified: obj.LastModified ?? default,
                        ETag: obj.ETag ?? string.Empty,
                        IsDirectory: false);
                }
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (!string.IsNullOrEmpty(request.ContinuationToken));
    }

    public async IAsyncEnumerable<S3ObjectInfo> ListAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            // When a prefix is configured, only list objects under it; the rest of the bucket
            // is intentionally invisible to the virtualization root.
            Prefix = keyPrefix.Length > 0 ? keyPrefix : null,
        };

        do
        {
            var response = await client.ListObjectsV2Async(request, ct).ConfigureAwait(false);
            if (response.S3Objects is { } s3Objects)
            {
                foreach (var obj in s3Objects)
                {
                    if (string.IsNullOrEmpty(obj.Key) || obj.Key.EndsWith('/')) continue;
                    var relKey = S3Util.StripPrefix(keyPrefix, obj.Key);
                    if (relKey.Length == 0) continue;
                    yield return new S3ObjectInfo(
                        Key: relKey,
                        RelativePath: S3Util.ToRelativePath(relKey),
                        Size: obj.Size ?? 0,
                        LastModified: obj.LastModified ?? default,
                        ETag: obj.ETag ?? string.Empty,
                        IsDirectory: false);
                }
            }
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (!string.IsNullOrEmpty(request.ContinuationToken));
    }

    public async IAsyncEnumerable<S3ObjectInfo> ListRecursiveAsync(
        string relativeDirectory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var fullPrefix = S3Util.FullPrefix(keyPrefix, relativeDirectory);
        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = fullPrefix,
        };

        do
        {
            var response = await client.ListObjectsV2Async(request, ct).ConfigureAwait(false);

            if (response.S3Objects is { } s3Objects)
            {
                foreach (var obj in s3Objects)
                {
                    if (string.IsNullOrEmpty(obj.Key) || obj.Key.EndsWith('/')) continue;
                    var relKey = S3Util.StripPrefix(keyPrefix, obj.Key);
                    if (relKey.Length == 0) continue;
                    yield return new S3ObjectInfo(
                        Key: relKey,
                        RelativePath: S3Util.ToRelativePath(relKey),
                        Size: obj.Size ?? 0,
                        LastModified: obj.LastModified ?? default,
                        ETag: obj.ETag ?? string.Empty,
                        IsDirectory: false);
                }
            }
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (!string.IsNullOrEmpty(request.ContinuationToken));
    }

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

    public async Task<S3ObjectInfo?> HeadAsync(string relativePath, CancellationToken ct)
    {
        var relKey = S3Util.ToS3Key(relativePath);
        if (relKey.Length == 0)
        {
            return new S3ObjectInfo(string.Empty, string.Empty, 0, default, string.Empty, IsDirectory: true);
        }

        var fullKey = S3Util.FullKey(keyPrefix, relKey);

        try
        {
            var resp = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucketName,
                Key = fullKey,
            }, ct).ConfigureAwait(false);

            return new S3ObjectInfo(
                Key: relKey,
                RelativePath: S3Util.ToRelativePath(relKey),
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
                return new S3ObjectInfo(
                    Key: relKey,
                    RelativePath: S3Util.ToRelativePath(relKey),
                    Size: 0,
                    LastModified: default,
                    ETag: string.Empty,
                    IsDirectory: true);
            }
            return null;
        }
    }

    public async Task ReadRangeAsync(
        string relativePath, long offset, long length, Stream destination, CancellationToken ct)
    {
        if (length == 0) return;

        using var resp = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucketName,
            Key = S3Util.FullKey(keyPrefix, S3Util.ToS3Key(relativePath)),
            ByteRange = new ByteRange(offset, offset + length - 1),
        }, ct).ConfigureAwait(false);
        await resp.ResponseStream.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    public async Task<UploadResult> UploadAsync(
        string relativePath, Stream content, string? ifMatchETag, CancellationToken ct)
    {
        var relKey = S3Util.ToS3Key(relativePath);
        if (string.IsNullOrEmpty(relKey))
        {
            throw new ArgumentException("Cannot upload to empty key.", nameof(relativePath));
        }

        var fullKey = S3Util.FullKey(keyPrefix, relKey);
        // IfMatch lives on PutObject; on a multipart upload preconditions move to Complete
        // and behave differently. Honor IfMatch on the simple path; let TransferUtility
        // handle every other case (it auto-splits at MinSizeBeforePartUpload, parallelizes
        // parts, and aborts cleanly on failure).
        if (!string.IsNullOrEmpty(ifMatchETag))
        {
            return await SinglePutAsync(fullKey, content, ifMatchETag, ct).ConfigureAwait(false);
        }
        else
        {
            return await MultiPartPutAsync(fullKey, content, ct).ConfigureAwait(false);
        }
    }

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

        // PutObjectResponse carries no payload size — pass null and let the builder fall
        // back to the stream length.
        return new(
            ETag: resp.ETag ?? string.Empty,
            VersionId: resp.VersionId ?? string.Empty,
            Size: content.CanSeek ? content.Length : 0L,
            LastModified: DateTimeOffset.UtcNow);
    }

    private async Task<UploadResult> MultiPartPutAsync(
        string key, Stream content, CancellationToken ct)
    {
        var resp = await transferUtility.UploadWithResponseAsync(new TransferUtilityUploadRequest
        {
            BucketName = bucketName,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            PartSize = S3Util.MultipartPartSizeBytes,
        }, ct).ConfigureAwait(false);

        // resp.Size is nullable: TransferUtility leaves it null on the small-file path that
        // delegates to PutObject. The shared builder falls back to the stream length.
        return new(
            ETag: resp.ETag ?? string.Empty,
            VersionId: resp.VersionId ?? string.Empty,
            Size: resp.Size ?? (content.CanSeek ? content.Length : 0L),
            LastModified: DateTimeOffset.UtcNow);
    }

    public async Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        var relKey = S3Util.ToS3Key(relativePath);
        if (string.IsNullOrEmpty(relKey)) return;

        try
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucketName,
                Key = S3Util.FullKey(keyPrefix, relKey),
            }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone — treat as success.
        }
    }

    public async Task DeletePrefixAsync(string relativeDirectory, CancellationToken ct)
    {
        var relPrefix = S3Util.NormalizePrefix(relativeDirectory);
        if (relPrefix.Length == 0) return;
        var fullPrefix = keyPrefix + relPrefix;

        var batch = new List<KeyVersion>(capacity: 1000);
        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = fullPrefix,
        };

        do
        {
            var response = await client.ListObjectsV2Async(request, ct).ConfigureAwait(false);
            if (response.S3Objects is { } s3Objects)
            {
                foreach (var obj in s3Objects)
                {
                    if (string.IsNullOrEmpty(obj.Key)) continue;
                    batch.Add(new KeyVersion { Key = obj.Key });
                    if (batch.Count == 1000)
                    {
                        await FlushDeleteBatchAsync(batch, ct).ConfigureAwait(false);
                    }
                }
            }
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (!string.IsNullOrEmpty(request.ContinuationToken));

        if (batch.Count > 0)
        {
            await FlushDeleteBatchAsync(batch, ct).ConfigureAwait(false);
        }
    }

    public async Task RenameAsync(string oldRelativePath, string newRelativePath, CancellationToken ct)
    {
        var oldRelKey = S3Util.ToS3Key(oldRelativePath);
        var newRelKey = S3Util.ToS3Key(newRelativePath);
        if (string.IsNullOrEmpty(oldRelKey) || string.IsNullOrEmpty(newRelKey)) return;
        if (string.Equals(oldRelKey, newRelKey, StringComparison.Ordinal)) return;

        var oldKey = S3Util.FullKey(keyPrefix, oldRelKey);
        var newKey = S3Util.FullKey(keyPrefix, newRelKey);

        await client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucketName,
            SourceKey = oldKey,
            DestinationBucket = bucketName,
            DestinationKey = newKey,
        }, ct).ConfigureAwait(false);

        await client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = bucketName,
            Key = oldKey,
        }, ct).ConfigureAwait(false);
    }

    public async Task RenamePrefixAsync(
        string oldRelativeDirectory, string newRelativeDirectory, CancellationToken ct)
    {
        var oldRelPrefix = S3Util.NormalizePrefix(oldRelativeDirectory);
        var newRelPrefix = S3Util.NormalizePrefix(newRelativeDirectory);
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
        do
        {
            var response = await client.ListObjectsV2Async(request, ct).ConfigureAwait(false);
            if (response.S3Objects is { } s3Objects)
            {
                foreach (var obj in s3Objects)
                {
                    if (!string.IsNullOrEmpty(obj.Key))
                    {
                        keys.Add(obj.Key);
                    }
                }
            }
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (!string.IsNullOrEmpty(request.ContinuationToken));

        foreach (var oldKey in keys)
        {
            var suffix = oldKey[oldFullPrefix.Length..];
            var newKey = newFullPrefix + suffix;
            await client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = bucketName,
                SourceKey = oldKey,
                DestinationBucket = bucketName,
                DestinationKey = newKey,
            }, ct).ConfigureAwait(false);
        }

        if (keys.Count == 0) return;

        var batch = new List<KeyVersion>(capacity: Math.Min(keys.Count, 1000));
        foreach (var key in keys)
        {
            batch.Add(new KeyVersion { Key = key });
            if (batch.Count == 1000)
            {
                await FlushDeleteBatchAsync(batch, ct).ConfigureAwait(false);
            }
        }
        if (batch.Count > 0)
        {
            await FlushDeleteBatchAsync(batch, ct).ConfigureAwait(false);
        }
    }

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
