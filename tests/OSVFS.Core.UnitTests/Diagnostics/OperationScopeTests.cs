using OSVFS.Diagnostics;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Xunit;

namespace OSVFS.Core.UnitTests.Diagnostics;

/// <summary>
/// Verifies that <see cref="OperationScope"/> emits one Activity, records
/// one duration histogram sample, and only bumps the error counter when
/// <see cref="OperationScope.Fail"/> ran. Both telemetry pipelines (S3
/// outbound + ProjFS internal) share the same scope helper, so the tests
/// drive each via its <c>OsvfsTelemetry.Start...Operation</c> factory.
/// The tests subscribe via the raw <see cref="ActivityListener"/> /
/// <see cref="MeterListener"/> APIs (rather than pulling in OpenTelemetry
/// test exporters) because the production telemetry surface is exactly
/// those two BCL types.
/// </summary>
public class OperationScopeTests
{
    [Fact]
    public void S3_successful_scope_emits_activity_and_duration_but_no_error()
    {
        using var listener = SubscribeActivities(OsvfsTelemetry.S3SourceName);
        using var meter = SubscribeMeter(OsvfsTelemetry.S3SourceName, "osvfs.s3.duration", "osvfs.s3.errors_total");

        using (var scope = OsvfsTelemetry.StartS3Operation("S3.UnitTest"))
        {
            scope.SetTag("relative.path", "foo/bar.txt");
        }

        var activity = Assert.Single(listener.Activities);
        Assert.Equal("S3.UnitTest", activity.OperationName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Contains(activity.Tags, t => t is { Key: "relative.path", Value: "foo/bar.txt" });

        var durationSample = Assert.Single(meter.Durations);
        Assert.Equal("S3.UnitTest", durationSample.Operation);
        Assert.True(durationSample.Value >= 0);

        Assert.Empty(meter.Errors);
    }

    [Fact]
    public void S3_failed_scope_increments_error_counter_and_marks_activity_error()
    {
        using var listener = SubscribeActivities(OsvfsTelemetry.S3SourceName);
        using var meter = SubscribeMeter(OsvfsTelemetry.S3SourceName, "osvfs.s3.duration", "osvfs.s3.errors_total");

        using (var scope = OsvfsTelemetry.StartS3Operation("S3.UnitTest"))
        {
            scope.Fail(new InvalidOperationException("boom"));
        }

        var activity = Assert.Single(listener.Activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);

        var durationSample = Assert.Single(meter.Durations);
        Assert.Equal("S3.UnitTest", durationSample.Operation);

        var errorSample = Assert.Single(meter.Errors);
        Assert.Equal("S3.UnitTest", errorSample.Operation);
        Assert.Equal(1, errorSample.Value);
    }

    [Fact]
    public void S3_scope_records_metrics_even_without_listener_attached()
    {
        // The Meter is independent of the ActivitySource. With no ActivityListener
        // attached, StartActivity returns null but the duration / error counters
        // must still record their samples.
        using var meter = SubscribeMeter(OsvfsTelemetry.S3SourceName, "osvfs.s3.duration", "osvfs.s3.errors_total");

        using (var scope = OsvfsTelemetry.StartS3Operation("S3.UnlistenedActivity"))
        {
            scope.Fail(new IOException("boom"));
        }

        Assert.Single(meter.Durations);
        Assert.Single(meter.Errors);
    }

    [Fact]
    public void ProjFs_successful_scope_emits_internal_kind_activity_on_projfs_source()
    {
        using var listener = SubscribeActivities(OsvfsTelemetry.ProjFsSourceName);
        using var meter = SubscribeMeter(
            OsvfsTelemetry.ProjFsSourceName, "osvfs.projfs.duration", "osvfs.projfs.errors_total");

        using (var scope = OsvfsTelemetry.StartProjFsOperation("ProjFS.UnitTest"))
        {
            scope.SetTag("relative.path", "callback/file.txt");
        }

        var activity = Assert.Single(listener.Activities);
        Assert.Equal("ProjFS.UnitTest", activity.OperationName);
        // ProjFS callbacks are in-process work, not outbound calls.
        Assert.Equal(ActivityKind.Internal, activity.Kind);

        var durationSample = Assert.Single(meter.Durations);
        Assert.Equal("ProjFS.UnitTest", durationSample.Operation);
        Assert.Empty(meter.Errors);
    }

    [Fact]
    public void ProjFs_failed_scope_routes_error_to_projfs_counter()
    {
        using var meter = SubscribeMeter(
            OsvfsTelemetry.ProjFsSourceName, "osvfs.projfs.duration", "osvfs.projfs.errors_total");

        using (var scope = OsvfsTelemetry.StartProjFsOperation("ProjFS.UnitTest"))
        {
            scope.Fail(new IOException("io"));
        }

        var error = Assert.Single(meter.Errors);
        Assert.Equal("ProjFS.UnitTest", error.Operation);
        Assert.Equal(1, error.Value);
    }

    [Fact]
    public void ProjFs_and_S3_pipelines_emit_to_separate_meters()
    {
        // A ProjFS-pipeline scope must NOT contribute to the S3 meter, and
        // vice versa. Subscribing to only one pipeline's instruments confirms
        // OsvfsTelemetry routes the right Histogram / Counter into the
        // OperationScope each time.
        using var s3Meter = SubscribeMeter(
            OsvfsTelemetry.S3SourceName, "osvfs.s3.duration", "osvfs.s3.errors_total");
        using var projFsMeter = SubscribeMeter(
            OsvfsTelemetry.ProjFsSourceName, "osvfs.projfs.duration", "osvfs.projfs.errors_total");

        using (var s = OsvfsTelemetry.StartS3Operation("S3.X")) { }
        using (var p = OsvfsTelemetry.StartProjFsOperation("ProjFS.Y")) { }

        Assert.Single(s3Meter.Durations);
        Assert.Equal("S3.X", s3Meter.Durations[0].Operation);
        Assert.Single(projFsMeter.Durations);
        Assert.Equal("ProjFS.Y", projFsMeter.Durations[0].Operation);
    }

    private static ActivityCapture SubscribeActivities(string sourceName)
    {
        var capture = new ActivityCapture();
        capture.Listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = capture.Activities.Add,
        };
        ActivitySource.AddActivityListener(capture.Listener);
        return capture;
    }

    private static MeterCapture SubscribeMeter(string meterName, string durationName, string errorsName)
    {
        var capture = new MeterCapture();
        capture.Listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name != meterName) return;
                listener.EnableMeasurementEvents(instrument);
            },
        };
        capture.Listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == errorsName)
            {
                capture.Errors.Add(new Sample<long>(ExtractOperationTag(tags), measurement));
            }
        });
        capture.Listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == durationName)
            {
                capture.Durations.Add(new Sample<double>(ExtractOperationTag(tags), measurement));
            }
        });
        capture.Listener.Start();
        return capture;
    }

    private static string ExtractOperationTag(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "operation") return tag.Value?.ToString() ?? "";
        }
        return "";
    }

    private sealed class ActivityCapture : IDisposable
    {
        public ActivityListener Listener { get; set; } = null!;
        public List<Activity> Activities { get; } = [];

        public void Dispose() => Listener.Dispose();
    }

    private sealed class MeterCapture : IDisposable
    {
        public MeterListener Listener { get; set; } = null!;
        public List<Sample<double>> Durations { get; } = [];
        public List<Sample<long>> Errors { get; } = [];

        public void Dispose() => Listener.Dispose();
    }

    private sealed record Sample<T>(string Operation, T Value);
}
