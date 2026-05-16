using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage;
using OSVFS.Net;
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

    /// <summary>
    /// Default stream-size threshold above which uploads are routed through
    /// the Block Blob commit path. Matches the S3 backend's 16 MiB so the
    /// operator-facing tuning advice in the README applies on both sides.
    /// </summary>
    public const long DefaultMultipartThresholdBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Default per-block size for chunked uploads. 4 MiB is the Azure SDK's
    /// own default and a safe middle ground: large enough to amortize per-
    /// request overhead on fat links, small enough that retries on a flaky
    /// network only re-upload a few MB.
    /// </summary>
    public const long DefaultMultipartPartSizeBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Default ceiling on in-flight upload API calls — mirrors S3.
    /// </summary>
    public const int DefaultMaxConcurrentUploads = 4;

    /// <summary>
    /// Default ceiling on in-flight range-read API calls — mirrors S3.
    /// </summary>
    public const int DefaultMaxConcurrentDownloads = 8;

    /// <summary>
    /// Default per-upload block parallelism. Threaded through to
    /// <see cref="StorageTransferOptions.MaximumConcurrency"/>.
    /// </summary>
    public const int DefaultMaxMultipartParts = 10;

    private readonly string containerName;
    private readonly string keyPrefix;
    private readonly BlobServiceClient serviceClient;
    private readonly BlobContainerClient containerClient;
    private readonly IRateLimiter? upLimiter;
    private readonly IRateLimiter? downLimiter;
    private readonly StorageTransferOptions transferOptions;
    private readonly SemaphoreSlim uploadGate;
    private readonly SemaphoreSlim downloadGate;

    /// <inheritdoc/>
    public int UserMetadataMaxBytes => UserMetadataMaxByteCount;

    /// <summary>
    /// Creates a backend bound to <paramref name="containerName"/>. The
    /// supported <paramref name="credentials"/> branches are:
    /// <list type="bullet">
    ///   <item>connection string (Azurite, Azure Portal "Access keys")</item>
    ///   <item>service- or account-level SAS bound to an account name</item>
    ///   <item>Managed Identity bound to an account name</item>
    ///   <item><c>DefaultAzureCredential</c> chain bound to an account name</item>
    /// </list>
    /// <paramref name="endpointUrl"/> overrides the otherwise-default
    /// <c>https://{accountName}.blob.core.windows.net</c> service endpoint —
    /// useful for Azure Stack / sovereign clouds and for pointing a SAS-
    /// authenticated client at Azurite.
    /// <paramref name="clientOptions"/> lets callers pin the SDK's
    /// <c>x-ms-version</c> (and configure retry / pipeline policies); when
    /// null, the SDK's default newest <c>ServiceVersion</c> is used. The
    /// integration tests pass a pinned-version instance so Azurite's lagging
    /// API-version support cannot block Azure SDK Dependabot bumps.
    /// </summary>
    public AzureBlobBackend(
        string containerName,
        AzureCredentialSource? credentials,
        string? endpointUrl = null,
        string? keyPrefix = null,
        IRateLimiter? upLimiter = null,
        IRateLimiter? downLimiter = null,
        long? multipartThresholdBytes = null,
        long? multipartPartSizeBytes = null,
        int? maxConcurrentUploads = null,
        int? maxConcurrentDownloads = null,
        int? maxMultipartParts = null,
        BlobClientOptions? clientOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        this.containerName = containerName;
        this.keyPrefix = KeyPath.NormalizeKeyPrefix(keyPrefix);
        this.upLimiter = upLimiter;
        this.downLimiter = downLimiter;

        var threshold = multipartThresholdBytes ?? DefaultMultipartThresholdBytes;
        var partSize = multipartPartSizeBytes ?? DefaultMultipartPartSizeBytes;
        var multipartParts = Math.Max(1, maxMultipartParts ?? DefaultMaxMultipartParts);
        // The Azure SDK's StorageTransferOptions maps cleanly onto OSVFS's
        // multipart knobs: InitialTransferLength is the "single-shot" cap (the
        // operator's multipart-threshold), MaximumTransferLength is the per-
        // block size, and MaximumConcurrency caps the in-flight blocks for one
        // upload (the operator's max-multipart-parts).
        transferOptions = new StorageTransferOptions
        {
            InitialTransferSize = threshold,
            MaximumTransferSize = partSize,
            MaximumConcurrency = multipartParts,
        };

        var concurrentUploads = Math.Max(1, maxConcurrentUploads ?? DefaultMaxConcurrentUploads);
        var concurrentDownloads = Math.Max(1, maxConcurrentDownloads ?? DefaultMaxConcurrentDownloads);
        uploadGate = new SemaphoreSlim(concurrentUploads, concurrentUploads);
        downloadGate = new SemaphoreSlim(concurrentDownloads, concurrentDownloads);

        serviceClient = BuildServiceClient(credentials, endpointUrl, clientOptions);
        containerClient = serviceClient.GetBlobContainerClient(containerName);
    }

    /// <summary>
    /// Selects the matching <see cref="BlobServiceClient"/> constructor for
    /// the configured credential branch. Each branch fails fast when its
    /// required keys are missing so the operator gets a clear startup error
    /// rather than an opaque SDK exception on the first request. When
    /// <paramref name="clientOptions"/> is non-null it is threaded into every
    /// BlobServiceClient constructor so the caller can pin the wire-version
    /// (or attach retry / pipeline policies); a null is left as null so the
    /// SDK falls back to its own newest-default <c>ServiceVersion</c> in
    /// production.
    /// </summary>
    private static BlobServiceClient BuildServiceClient(
        AzureCredentialSource? credentials, string? endpointUrl, BlobClientOptions? clientOptions)
    {
        if (credentials is null)
        {
            throw new InvalidOperationException(
                "Azure Blob backend requires a credential source. Set one of " +
                "'connection-string', 'sas', 'managed-identity', or 'default-azure-credential' " +
                "in osvfs.toml.");
        }

        // Branch 1 — Connection string. The connection string carries the
        // endpoint, so endpointUrl is intentionally ignored here.
        if (credentials.ConnectionString is { } connectionString)
        {
            return new BlobServiceClient(connectionString, clientOptions);
        }

        // Branches 2-4 all need an account name to build the service URL.
        if (string.IsNullOrEmpty(credentials.AccountName))
        {
            throw new InvalidOperationException(
                $"Azure Blob credential source '{credentials.Description}' is missing the account name. " +
                "This is a wiring bug — credential factory methods should set AccountName for non-connection-string branches.");
        }

        var serviceUri = ResolveServiceUri(credentials.AccountName, endpointUrl);

        // Branch 2 — SAS.
        if (credentials.Sas is { } sas)
        {
            return new BlobServiceClient(serviceUri, new AzureSasCredential(sas), clientOptions);
        }

        // Branches 3 / 4 — Managed Identity / DefaultAzureCredential. The
        // backend doesn't care which one; the SDK consumes the TokenCredential
        // uniformly.
        if (credentials.TokenCredential is { } tokenCredential)
        {
            return new BlobServiceClient(serviceUri, tokenCredential, clientOptions);
        }

        throw new InvalidOperationException(
            $"Azure Blob credential source '{credentials.Description}' carries no usable branch. " +
            "This is a wiring bug — credential factory methods should populate exactly one branch.");
    }

    /// <summary>
    /// Builds the blob service URI. Operators may override the default
    /// (<c>https://{accountName}.blob.core.windows.net</c>) via
    /// <paramref name="endpointUrl"/> for Azure Stack, sovereign clouds, or
    /// SAS-authenticated traffic against Azurite.
    /// </summary>
    private static Uri ResolveServiceUri(string accountName, string? endpointUrl)
    {
        if (!string.IsNullOrEmpty(endpointUrl))
        {
            return new Uri(endpointUrl);
        }
        return new Uri($"https://{accountName}.blob.core.windows.net");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // BlobServiceClient / BlobContainerClient are lightweight wrappers
        // over a shared HttpPipeline and do not implement IDisposable; only
        // the semaphores and rate-limit owners we constructed need cleanup.
        uploadGate.Dispose();
        downloadGate.Dispose();
        (upLimiter as IDisposable)?.Dispose();
        (downLimiter as IDisposable)?.Dispose();
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
    /// <remarks>
    /// Azure storage-account Versioning is **not** exposed through the
    /// data-plane <c>GetServiceProperties</c> response — that lives on the
    /// Azure Resource Manager surface (<c>Microsoft.Storage</c> control
    /// plane), which OSVFS deliberately does not depend on so a single SDK +
    /// data-plane credential can drive the whole mount. We therefore check
    /// the half of the safety bar the data plane *does* expose, namely
    /// blob-level Soft Delete (<c>DeleteRetentionPolicy.Enabled</c>): if
    /// Soft Delete is on, accidental local deletes propagate as tombstones
    /// that stay recoverable for the configured retention window. Versioning
    /// — which protects against the *overwrite* path — is an operator
    /// responsibility surfaced through <see cref="GetEnableVersioningInstructions"/>:
    /// the remediation message asks for both <c>--enable-versioning</c> AND
    /// <c>--enable-delete-retention</c>, so an operator who follows it ends
    /// up with the full Phase-2 "Soft Delete + Versioning" posture even
    /// though only the Soft Delete half is automatically verified.
    /// </remarks>
    public async Task<BucketVersioningStatus> GetBucketVersioningStatusAsync(CancellationToken ct)
    {
        BlobServiceProperties props;
        try
        {
            var resp = await serviceClient.GetPropertiesAsync(ct).ConfigureAwait(false);
            props = resp.Value;
        }
        catch (RequestFailedException)
        {
            // Reading service properties requires storage-account-level read;
            // if the supplied credential lacks it (common with narrowly-scoped
            // SAS or RBAC roles) we cannot confirm safety and fall back to
            // NotEnabled. The startup guard then surfaces the same
            // "enable Soft Delete + Versioning" hint as if the account were
            // unprotected — the right user-facing state.
            return BucketVersioningStatus.NotEnabled;
        }

        var softDelete = props.DeleteRetentionPolicy?.Enabled == true;
        return softDelete
            ? BucketVersioningStatus.Enabled
            : BucketVersioningStatus.NotEnabled;
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

        // Hold the gate for the entire request lifetime — including streaming
        // the response body — so a slow consumer cannot let more than
        // maxConcurrentDownloads readers share the SDK's HTTP pool.
        await downloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var resp = await blob.DownloadStreamingAsync(
                new BlobDownloadOptions { Range = new HttpRange(offset, length) },
                cancellationToken: ct).ConfigureAwait(false);
            await using var content = resp.Value.Content;
            // Pace the response body when a download ceiling is configured.
            var source = downLimiter is null
                ? content
                : new RateLimitedStream(content, downLimiter);
            await source.CopyToAsync(destination, ct).ConfigureAwait(false);
        }
        finally
        {
            downloadGate.Release();
        }
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

        var options = new BlobUploadOptions
        {
            // Thread the operator-supplied multipart knobs straight through to
            // the SDK's StorageTransferOptions: the SDK fan-outs to StageBlock
            // / CommitBlockList when the stream exceeds InitialTransferSize,
            // chunks it at MaximumTransferSize, and respects MaximumConcurrency
            // for the parallel block uploads.
            TransferOptions = transferOptions,
        };
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

        // Hold the upload gate around the full upload (single-shot or
        // commit-style multipart). One UploadAsync = one permit regardless
        // of how many blocks the SDK splits the upload into; per-upload
        // block parallelism is capped by transferOptions.MaximumConcurrency.
        await uploadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Pace the upload payload via the rate-limited wrapper when a
            // bandwidth ceiling is configured.
            var paced = upLimiter is null ? content : new RateLimitedStream(content, upLimiter);
            var resp = await blob.UploadAsync(paced, options, ct).ConfigureAwait(false);
            var info = resp.Value;
            // BlobContentInfo doesn't carry size; the upload accepted whatever
            // the stream produced. If the stream is seekable we report Length,
            // otherwise 0 — the watcher's snapshot only uses Size as a
            // heuristic and ETag is the authoritative identity.
            var size = content.CanSeek ? content.Length : 0L;
            return new UploadResult(
                ETag: info.ETag.ToString(),
                VersionId: info.VersionId ?? string.Empty,
                Size: size,
                LastModified: info.LastModified);
        }
        finally
        {
            uploadGate.Release();
        }
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
