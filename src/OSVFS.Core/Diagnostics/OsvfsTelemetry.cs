using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace OSVFS.Diagnostics;

/// <summary>
/// Static surface for OSVFS-emitted observability signals. Centralizing the
/// <see cref="ActivitySource"/> and <see cref="Meter"/> here lets every layer
/// share the same instrument names so dashboards and exporters can be wired
/// up against a single, stable identifier set ("osvfs.s3").
/// </summary>
internal static class OsvfsTelemetry
{
    /// <summary>
    /// Source / meter name shared by tracing and metrics so a single OTel
    /// pipeline subscription captures every S3 backend signal.
    /// </summary>
    public const string S3SourceName = "osvfs.s3";

    /// <summary>
    /// Source / meter name for ProjFS callback signals. Kept separate from
    /// <see cref="S3SourceName"/> so dashboards can distinguish kernel-side
    /// virtualization activity from object-store traffic — the two have
    /// very different cost profiles and failure modes.
    /// </summary>
    public const string ProjFsSourceName = "osvfs.projfs";

    /// <summary>
    /// Assembly version string used to tag the <see cref="ActivitySource"/>
    /// and <see cref="Meter"/>. Falls back to "0.0.0" when the assembly
    /// metadata is absent (e.g. AOT-published binaries that strip
    /// <see cref="AssemblyInformationalVersionAttribute"/>).
    /// </summary>
    private static readonly string Version =
        typeof(OsvfsTelemetry).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "0.0.0";

    /// <summary>
    /// <see cref="ActivitySource"/> emitting per-operation spans (S3.List,
    /// S3.Head, S3.Get, S3.Put, S3.Delete, S3.Copy). Subscribers configure an
    /// <see cref="ActivityListener"/> or an OpenTelemetry tracer provider on
    /// <see cref="S3SourceName"/> to collect these.
    /// </summary>
    public static readonly ActivitySource S3 = new(S3SourceName, Version);

    /// <summary>
    /// <see cref="Meter"/> emitting per-operation metrics
    /// (<see cref="BytesUploaded"/>, <see cref="BytesDownloaded"/>,
    /// <see cref="Errors"/>, <see cref="Duration"/>).
    /// </summary>
    public static readonly Meter S3Meter = new(S3SourceName, Version);

    /// <summary>
    /// Total bytes uploaded across every <c>UploadAsync</c> success.
    /// Reported in bytes so dashboards can convert to MiB / s on display.
    /// </summary>
    public static readonly Counter<long> BytesUploaded = S3Meter.CreateCounter<long>(
        name: "osvfs.s3.bytes_uploaded",
        unit: "By",
        description: "Total bytes successfully uploaded to the S3 backend.");

    /// <summary>
    /// Total bytes downloaded across every <c>ReadRangeAsync</c> success.
    /// </summary>
    public static readonly Counter<long> BytesDownloaded = S3Meter.CreateCounter<long>(
        name: "osvfs.s3.bytes_downloaded",
        unit: "By",
        description: "Total bytes successfully downloaded from the S3 backend.");

    /// <summary>
    /// Total operation failures, partitioned by the <c>operation</c> tag
    /// (List/Head/Get/Put/Delete/Copy) so dashboards can break the rate
    /// down by operation kind.
    /// </summary>
    public static readonly Counter<long> Errors = S3Meter.CreateCounter<long>(
        name: "osvfs.s3.errors_total",
        unit: "{error}",
        description: "Total S3 operation failures, partitioned by operation tag.");

    /// <summary>
    /// Per-operation latency histogram in milliseconds, partitioned by
    /// the <c>operation</c> tag. Recording in milliseconds (rather than
    /// seconds) keeps the buckets useful for the sub-second range that
    /// dominates a typical S3 workload.
    /// </summary>
    public static readonly Histogram<double> Duration = S3Meter.CreateHistogram<double>(
        name: "osvfs.s3.duration",
        unit: "ms",
        description: "Per-operation S3 latency in milliseconds, partitioned by operation tag.");

    /// <summary>
    /// <see cref="ActivitySource"/> emitting per-ProjFS-callback spans
    /// (ProjFS.GetPlaceholderInfo, ProjFS.GetFileData, ProjFS.PreDelete,
    /// …). Subscribers configure an <see cref="ActivityListener"/> or an
    /// OpenTelemetry tracer provider on <see cref="ProjFsSourceName"/> to
    /// collect these.
    /// </summary>
    public static readonly ActivitySource ProjFs = new(ProjFsSourceName, Version);

    /// <summary>
    /// <see cref="Meter"/> emitting per-ProjFS-callback metrics
    /// (<see cref="ProjFsErrors"/>, <see cref="ProjFsDuration"/>).
    /// </summary>
    public static readonly Meter ProjFsMeter = new(ProjFsSourceName, Version);

    /// <summary>
    /// Total ProjFS callback failures, partitioned by the <c>operation</c>
    /// tag (GetPlaceholderInfo / GetFileData / PreDelete / …). A high
    /// rate here typically points at the object-store backend rather
    /// than ProjFS itself; check <see cref="Errors"/> in tandem.
    /// </summary>
    public static readonly Counter<long> ProjFsErrors = ProjFsMeter.CreateCounter<long>(
        name: "osvfs.projfs.errors_total",
        unit: "{error}",
        description: "Total ProjFS callback failures, partitioned by operation tag.");

    /// <summary>
    /// Per-callback latency histogram in milliseconds, partitioned by
    /// the <c>operation</c> tag. Slow placeholder hydration usually
    /// shows up here first; the corresponding S3 span is the next stop.
    /// </summary>
    public static readonly Histogram<double> ProjFsDuration = ProjFsMeter.CreateHistogram<double>(
        name: "osvfs.projfs.duration",
        unit: "ms",
        description: "Per-callback ProjFS latency in milliseconds, partitioned by operation tag.");

    /// <summary>
    /// Starts an <see cref="OperationScope"/> bound to the S3 telemetry
    /// pipeline. Used by every <c>S3.&lt;name&gt;</c> instrumentation site
    /// in the backend.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OperationScope StartS3Operation(string operation) =>
        OperationScope.Start(S3, Duration, Errors, operation, ActivityKind.Client);

    /// <summary>
    /// Starts an <see cref="OperationScope"/> bound to the ProjFS
    /// telemetry pipeline. Used by every <c>ProjFS.&lt;name&gt;</c>
    /// callback site. Spans are tagged <see cref="ActivityKind.Internal"/>
    /// because the work is inside the OSVFS host process; outbound S3
    /// children attach as <see cref="ActivityKind.Client"/> below them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OperationScope StartProjFsOperation(string operation) =>
        OperationScope.Start(ProjFs, ProjFsDuration, ProjFsErrors, operation, ActivityKind.Internal);
}
