using Amazon.S3;
using Amazon.S3.Model;
using OSVFS.Diagnostics;
using OSVFS.ObjectStore.S3;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Xunit;

namespace OSVFS.Core.IntegrationTests;

/// <summary>
/// End-to-end coverage of the OSVFS S3 telemetry pipeline against a real
/// LocalStack-backed bucket. Confirms that the public IObjectStoreBackend
/// methods produce the expected Activity tree (operation names + tags)
/// and feed the four <c>osvfs.s3.*</c> instruments. Listening via the BCL
/// <see cref="ActivityListener"/> / <see cref="MeterListener"/> APIs keeps
/// the tests independent of any OpenTelemetry test exporter package.
/// </summary>
[Collection(LocalStackCollection.Name)]
public sealed class S3BackendTelemetryTests : IAsyncLifetime
{
    private readonly LocalStackFixture localStack;
    private readonly string bucket = $"osvfs-{Guid.NewGuid():N}";
    private AmazonS3Client adminClient = null!;
    private S3Backend backend = null!;

    public S3BackendTelemetryTests(LocalStackFixture localStack)
    {
        this.localStack = localStack;
    }

    public async Task InitializeAsync()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = localStack.ServiceUrl,
            ForcePathStyle = true,
        };
        adminClient = new AmazonS3Client(config);
        await adminClient.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        backend = new S3Backend(bucket, localStack.ServiceUrl);
    }

    public async Task DisposeAsync()
    {
        try
        {
            var listing = await adminClient.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
            if ((listing.S3Objects?.Count ?? 0) > 0)
            {
                await adminClient.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = bucket,
                    Objects = listing.S3Objects!.Select(o => new KeyVersion { Key = o.Key }).ToList(),
                    Quiet = true,
                });
            }
            await adminClient.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket });
        }
        catch (AmazonS3Exception)
        {
        }
        backend.Dispose();
        adminClient.Dispose();
    }

    [Fact]
    public async Task Upload_emits_S3Put_activity_and_records_bytes_uploaded()
    {
        using var capture = TelemetryCapture.Subscribe();

        var payload = "hello, telemetry"u8.ToArray();
        using var ms = new MemoryStream(payload);
        await backend.UploadAsync(
            "telemetry/upload.txt", ms, ifMatchETag: null, CancellationToken.None);

        Assert.Contains(capture.Activities, a => a.OperationName == "S3.Put");
        Assert.Contains(capture.Durations, d => d.Operation == "S3.Put");
        // Bytes-uploaded counter is tagless so we just ensure it captured something
        // matching the payload length.
        Assert.Equal(payload.Length, capture.BytesUploaded);
    }

    [Fact]
    public async Task ReadRange_emits_S3Get_activity_and_records_bytes_downloaded()
    {
        var payload = "0123456789ABCDEF"u8.ToArray();
        using (var seed = new MemoryStream(payload))
        {
            await backend.UploadAsync("blob.bin", seed, ifMatchETag: null, CancellationToken.None);
        }

        using var capture = TelemetryCapture.Subscribe();

        using var ms = new MemoryStream();
        await backend.ReadRangeAsync("blob.bin", 4, 6, ms, CancellationToken.None);

        Assert.Contains(capture.Activities, a => a.OperationName == "S3.Get");
        Assert.Contains(capture.Durations, d => d.Operation == "S3.Get");
        Assert.Equal(6, capture.BytesDownloaded);
    }

    [Fact]
    public async Task List_emits_S3List_activity_with_relative_directory_tag()
    {
        using (var seed = new MemoryStream("x"u8.ToArray()))
        {
            await backend.UploadAsync("dir/inner.txt", seed, ifMatchETag: null, CancellationToken.None);
        }

        using var capture = TelemetryCapture.Subscribe();

        await foreach (var _ in backend.ListAsync(string.Empty, CancellationToken.None))
        {
        }

        var listActivity = Assert.Single(capture.Activities, a => a.OperationName == "S3.List");
        Assert.Contains(listActivity.Tags, t => t.Key == "relative.directory");
    }

    [Fact]
    public async Task Head_emits_S3Head_activity()
    {
        using (var seed = new MemoryStream("h"u8.ToArray()))
        {
            await backend.UploadAsync("head.txt", seed, ifMatchETag: null, CancellationToken.None);
        }

        using var capture = TelemetryCapture.Subscribe();

        await backend.HeadAsync("head.txt", CancellationToken.None);

        Assert.Contains(capture.Activities, a => a.OperationName == "S3.Head");
        Assert.Contains(capture.Durations, d => d.Operation == "S3.Head");
    }

    [Fact]
    public async Task Delete_emits_S3Delete_activity()
    {
        using (var seed = new MemoryStream("d"u8.ToArray()))
        {
            await backend.UploadAsync("doomed.txt", seed, ifMatchETag: null, CancellationToken.None);
        }

        using var capture = TelemetryCapture.Subscribe();

        await backend.DeleteAsync("doomed.txt", CancellationToken.None);

        Assert.Contains(capture.Activities, a => a.OperationName == "S3.Delete");
    }

    [Fact]
    public async Task Rename_emits_outer_S3Rename_with_inner_S3Copy_child()
    {
        using (var seed = new MemoryStream("r"u8.ToArray()))
        {
            await backend.UploadAsync("src.txt", seed, ifMatchETag: null, CancellationToken.None);
        }

        using var capture = TelemetryCapture.Subscribe();

        await backend.RenameAsync("src.txt", "dst.txt", CancellationToken.None);

        var rename = Assert.Single(capture.Activities, a => a.OperationName == "S3.Rename");
        var copy = Assert.Single(capture.Activities, a => a.OperationName == "S3.Copy");
        // The Copy span runs inside Rename, so its parent must be the Rename span.
        Assert.Equal(rename.SpanId, copy.ParentSpanId);
    }

    [Fact]
    public async Task Failed_operation_marks_activity_error_and_increments_error_counter()
    {
        using var capture = TelemetryCapture.Subscribe();

        // ReadRange on a missing key surfaces an AmazonS3Exception out of the SDK;
        // the scope catches it, marks the span as Error, and bumps the counter.
        using var sink = new MemoryStream();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            backend.ReadRangeAsync("missing.bin", 0, 16, sink, CancellationToken.None));

        var get = Assert.Single(capture.Activities, a => a.OperationName == "S3.Get");
        Assert.Equal(ActivityStatusCode.Error, get.Status);
        Assert.Contains(capture.Errors, e => e.Operation == "S3.Get" && e.Value == 1);
    }

    /// <summary>
    /// Subscribes to the OSVFS ActivitySource and Meter and aggregates
    /// recorded samples in memory. Tests dispose the capture to detach
    /// the listeners so cross-suite leaks are impossible.
    /// </summary>
    private sealed class TelemetryCapture : IDisposable
    {
        public List<Activity> Activities { get; } = [];
        public List<Sample<double>> Durations { get; } = [];
        public List<Sample<long>> Errors { get; } = [];
        public long BytesUploaded { get; private set; }
        public long BytesDownloaded { get; private set; }

        private readonly ActivityListener activityListener;
        private readonly MeterListener meterListener;

        private TelemetryCapture()
        {
            activityListener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == OsvfsTelemetry.S3SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    lock (Activities) Activities.Add(activity);
                },
            };
            ActivitySource.AddActivityListener(activityListener);

            meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == OsvfsTelemetry.S3SourceName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                switch (instrument.Name)
                {
                    case "osvfs.s3.bytes_uploaded":
                        BytesUploaded += measurement;
                        break;
                    case "osvfs.s3.bytes_downloaded":
                        BytesDownloaded += measurement;
                        break;
                    case "osvfs.s3.errors_total":
                        lock (Errors) Errors.Add(new Sample<long>(ExtractOperation(tags), measurement));
                        break;
                }
            });
            meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            {
                if (instrument.Name == "osvfs.s3.duration")
                {
                    lock (Durations) Durations.Add(new Sample<double>(ExtractOperation(tags), measurement));
                }
            });
            meterListener.Start();
        }

        public static TelemetryCapture Subscribe() => new();

        public void Dispose()
        {
            activityListener.Dispose();
            meterListener.Dispose();
        }

        private static string ExtractOperation(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "operation") return tag.Value?.ToString() ?? "";
            }
            return "";
        }
    }

    private sealed record Sample<T>(string Operation, T Value);
}
