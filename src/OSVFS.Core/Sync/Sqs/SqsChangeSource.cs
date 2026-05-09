using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using OSVFS.ObjectStore;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OSVFS.Sync.Sqs;

/// <summary>
/// <see cref="IChangeSource"/> that long-polls an SQS queue carrying EventBridge
/// S3 notifications. Successful messages are translated to
/// <see cref="ObjectChangeEvent"/>s and yielded; consumed (or unparseable)
/// messages are deleted from the queue, while transient receive failures back
/// off and retry without deleting so the visibility timeout takes over.
/// </summary>
/// <remarks>
/// <para>
/// Only the EventBridge envelope (<c>{"source":"aws.s3","detail-type":"Object Created"|"Object Deleted",...}</c>)
/// is recognized. The legacy direct-S3-to-SQS notification format with a
/// <c>Records[]</c> array is not supported; configure the bucket to publish
/// through EventBridge if you want push-based change detection.
/// </para>
/// <para>
/// Self-suppression for events that echo the host's own writes is handled by
/// <see cref="ObjectStoreChangeWatcher"/> via its recent-mutation map; this
/// source is intentionally stateless beyond its queue connection.
/// </para>
/// </remarks>
internal sealed class SqsChangeSource : IChangeSource
{
    /// <summary>
    /// SQS hard-caps long-poll wait at 20 seconds; using the full window
    /// minimizes empty-receive overhead.
    /// </summary>
    public const int LongPollWaitSeconds = 20;

    /// <summary>
    /// How many messages to pull per receive call. SQS caps this at 10.
    /// </summary>
    public const int MaxMessagesPerReceive = 10;

    /// <summary>
    /// How long a message stays invisible after we receive it. Long enough for
    /// the watcher to apply the change before SQS would re-deliver.
    /// </summary>
    public const int VisibilityTimeoutSeconds = 30;

    /// <summary>
    /// Delay between failed receive cycles to avoid hot-looping when SQS or the
    /// network is misbehaving.
    /// </summary>
    private static readonly TimeSpan BackoffOnError = TimeSpan.FromSeconds(5);

    private readonly IAmazonSQS client;
    private readonly bool ownsClient;
    private readonly string queueUrlOrName;
    private readonly string bucketName;
    private readonly string keyPrefix;
    private readonly ILogger<SqsChangeSource> logger;

    private string? resolvedQueueUrl;

    /// <summary>
    /// Creates an SQS-backed change source bound to <paramref name="queueUrlOrName"/>.
    /// When the value lacks a scheme it is treated as a queue name and resolved via
    /// <c>GetQueueUrl</c> on first use. The source filters incoming events to those
    /// whose bucket matches <paramref name="bucketName"/> and whose key falls
    /// under <paramref name="keyPrefix"/> (when non-empty).
    /// </summary>
    /// <param name="client">SQS client used for receive/delete; the source disposes it iff <paramref name="ownsClient"/> is true.</param>
    /// <param name="ownsClient">Whether to dispose <paramref name="client"/> when the source is disposed.</param>
    /// <param name="queueUrlOrName">SQS queue URL or queue name.</param>
    /// <param name="bucketName">Name of the bucket the watcher is bound to.</param>
    /// <param name="keyPrefix">Optional linked key prefix (slash-terminated or empty) used to scope/strip events.</param>
    /// <param name="logger">Logger for receive errors and parse warnings.</param>
    public SqsChangeSource(
        IAmazonSQS client,
        bool ownsClient,
        string queueUrlOrName,
        string bucketName,
        string? keyPrefix,
        ILogger<SqsChangeSource> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueUrlOrName);
        ArgumentException.ThrowIfNullOrEmpty(bucketName);

        this.client = client;
        this.ownsClient = ownsClient;
        this.queueUrlOrName = queueUrlOrName;
        this.bucketName = bucketName;
        this.keyPrefix = KeyPath.NormalizeKeyPrefix(keyPrefix);
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ObjectChangeEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        string queueUrl;
        try
        {
            queueUrl = await ResolveQueueUrlAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { yield break; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve SQS queue URL for {QueueUrlOrName}; SQS change source disabled.", queueUrlOrName);
            yield break;
        }

        logger.LogInformation(
            "SQS change source started (queue = {QueueUrl}, bucket = {Bucket}).", queueUrl, bucketName);

        while (!ct.IsCancellationRequested)
        {
            ReceiveMessageResponse? response;
            try
            {
                response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    WaitTimeSeconds = LongPollWaitSeconds,
                    MaxNumberOfMessages = MaxMessagesPerReceive,
                    VisibilityTimeout = VisibilityTimeoutSeconds,
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { yield break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "SQS ReceiveMessage failed; backing off {Delay}.", BackoffOnError);
                try { await Task.Delay(BackoffOnError, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                continue;
            }

            if (response.Messages is null || response.Messages.Count == 0)
            {
                continue;
            }

            foreach (var msg in response.Messages)
            {
                ct.ThrowIfCancellationRequested();

                var converted = TryConvert(msg);
                // Delete before yielding: at-least-once SQS semantics combined with the
                // watcher's idempotent apply path means losing a duplicate on consumer
                // cancellation is preferable to leaving the message in the queue when
                // the iterator is suspended at the yield and never resumed.
                await TryDeleteAsync(queueUrl, msg.ReceiptHandle, ct).ConfigureAwait(false);
                if (converted is { } ev)
                {
                    yield return ev;
                }
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the configured queue specifier to a full URL. URLs are returned
    /// as-is; bare queue names are resolved via <c>GetQueueUrl</c>. The result
    /// is cached for the lifetime of the source.
    /// </summary>
    private async Task<string> ResolveQueueUrlAsync(CancellationToken ct)
    {
        if (resolvedQueueUrl is not null) return resolvedQueueUrl;

        if (LooksLikeUrl(queueUrlOrName))
        {
            resolvedQueueUrl = queueUrlOrName;
            return resolvedQueueUrl;
        }

        var resp = await client.GetQueueUrlAsync(
            new GetQueueUrlRequest { QueueName = queueUrlOrName }, ct).ConfigureAwait(false);
        resolvedQueueUrl = resp.QueueUrl
            ?? throw new InvalidOperationException(
                $"GetQueueUrl returned no URL for queue '{queueUrlOrName}'.");
        return resolvedQueueUrl;
    }

    /// <summary>
    /// Attempts to map an SQS message to an <see cref="ObjectChangeEvent"/>.
    /// Returns null when the message is malformed, references a different
    /// bucket, or falls outside the linked prefix; the caller still deletes
    /// such messages so they don't redeliver indefinitely.
    /// </summary>
    private ObjectChangeEvent? TryConvert(Message msg)
    {
        if (string.IsNullOrEmpty(msg.Body))
        {
            logger.LogWarning("Dropping empty SQS message {MessageId}.", msg.MessageId);
            return null;
        }

        EventBridgeS3Event? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(
                msg.Body,
                EventBridgeS3EventJsonContext.Default.EventBridgeS3Event);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(
                ex, "Dropping unparseable SQS message {MessageId}.", msg.MessageId);
            return null;
        }

        if (envelope is null || envelope.Detail is null)
        {
            logger.LogWarning(
                "Dropping SQS message {MessageId}: missing EventBridge envelope.", msg.MessageId);
            return null;
        }

        if (!string.Equals(envelope.Source, "aws.s3", StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Dropping SQS message {MessageId}: unexpected source {Source}.",
                msg.MessageId, envelope.Source);
            return null;
        }

        var detail = envelope.Detail;
        var actualBucket = detail.Bucket?.Name;
        if (!string.Equals(actualBucket, bucketName, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Dropping SQS message {MessageId}: bucket {Actual} does not match configured {Expected}.",
                msg.MessageId, actualBucket, bucketName);
            return null;
        }

        var fullKey = detail.Object?.Key;
        if (string.IsNullOrEmpty(fullKey))
        {
            logger.LogWarning(
                "Dropping SQS message {MessageId}: missing object key.", msg.MessageId);
            return null;
        }

        if (keyPrefix.Length > 0 && !fullKey.StartsWith(keyPrefix, StringComparison.Ordinal))
        {
            logger.LogDebug(
                "Skipping SQS message {MessageId}: key {Key} outside linked prefix {Prefix}.",
                msg.MessageId, fullKey, keyPrefix);
            return null;
        }

        var relativeKey = KeyPath.StripPrefix(keyPrefix, fullKey);
        if (string.IsNullOrEmpty(relativeKey) || relativeKey.EndsWith('/'))
        {
            // The bucket-root or directory-style entries can't be projected as files.
            return null;
        }

        return envelope.DetailType switch
        {
            "Object Created" => new ObjectChangeEvent(
                Kind: ObjectChangeKind.Upserted,
                Key: relativeKey,
                RelativePath: KeyPath.ToRelativePath(relativeKey),
                Size: detail.Object?.Size ?? 0,
                LastModified: DateTimeOffset.UtcNow,
                ETag: detail.Object?.ETag ?? string.Empty),
            "Object Deleted" => new ObjectChangeEvent(
                Kind: ObjectChangeKind.Deleted,
                Key: relativeKey,
                RelativePath: KeyPath.ToRelativePath(relativeKey),
                Size: 0,
                LastModified: default,
                ETag: string.Empty),
            _ => Skip(msg, envelope.DetailType),
        };
    }

    /// <summary>
    /// Logs and drops messages whose <c>detail-type</c> we don't translate.
    /// </summary>
    private ObjectChangeEvent? Skip(Message msg, string? detailType)
    {
        logger.LogDebug(
            "Skipping SQS message {MessageId} with unhandled detail-type {DetailType}.",
            msg.MessageId, detailType);
        return null;
    }

    /// <summary>
    /// Best-effort delete that doesn't propagate errors back into the receive
    /// loop. A failed delete just means the message redelivers after the
    /// visibility timeout, which the watcher tolerates.
    /// </summary>
    private async Task TryDeleteAsync(string queueUrl, string receiptHandle, CancellationToken ct)
    {
        try
        {
            await client.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = queueUrl,
                ReceiptHandle = receiptHandle,
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* loop will exit on next iteration */ }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Failed to delete SQS message; visibility timeout will redeliver it.");
        }
    }

    /// <summary>
    /// True for inputs that already look like full SQS queue URLs. We avoid
    /// over-clever heuristics — a scheme prefix is the only signal we care about.
    /// </summary>
    private static bool LooksLikeUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
