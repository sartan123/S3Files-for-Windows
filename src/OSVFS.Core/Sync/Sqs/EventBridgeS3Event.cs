using System.Text.Json.Serialization;

namespace OSVFS.Sync.Sqs;

/// <summary>
/// Parsed shape of an EventBridge S3 notification delivered through SQS. Only the
/// fields the change source actually consumes are modeled; unknown JSON members
/// are tolerated.
/// </summary>
/// <remarks>
/// Reference: <see href="https://docs.aws.amazon.com/AmazonS3/latest/userguide/EventBridge.html"/>.
/// EventBridge wraps the S3 event payload in an envelope with <c>source</c>,
/// <c>detail-type</c>, and <c>detail</c> fields. We only handle
/// <c>"Object Created"</c> and <c>"Object Deleted"</c>.
/// </remarks>
internal sealed record EventBridgeS3Event(
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("detail-type")] string? DetailType,
    [property: JsonPropertyName("detail")] EventBridgeS3Detail? Detail);

/// <summary>
/// Inner <c>detail</c> payload of an EventBridge S3 notification. Provides the
/// bucket and the affected object's identity.
/// </summary>
internal sealed record EventBridgeS3Detail(
    [property: JsonPropertyName("bucket")] EventBridgeS3Bucket? Bucket,
    [property: JsonPropertyName("object")] EventBridgeS3Object? Object,
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>
/// Bucket reference inside the EventBridge S3 detail block.
/// </summary>
internal sealed record EventBridgeS3Bucket(
    [property: JsonPropertyName("name")] string? Name);

/// <summary>
/// Object identity inside the EventBridge S3 detail block. <c>size</c> and
/// <c>etag</c> are only populated for create-style events; deletes leave them null.
/// </summary>
internal sealed record EventBridgeS3Object(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("etag")] string? ETag,
    [property: JsonPropertyName("version-id")] string? VersionId,
    [property: JsonPropertyName("sequencer")] string? Sequencer);

/// <summary>
/// Source-generated <see cref="System.Text.Json.JsonSerializerContext"/> so the
/// EventBridge payload deserializes without runtime reflection (Native AOT safe).
/// </summary>
[JsonSerializable(typeof(EventBridgeS3Event))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class EventBridgeS3EventJsonContext : JsonSerializerContext
{
}
