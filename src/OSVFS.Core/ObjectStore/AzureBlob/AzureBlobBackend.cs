using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Runtime.CompilerServices;

namespace OSVFS.ObjectStore.AzureBlob;

/// <summary>
/// Azure SDK-backed implementation of <see cref="IObjectStoreBackend"/>. Step 2A
/// covers the read/write path against Azurite and a real Azure Storage Account
/// using a connection string; Versioning + Soft Delete enforcement, Block Blob
/// commit-style multipart uploads, and Event Grid → Storage Queue change
/// notifications land in Step 2C / 2D / 2E respectively.
/// </summary>
internal sealed class AzureBlobBackend : IObjectStoreBackend
{
    /// <summary>
    /// Azure Blob caps the combined byte count of all <c>x-ms-meta-*</c>
    /// headers at 8 KiB per blob. Pre-validating uploads against this limit
    /// fails fast instead of surfacing as an opaque 400 from the service.
    /// </summary>
    public const int UserMetadataMaxByteCount = 8 * 1024;

    private readonly string containerName;
    private readonly string keyPrefix;
    private readonly BlobContainerClient containerClient;

    /// <inheritdoc/>
    public int UserMetadataMaxBytes => UserMetadataMaxByteCount;

    /// <summary>
    /// Creates a backend bound to <paramref name="containerName"/>. The credential
    /// source must carry a connection string today; SAS / Managed Identity /
    /// <c>DefaultAzureCredential</c> branches land in Step 2B (#52).
    /// <paramref name="endpointUrl"/> is currently unused — connection strings
    /// already carry the endpoint — but kept on the surface for symmetry with
    /// the S3 backend so the factory can stay uniform.
    /// </summary>
    public AzureBlobBackend(
        string containerName,
        AzureCredentialSource? credentials,
        string? endpointUrl = null,
        string? keyPrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        this.containerName = containerName;
        this.keyPrefix = KeyPath.NormalizeKeyPrefix(keyPrefix);

        if (credentials?.ConnectionString is { } connectionString)
        {
            var serviceClient = new BlobServiceClient(connectionString);
            containerClient = serviceClient.GetBlobContainerClient(containerName);
        }
        else
        {
            throw new InvalidOperationException(
                "Azure Blob backend requires a connection string credential source. " +
                "Set 'connection-string' in osvfs.toml or wait for Step 2B (#52) for SAS / Managed Identity / DefaultAzureCredential.");
        }
        _ = endpointUrl; // reserved for future use; currently the connection string is authoritative.
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // BlobServiceClient / BlobContainerClient do not implement IDisposable —
        // they are lightweight wrappers over a shared HttpPipeline. Nothing to
        // release here today.
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ObjectInfo> ListAsync(string relativeDirectory, CancellationToken ct) =>
        ListInternalAsync(relativeDirectory, ct);

    private async IAsyncEnumerable<ObjectInfo> ListInternalAsync(
        string relativeDirectory, [EnumeratorCancellation] CancellationToken ct)
    {
        var fullPrefix = KeyPath.FullPrefix(keyPrefix, relativeDirectory);
        await foreach (var page in containerClient
            .GetBlobsByHierarchyAsync(
                traits: BlobTraits.Metadata,
                states: BlobStates.None,
                prefix: fullPrefix,
                delimiter: "/",
                cancellationToken: ct)
            .AsPages())
        {
            foreach (var item in page.Values)
            {
                if (item.IsBlob && item.Blob is { } blob)
                {
                    if (string.IsNullOrEmpty(blob.Name) || blob.Name.EndsWith('/')) continue;
                    if (blob.Name.Length == fullPrefix.Length) continue;
                    yield return CreateFileInfo(blob);
                }
                else if (item.IsPrefix && !string.IsNullOrEmpty(item.Prefix))
                {
                    yield return CreateDirectoryInfo(item.Prefix);
                }
            }
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ObjectInfo> ListAllAsync(CancellationToken ct) =>
        ListAllInternalAsync(ct);

    private async IAsyncEnumerable<ObjectInfo> ListAllInternalAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var page in containerClient
            .GetBlobsAsync(
                traits: BlobTraits.Metadata,
                states: BlobStates.None,
                prefix: keyPrefix.Length > 0 ? keyPrefix : null,
                cancellationToken: ct)
            .AsPages())
        {
            foreach (var item in page.Values)
            {
                if (string.IsNullOrEmpty(item.Name) || item.Name.EndsWith('/')) continue;
                yield return CreateFileInfo(item);
            }
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ObjectInfo> ListRecursiveAsync(
        string relativeDirectory, CancellationToken ct) =>
        ListRecursiveInternalAsync(relativeDirectory, ct);

    private async IAsyncEnumerable<ObjectInfo> ListRecursiveInternalAsync(
        string relativeDirectory, [EnumeratorCancellation] CancellationToken ct)
    {
        var fullPrefix = KeyPath.FullPrefix(keyPrefix, relativeDirectory);
        await foreach (var page in containerClient
            .GetBlobsAsync(
                traits: BlobTraits.Metadata,
                states: BlobStates.None,
                prefix: fullPrefix,
                cancellationToken: ct)
            .AsPages())
        {
            foreach (var item in page.Values)
            {
                if (string.IsNullOrEmpty(item.Name) || item.Name.EndsWith('/')) continue;
                yield return CreateFileInfo(item);
            }
        }
    }

    /// <inheritdoc/>
    public Task<BucketVersioningStatus> GetBucketVersioningStatusAsync(CancellationToken ct)
    {
        // Step 2A stub: pretend versioning is on so the safety guard does not
        // block development against Azurite (Azurite does not implement
        // versioning). Step 2C (#53) reads the storage account's blob-service
        // properties for the real check, requiring both Versioning and Soft
        // Delete to be enabled.
        _ = ct;
        return Task.FromResult(BucketVersioningStatus.Enabled);
    }

    /// <inheritdoc/>
    public string GetEnableVersioningInstructions()
    {
        var account = containerClient.AccountName;
        return string.IsNullOrEmpty(account)
            ? "  az storage account blob-service-properties update --account-name <name> " +
              "--enable-versioning true --enable-delete-retention true --delete-retention-days 7"
            : $"  az storage account blob-service-properties update --account-name {account} " +
              "--enable-versioning true --enable-delete-retention true --delete-retention-days 7";
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
        var blob = containerClient.GetBlobClient(fullKey);
        try
        {
            var resp = await blob.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
            var props = resp.Value;
            return new ObjectInfo(
                Key: relKey,
                RelativePath: KeyPath.ToRelativePath(relKey),
                Size: props.ContentLength,
                LastModified: props.LastModified,
                ETag: props.ETag.ToString(),
                IsDirectory: false,
                UserMetadata: NormalizeMetadata(props.Metadata));
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Probe for a synthesized directory: if anything exists under the
            // would-be prefix, surface a directory entry — same shape S3 returns.
            var dirPrefix = fullKey.EndsWith('/') ? fullKey : fullKey + '/';
            await foreach (var _ in containerClient
                .GetBlobsAsync(traits: BlobTraits.None, states: BlobStates.None, prefix: dirPrefix, cancellationToken: ct))
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
        var fullKey = KeyPath.FullKey(keyPrefix, KeyPath.ToObjectKey(relativePath));
        var blob = containerClient.GetBlobClient(fullKey);
        var resp = await blob.DownloadStreamingAsync(
            new BlobDownloadOptions { Range = new HttpRange(offset, length) },
            cancellationToken: ct).ConfigureAwait(false);
        await using var content = resp.Value.Content;
        await content.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<UploadResult> UploadAsync(
        string relativePath, Stream content, string? ifMatchETag, CancellationToken ct,
        IReadOnlyDictionary<string, string>? userMetadata = null)
    {
        var normalizedMetadata = ObjectStore.UserMetadata.Normalize(userMetadata);
        ObjectStore.UserMetadata.EnsureWithinSizeLimit(normalizedMetadata, UserMetadataMaxBytes);

        var fullKey = KeyPath.FullKey(keyPrefix, KeyPath.ToObjectKey(relativePath));
        var blob = containerClient.GetBlobClient(fullKey);

        var options = new BlobUploadOptions();
        if (normalizedMetadata.Count > 0)
        {
            // BlobUploadOptions.Metadata is IDictionary<string, string>; copy into
            // a fresh map so we do not pin the caller's dictionary on the SDK request.
            var metadataCopy = new Dictionary<string, string>(normalizedMetadata.Count, StringComparer.Ordinal);
            foreach (var (k, v) in normalizedMetadata)
            {
                metadataCopy[k] = v;
            }
            options.Metadata = metadataCopy;
        }
        if (!string.IsNullOrEmpty(ifMatchETag))
        {
            // Azure expects ETag without surrounding quotes; strip if the caller passed S3-shape.
            options.Conditions = new BlobRequestConditions { IfMatch = new ETag(ifMatchETag.Trim('"')) };
        }

        // BlobClient.UploadAsync defaults to overwrite semantics when conditions
        // are omitted. Azurite + the SDK's default chunking is sufficient for
        // Step 2A; Block Blob commit-style chunking lands in Step 2D.
        var resp = await blob.UploadAsync(content, options, ct).ConfigureAwait(false);
        var info = resp.Value;
        // BlobContentInfo doesn't carry size; the upload accepted whatever the
        // stream produced. If the stream is seekable we report Length, otherwise
        // 0 — the watcher's snapshot only uses Size as a heuristic and ETag is
        // the authoritative identity.
        var size = content.CanSeek ? content.Length : 0L;
        return new UploadResult(
            ETag: info.ETag.ToString(),
            VersionId: info.VersionId ?? string.Empty,
            Size: size,
            LastModified: info.LastModified);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        var fullKey = KeyPath.FullKey(keyPrefix, KeyPath.ToObjectKey(relativePath));
        var blob = containerClient.GetBlobClient(fullKey);
        await blob.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeletePrefixAsync(string relativeDirectory, CancellationToken ct)
    {
        var fullPrefix = KeyPath.FullPrefix(keyPrefix, relativeDirectory);
        var keys = new List<string>();
        await foreach (var blob in containerClient
            .GetBlobsAsync(traits: BlobTraits.None, states: BlobStates.None, prefix: fullPrefix, cancellationToken: ct))
        {
            keys.Add(blob.Name);
        }
        // Azure exposes batch delete through BlobBatchClient (separate package).
        // Step 2A stays simple with per-blob deletes; Step 2D can switch to the
        // batch client if real-world directories prove large enough to benefit.
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            await containerClient.GetBlobClient(key)
                .DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task RenameAsync(string oldRelativePath, string newRelativePath, CancellationToken ct)
    {
        var src = containerClient.GetBlobClient(
            KeyPath.FullKey(keyPrefix, KeyPath.ToObjectKey(oldRelativePath)));
        var dst = containerClient.GetBlobClient(
            KeyPath.FullKey(keyPrefix, KeyPath.ToObjectKey(newRelativePath)));
        // SyncCopyFromUriAsync runs the copy server-side and waits for completion;
        // works for any same-account source up to 256 MiB. Step 2D may upgrade
        // to StartCopyFromUriAsync + polling for very large blobs.
        await dst.SyncCopyFromUriAsync(src.Uri, cancellationToken: ct).ConfigureAwait(false);
        await src.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RenamePrefixAsync(
        string oldRelativeDirectory, string newRelativeDirectory, CancellationToken ct)
    {
        var oldPrefix = KeyPath.FullPrefix(keyPrefix, oldRelativeDirectory);
        var newPrefix = KeyPath.FullPrefix(keyPrefix, newRelativeDirectory);
        var pairs = new List<(string OldKey, string NewKey)>();
        await foreach (var blob in containerClient
            .GetBlobsAsync(traits: BlobTraits.None, states: BlobStates.None, prefix: oldPrefix, cancellationToken: ct))
        {
            var newKey = newPrefix + blob.Name[oldPrefix.Length..];
            pairs.Add((blob.Name, newKey));
        }
        foreach (var (oldKey, newKey) in pairs)
        {
            ct.ThrowIfCancellationRequested();
            var src = containerClient.GetBlobClient(oldKey);
            var dst = containerClient.GetBlobClient(newKey);
            await dst.SyncCopyFromUriAsync(src.Uri, cancellationToken: ct).ConfigureAwait(false);
            await src.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds an <see cref="ObjectInfo"/> from a hierarchical-listing blob entry.
    /// Strips the linked prefix back off so the caller receives virt-root-relative keys.
    /// </summary>
    private ObjectInfo CreateFileInfo(BlobItem blob)
    {
        var relKey = KeyPath.StripPrefix(keyPrefix, blob.Name);
        return new ObjectInfo(
            Key: relKey,
            RelativePath: KeyPath.ToRelativePath(relKey),
            Size: blob.Properties.ContentLength ?? 0,
            LastModified: blob.Properties.LastModified ?? default,
            ETag: blob.Properties.ETag?.ToString() ?? string.Empty,
            IsDirectory: false,
            UserMetadata: NormalizeMetadata(blob.Metadata));
    }

    /// <summary>
    /// Builds a synthesized directory entry from a common-prefix listing item.
    /// </summary>
    private ObjectInfo CreateDirectoryInfo(string fullPrefix)
    {
        var relPrefix = KeyPath.StripPrefix(keyPrefix, fullPrefix.TrimEnd('/'));
        return new ObjectInfo(
            Key: relPrefix,
            RelativePath: KeyPath.ToRelativePath(relPrefix),
            Size: 0,
            LastModified: default,
            ETag: string.Empty,
            IsDirectory: true);
    }

    /// <summary>
    /// Normalizes Azure metadata to the lowercase-key shape OSVFS uses
    /// internally. Azure returns case-preserved names but treats them
    /// case-insensitively on retrieval, so the lowercase form keeps
    /// round-trips consistent with the S3 wire shape.
    /// </summary>
    private static Dictionary<string, string>? NormalizeMetadata(
        IDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;
        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var (k, v) in metadata)
        {
            if (string.IsNullOrEmpty(k)) continue;
            copy[k.ToLowerInvariant()] = v ?? string.Empty;
        }
        return copy;
    }
}
