using Amazon.S3;
using Amazon.S3.Model;
using System.Net;
using System.Runtime.CompilerServices;

namespace S3Files.Windows.S3;

internal sealed class S3Backend(string bucketName, string? endpointUrl = null) : IS3Backend, IDisposable
{
    private readonly AmazonS3Client client = CreateClient(endpointUrl);

    public void Dispose() => client.Dispose();

    public async IAsyncEnumerable<S3ObjectInfo> ListAsync(
        string relativeDirectory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var prefix = S3Util.NormalizePrefix(relativeDirectory);
        var request = new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = prefix,
            Delimiter = "/",
        };

        do
        {
            var response = await client.ListObjectsV2Async(request, ct).ConfigureAwait(false);

            if (response.CommonPrefixes is { } commonPrefixes)
            {
                foreach (var common in commonPrefixes)
                {
                    var name = common[prefix.Length..].TrimEnd('/');
                    if (name.Length == 0) continue;
                    yield return new S3ObjectInfo(
                        Key: common,
                        RelativePath: S3Util.ToRelativePath(common.TrimEnd('/')),
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
                    var name = obj.Key[prefix.Length..];
                    if (name.Length == 0) continue;
                    yield return new S3ObjectInfo(
                        Key: obj.Key,
                        RelativePath: S3Util.ToRelativePath(obj.Key),
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
        };

        do
        {
            var response = await client.ListObjectsV2Async(request, ct).ConfigureAwait(false);
            if (response.S3Objects is { } s3Objects)
            {
                foreach (var obj in s3Objects)
                {
                    if (string.IsNullOrEmpty(obj.Key) || obj.Key.EndsWith('/')) continue;
                    yield return new S3ObjectInfo(
                        Key: obj.Key,
                        RelativePath: S3Util.ToRelativePath(obj.Key),
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

    public async Task<S3ObjectInfo?> HeadAsync(string relativePath, CancellationToken ct)
    {
        var key = S3Util.ToS3Key(relativePath);
        if (key.Length == 0)
        {
            return new S3ObjectInfo(string.Empty, string.Empty, 0, default, string.Empty, IsDirectory: true);
        }

        try
        {
            var resp = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucketName,
                Key = key,
            }, ct).ConfigureAwait(false);

            return new S3ObjectInfo(
                Key: key,
                RelativePath: S3Util.ToRelativePath(key),
                Size: resp.ContentLength,
                LastModified: resp.LastModified ?? default,
                ETag: resp.ETag ?? string.Empty,
                IsDirectory: false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            var dirPrefix = key.EndsWith('/') ? key : key + '/';
            var listResp = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucketName,
                Prefix = dirPrefix,
                MaxKeys = 1,
            }, ct).ConfigureAwait(false);

            if ((listResp.S3Objects?.Count ?? 0) > 0 || (listResp.CommonPrefixes?.Count ?? 0) > 0)
            {
                return new S3ObjectInfo(
                    Key: key,
                    RelativePath: S3Util.ToRelativePath(key),
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
            Key = S3Util.ToS3Key(relativePath),
            ByteRange = new ByteRange(offset, offset + length - 1),
        }, ct).ConfigureAwait(false);
        await resp.ResponseStream.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    public Task<UploadResult> UploadAsync(string relativePath, Stream content, string? ifMatchETag, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task DeleteAsync(string relativePath, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task RenameAsync(string oldRelativePath, string newRelativePath, CancellationToken ct) =>
        throw new NotImplementedException();

    private static AmazonS3Client CreateClient(string? endpointUrl)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = endpointUrl is not null,
        };
        if (!string.IsNullOrEmpty(endpointUrl))
        {
            config.ServiceURL = endpointUrl;
        }
        return new AmazonS3Client(config);
    }
}
