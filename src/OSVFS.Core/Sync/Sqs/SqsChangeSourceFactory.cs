using Amazon.Runtime;
using Amazon.SQS;
using Microsoft.Extensions.Logging;
using OSVFS.ObjectStore;

namespace OSVFS.Sync.Sqs;

/// <summary>
/// Builds a fully-wired <see cref="SqsChangeSource"/> from CLI / config options,
/// constructing the underlying <see cref="AmazonSQSClient"/> with the same
/// endpoint / region / credential conventions as <c>S3Backend</c> so a single
/// LocalStack endpoint or AWS profile drives both.
/// </summary>
internal static class SqsChangeSourceFactory
{
    /// <summary>
    /// Constructs an <see cref="SqsChangeSource"/> bound to the supplied bucket
    /// and queue. The returned source owns its SQS client and disposes it.
    /// </summary>
    /// <param name="queueUrlOrName">SQS queue URL or queue name.</param>
    /// <param name="bucketName">Bucket whose events the queue carries; used to filter cross-bucket noise.</param>
    /// <param name="keyPrefix">Optional linked key prefix (slash-terminated or empty).</param>
    /// <param name="endpointUrl">Optional SQS endpoint override (LocalStack, custom proxy).</param>
    /// <param name="region">Optional AWS region; falls back to the SDK chain when null.</param>
    /// <param name="credentials">
    /// Optional credential source carried as the provider-neutral seam. The
    /// SQS factory is S3 / EventBridge specific, so it accepts only
    /// <see cref="AwsCredentialSource"/>; any other concrete shape (the host
    /// wiring a GCS / Azure source by mistake) is treated as a wiring bug
    /// and triggers <see cref="InvalidOperationException"/>. Null falls back
    /// to the SDK chain.
    /// </param>
    /// <param name="logger">Logger passed to the source for receive errors and parse warnings.</param>
    public static SqsChangeSource Create(
        string queueUrlOrName,
        string bucketName,
        string? keyPrefix,
        string? endpointUrl,
        string? region,
        IObjectStoreCredentialSource? credentials,
        ILogger<SqsChangeSource> logger)
    {
        var awsCredentials = NarrowToAws(credentials);
        var client = CreateClient(endpointUrl, region, awsCredentials);
        return new SqsChangeSource(
            client,
            ownsClient: true,
            queueUrlOrName,
            bucketName,
            keyPrefix,
            logger);
    }

    /// <summary>
    /// Narrows the generic credential seam to the AWS shape this factory
    /// requires. Null passes through; any non-AWS concrete type fails fast.
    /// </summary>
    private static AwsCredentialSource? NarrowToAws(IObjectStoreCredentialSource? credentials)
    {
        if (credentials is null) return null;
        if (credentials is AwsCredentialSource aws) return aws;
        throw new InvalidOperationException(
            $"SqsChangeSourceFactory requires credentials of type {nameof(AwsCredentialSource)}, " +
            $"got {credentials.GetType().Name}.");
    }

    /// <summary>
    /// Builds an <see cref="AmazonSQSClient"/> with the same endpoint/region/credential
    /// conventions used by the S3 backend.
    /// </summary>
    private static AmazonSQSClient CreateClient(
        string? endpointUrl, string? region, AwsCredentialSource? credentials)
    {
        var config = new AmazonSQSConfig();
        if (!string.IsNullOrEmpty(endpointUrl))
        {
            config.ServiceURL = endpointUrl;
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
            return new AmazonSQSClient(config);
        }
        return new AmazonSQSClient(credentials.Materialize(), config);
    }
}
