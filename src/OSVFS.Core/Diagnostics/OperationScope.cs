using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OSVFS.Diagnostics;

/// <summary>
/// One-shot RAII helper that pairs an <see cref="Activity"/> with a
/// duration <see cref="Histogram{T}"/> and an error <see cref="Counter{T}"/>.
/// Intended to be used with <c>using</c>: dispose records the elapsed time
/// keyed by the operation name and, when <see cref="Fail(System.Exception)"/>
/// ran, bumps the error counter for the same operation. Two instrument
/// pipelines share this struct — S3 backend ops emit through
/// <see cref="OsvfsTelemetry.S3"/> + <see cref="OsvfsTelemetry.Duration"/>
/// + <see cref="OsvfsTelemetry.Errors"/>; ProjFS callbacks emit through
/// <see cref="OsvfsTelemetry.ProjFs"/> + <see cref="OsvfsTelemetry.ProjFsDuration"/>
/// + <see cref="OsvfsTelemetry.ProjFsErrors"/>.
/// </summary>
/// <remarks>
/// A struct is chosen over a class so the wrapper allocates on the stack;
/// the only heap object is the <see cref="Activity"/> itself, and that
/// allocation only happens when a listener is attached to the supplied
/// <see cref="ActivitySource"/> (the SDK returns null otherwise). The
/// struct is mutable (non-readonly) because <see cref="Fail(System.Exception)"/>
/// flips the failure flag so the error counter can fire on dispose even when
/// no Activity listener is attached.
/// </remarks>
internal struct OperationScope : IDisposable
{
    /// <summary>
    /// Operation tag value applied to the duration histogram and error
    /// counter. Matches the Activity name so traces and metrics can be
    /// correlated by name.
    /// </summary>
    private readonly string operation;

    /// <summary>
    /// Optional Activity issued by the supplied <see cref="ActivitySource"/>;
    /// null when no listener is attached to the source. Disposed to flush
    /// the span on scope exit.
    /// </summary>
    private readonly Activity? activity;

    /// <summary>
    /// Captured at construction so dispose can compute the elapsed
    /// milliseconds without depending on <see cref="Activity.Duration"/>
    /// (which is null when no listener is attached).
    /// </summary>
    private readonly long startTimestamp;

    /// <summary>
    /// Histogram instrument that receives the elapsed-milliseconds
    /// sample on dispose, tagged with <c>operation</c>.
    /// </summary>
    private readonly Histogram<double> duration;

    /// <summary>
    /// Counter instrument incremented on dispose iff
    /// <see cref="Fail(System.Exception)"/> ran, tagged with <c>operation</c>.
    /// </summary>
    private readonly Counter<long> errors;

    /// <summary>
    /// Set by <see cref="Fail(System.Exception)"/> so the error counter
    /// fires on dispose even when no <see cref="Activity"/> listener is
    /// attached. Tracking it here (rather than on the Activity status)
    /// keeps metrics independent of tracing subscription.
    /// </summary>
    private bool failed;

    private OperationScope(
        string operation,
        Activity? activity,
        long startTimestamp,
        Histogram<double> duration,
        Counter<long> errors)
    {
        this.operation = operation;
        this.activity = activity;
        this.startTimestamp = startTimestamp;
        this.duration = duration;
        this.errors = errors;
        failed = false;
    }

    /// <summary>
    /// Starts a scope for <paramref name="operation"/> against the
    /// supplied tracing/metrics instruments. The Activity is started
    /// with <see cref="ActivityKind.Client"/> for outbound work (S3) or
    /// <see cref="ActivityKind.Internal"/> for in-process callbacks
    /// (ProjFS); the caller picks via <paramref name="kind"/>.
    /// </summary>
    public static OperationScope Start(
        ActivitySource source,
        Histogram<double> duration,
        Counter<long> errors,
        string operation,
        ActivityKind kind = ActivityKind.Client)
    {
        var activity = source.StartActivity(operation, kind);
        return new OperationScope(operation, activity, Stopwatch.GetTimestamp(), duration, errors);
    }

    /// <summary>
    /// Tags the active span with a key/value pair. No-op when no listener
    /// produced an Activity, so callers can record context without a
    /// branch on every site.
    /// </summary>
    public readonly void SetTag(string key, object? value) => activity?.SetTag(key, value);

    /// <summary>
    /// Marks the operation as failed, records the exception on the span
    /// (when one is active), and arms the error-counter increment on
    /// dispose.
    /// </summary>
    public void Fail(Exception ex)
    {
        failed = true;
        if (activity is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity.SetTag("exception.type", ex.GetType().FullName);
        }
    }

    /// <summary>
    /// Records the elapsed duration histogram sample, increments the
    /// error counter when <see cref="Fail(System.Exception)"/> ran, and
    /// disposes the underlying span (when present).
    /// </summary>
    public void Dispose()
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var operationTag = new KeyValuePair<string, object?>("operation", operation);
        duration.Record(elapsedMs, operationTag);
        if (failed)
        {
            errors.Add(1, operationTag);
        }
        activity?.Dispose();
    }
}
