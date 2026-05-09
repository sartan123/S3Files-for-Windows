using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.Sync;
using OSVFS.Sync.Sqs;
using Xunit;

namespace OSVFS.Core.UnitTests.Sqs;

public sealed class SqsChangeSourceTests
{
    private const string Bucket = "demo-bucket";

    [Fact]
    public async Task EventBridge_ObjectCreated_message_maps_to_Upserted_event()
    {
        var body = $$"""
            {
              "version": "0",
              "source": "aws.s3",
              "detail-type": "Object Created",
              "detail": {
                "bucket": { "name": "{{Bucket}}" },
                "object": { "key": "docs/readme.md", "size": 17, "etag": "abc123" },
                "reason": "PutObject"
              }
            }
            """;

        var client = new FakeSqsClient();
        client.QueueResponse(Msg("m1", body));
        var source = NewSource(client, "https://sqs/queue", keyPrefix: null);

        var ev = await ReceiveOneAsync(source);

        Assert.Equal(ObjectChangeKind.Upserted, ev.Kind);
        Assert.Equal("docs/readme.md", ev.Key);
        Assert.Equal("docs\\readme.md", ev.RelativePath);
        Assert.Equal(17, ev.Size);
        Assert.Equal("abc123", ev.ETag);
        Assert.Single(client.Deletes);
    }

    [Fact]
    public async Task EventBridge_ObjectDeleted_message_maps_to_Deleted_event()
    {
        var body = $$"""
            {
              "source": "aws.s3",
              "detail-type": "Object Deleted",
              "detail": {
                "bucket": { "name": "{{Bucket}}" },
                "object": { "key": "old.txt" },
                "reason": "DeleteObject"
              }
            }
            """;

        var client = new FakeSqsClient();
        client.QueueResponse(Msg("m1", body));
        var source = NewSource(client, "https://sqs/queue", keyPrefix: null);

        var ev = await ReceiveOneAsync(source);

        Assert.Equal(ObjectChangeKind.Deleted, ev.Kind);
        Assert.Equal("old.txt", ev.Key);
        Assert.Equal(0, ev.Size);
    }

    [Fact]
    public async Task Messages_for_other_buckets_are_dropped_and_deleted()
    {
        var body = """
            {
              "source": "aws.s3",
              "detail-type": "Object Created",
              "detail": {
                "bucket": { "name": "other-bucket" },
                "object": { "key": "something.txt", "size": 1, "etag": "x" }
              }
            }
            """;

        var client = new FakeSqsClient();
        client.QueueResponse(Msg("m1", body));
        // Push a second valid message so the source surfaces something we can wait on.
        var goodBody = $$"""
            { "source": "aws.s3", "detail-type": "Object Created",
              "detail": { "bucket": { "name": "{{Bucket}}" },
                          "object": { "key": "ok.txt", "size": 2, "etag": "e" } } }
            """;
        client.QueueResponse(Msg("m2", goodBody));
        var source = NewSource(client, "https://sqs/queue", keyPrefix: null);

        var ev = await ReceiveOneAsync(source);

        Assert.Equal("ok.txt", ev.Key);
        // Both messages were deleted: the noise so it doesn't redeliver, and the good
        // one as part of the normal happy path.
        Assert.Equal(2, client.Deletes.Count);
    }

    [Fact]
    public async Task Keys_outside_the_linked_prefix_are_dropped()
    {
        var noiseBody = $$"""
            { "source": "aws.s3", "detail-type": "Object Created",
              "detail": { "bucket": { "name": "{{Bucket}}" },
                          "object": { "key": "other-prefix/file.txt", "size": 1, "etag": "e" } } }
            """;
        var inScopeBody = $$"""
            { "source": "aws.s3", "detail-type": "Object Created",
              "detail": { "bucket": { "name": "{{Bucket}}" },
                          "object": { "key": "team-a/note.txt", "size": 9, "etag": "e2" } } }
            """;

        var client = new FakeSqsClient();
        client.QueueResponse(Msg("noise", noiseBody), Msg("ok", inScopeBody));
        var source = NewSource(client, "https://sqs/queue", keyPrefix: "team-a");

        var ev = await ReceiveOneAsync(source);

        // Prefix is stripped from the emitted key.
        Assert.Equal("note.txt", ev.Key);
        Assert.Equal("note.txt", ev.RelativePath);
    }

    [Fact]
    public async Task Unparseable_messages_are_logged_dropped_and_deleted()
    {
        var client = new FakeSqsClient();
        client.QueueResponse(Msg("bad", "not json"));
        var goodBody = $$"""
            { "source": "aws.s3", "detail-type": "Object Created",
              "detail": { "bucket": { "name": "{{Bucket}}" },
                          "object": { "key": "after.txt", "size": 1, "etag": "e" } } }
            """;
        client.QueueResponse(Msg("good", goodBody));
        var source = NewSource(client, "https://sqs/queue", keyPrefix: null);

        var ev = await ReceiveOneAsync(source);

        Assert.Equal("after.txt", ev.Key);
        // Receipt handle prefix is set by Msg() and uniquely identifies "bad".
        Assert.Contains("rh-bad", client.Deletes.Select(d => d.ReceiptHandle));
    }

    [Fact]
    public async Task GetQueueUrl_resolves_bare_names_once_then_caches()
    {
        var body = $$"""
            { "source": "aws.s3", "detail-type": "Object Created",
              "detail": { "bucket": { "name": "{{Bucket}}" },
                          "object": { "key": "k.txt", "size": 1, "etag": "e" } } }
            """;

        var client = new FakeSqsClient();
        client.QueueResponse(Msg("m1", body));
        client.QueueResponse(Msg("m2", body));
        client.QueueUrlForName["my-queue"] = "https://sqs.example/123/my-queue";

        var source = NewSource(client, "my-queue", keyPrefix: null);

        // Drive two receive cycles; the source should resolve the queue URL once.
        await ReceiveOneAsync(source);
        await ReceiveOneAsync(source);

        Assert.Equal(1, client.GetQueueUrlCallCount);
        Assert.All(client.ReceiveRequests, r =>
            Assert.Equal("https://sqs.example/123/my-queue", r.QueueUrl));
    }

    private static SqsChangeSource NewSource(IAmazonSQS client, string queueUrlOrName, string? keyPrefix) =>
        new(
            client,
            ownsClient: false,
            queueUrlOrName: queueUrlOrName,
            bucketName: Bucket,
            keyPrefix: keyPrefix,
            logger: NullLogger<SqsChangeSource>.Instance);

    private static Message Msg(string id, string body) =>
        new() { MessageId = id, ReceiptHandle = "rh-" + id, Body = body };

    private static async Task<ObjectChangeEvent> ReceiveOneAsync(SqsChangeSource source)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var ev in source.WatchAsync(cts.Token))
        {
            return ev;
        }
        throw new InvalidOperationException("Source completed without yielding any event.");
    }

    /// <summary>
    /// Test fake for <see cref="IAmazonSQS"/>. Subclasses <see cref="AmazonSQSClient"/>
    /// so callers don't have to stub out the ~30 methods on the interface; only the
    /// Receive / Delete / GetQueueUrl methods we depend on are overridden, and a
    /// disposed-from-config check is sidestepped by using a dummy endpoint.
    /// </summary>
    private sealed class FakeSqsClient : AmazonSQSClient
    {
        private readonly Queue<ReceiveMessageResponse> queuedResponses = new();

        public List<ReceiveMessageRequest> ReceiveRequests { get; } = new();
        public List<DeleteMessageRequest> Deletes { get; } = new();
        public Dictionary<string, string> QueueUrlForName { get; } = new();
        public int GetQueueUrlCallCount { get; private set; }

        public FakeSqsClient()
            : base(
                new BasicAWSCredentials("test", "test"),
                new AmazonSQSConfig { ServiceURL = "http://localhost:1" })
        {
        }

        public void QueueResponse(params Message[] messages)
        {
            queuedResponses.Enqueue(new ReceiveMessageResponse
            {
                Messages = messages.ToList(),
            });
        }

        public override Task<ReceiveMessageResponse> ReceiveMessageAsync(
            ReceiveMessageRequest request, CancellationToken cancellationToken = default)
        {
            ReceiveRequests.Add(request);
            if (queuedResponses.TryDequeue(out var resp))
            {
                return Task.FromResult(resp);
            }
            // No more messages — block until cancelled so the test finishes
            // deterministically instead of busy-looping.
            return WaitForeverAsync(cancellationToken);
        }

        public override Task<DeleteMessageResponse> DeleteMessageAsync(
            DeleteMessageRequest request, CancellationToken cancellationToken = default)
        {
            Deletes.Add(request);
            return Task.FromResult(new DeleteMessageResponse());
        }

        public override Task<GetQueueUrlResponse> GetQueueUrlAsync(
            GetQueueUrlRequest request, CancellationToken cancellationToken = default)
        {
            GetQueueUrlCallCount++;
            return Task.FromResult(new GetQueueUrlResponse
            {
                QueueUrl = QueueUrlForName.TryGetValue(request.QueueName, out var u)
                    ? u
                    : throw new InvalidOperationException(
                        $"FakeSqsClient: no queue URL configured for name '{request.QueueName}'."),
            });
        }

        private static async Task<ReceiveMessageResponse> WaitForeverAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            return new ReceiveMessageResponse();
        }
    }
}
