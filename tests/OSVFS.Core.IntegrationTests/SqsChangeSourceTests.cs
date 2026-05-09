using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.Sync;
using OSVFS.Sync.Sqs;
using Xunit;

namespace OSVFS.Core.IntegrationTests;

/// <summary>
/// Validates the SQS change source against a real SQS service in LocalStack.
/// We don't bother wiring up actual S3 → EventBridge → SQS in the container;
/// the source consumes whatever EventBridge-shaped JSON ends up on the queue,
/// so seeding the queue with hand-crafted messages exercises the end-to-end
/// long-poll → parse → translate path that production would use.
/// </summary>
[Collection(LocalStackCollection.Name)]
public sealed class SqsChangeSourceTests : IAsyncLifetime
{
    private const string Bucket = "osvfs-sqs-test";
    private readonly LocalStackFixture localStack;
    private readonly string queueName = $"osvfs-changes-{Guid.NewGuid():N}";
    private AmazonSQSClient adminClient = null!;
    private string queueUrl = null!;

    public SqsChangeSourceTests(LocalStackFixture localStack)
    {
        this.localStack = localStack;
    }

    public async Task InitializeAsync()
    {
        var config = new AmazonSQSConfig
        {
            ServiceURL = localStack.ServiceUrl,
        };
        adminClient = new AmazonSQSClient(config);
        var resp = await adminClient.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName });
        queueUrl = resp.QueueUrl;
    }

    public async Task DisposeAsync()
    {
        try
        {
            await adminClient.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl });
        }
        catch (AmazonSQSException)
        {
            // Cleanup is best-effort.
        }
        adminClient.Dispose();
    }

    [Fact]
    public async Task SqsChangeSource_translates_EventBridge_messages_received_from_LocalStack()
    {
        var createdBody = $$"""
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
        var deletedBody = $$"""
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

        await adminClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = createdBody,
        });
        await adminClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = deletedBody,
        });

        var sqsClient = new AmazonSQSClient(new AmazonSQSConfig
        {
            ServiceURL = localStack.ServiceUrl,
        });
        var source = new SqsChangeSource(
            sqsClient,
            ownsClient: true,
            queueUrlOrName: queueUrl,
            bucketName: Bucket,
            keyPrefix: null,
            logger: NullLogger<SqsChangeSource>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var collected = new List<ObjectChangeEvent>();

        await foreach (var ev in source.WatchAsync(cts.Token))
        {
            collected.Add(ev);
            if (collected.Count >= 2) break;
        }

        await source.DisposeAsync();

        Assert.Equal(2, collected.Count);
        Assert.Contains(collected, ev =>
            ev.Kind == ObjectChangeKind.Upserted &&
            ev.Key == "docs/readme.md" &&
            ev.Size == 17 &&
            ev.ETag == "abc123");
        Assert.Contains(collected, ev =>
            ev.Kind == ObjectChangeKind.Deleted &&
            ev.Key == "old.txt");
    }
}
