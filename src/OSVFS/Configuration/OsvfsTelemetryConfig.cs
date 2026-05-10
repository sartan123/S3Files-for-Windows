namespace OSVFS.Configuration;

/// <summary>
/// Strongly-typed view of the <c>[telemetry]</c> table inside
/// <c>osvfs.toml</c>. All fields are nullable so the loader can
/// distinguish "not set" from "explicitly disabled" and so
/// <see cref="MergeOverlay"/> can fold per-key overrides without
/// clobbering an unrelated key.
/// </summary>
internal sealed class OsvfsTelemetryConfig
{
    /// <summary>
    /// Destination endpoint for the OTLP exporter (e.g.
    /// <c>http://localhost:4317</c> for gRPC, <c>http://localhost:4318</c>
    /// for HTTP/Protobuf). Null leaves OTel disabled — the host will not
    /// build a TracerProvider/MeterProvider when this is empty.
    /// </summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>
    /// Wire protocol for the OTLP exporter. Null falls back to
    /// <see cref="OtlpProtocolKind.Grpc"/>, matching OpenTelemetry's SDK
    /// default.
    /// </summary>
    public OtlpProtocolKind? OtlpProtocol { get; init; }

    /// <summary>
    /// <c>service.name</c> attribute attached to every emitted span /
    /// metric. Identifies the OSVFS process inside Jaeger / Tempo /
    /// Prometheus dashboards. Null falls back to <c>"osvfs"</c>.
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Returns a copy of this config with non-null values from
    /// <paramref name="overlay"/> winning per-key. Used by the layered
    /// config loader (exe-adjacent → user-global → CLI <c>--config</c>).
    /// </summary>
    public OsvfsTelemetryConfig MergeOverlay(OsvfsTelemetryConfig overlay) => new()
    {
        OtlpEndpoint = overlay.OtlpEndpoint ?? OtlpEndpoint,
        OtlpProtocol = overlay.OtlpProtocol ?? OtlpProtocol,
        ServiceName = overlay.ServiceName ?? ServiceName,
    };
}

/// <summary>
/// Wire protocol selector for the OTLP exporter. Mirrors the choices
/// exposed by the OpenTelemetry .NET SDK (<c>OtlpExportProtocol</c>).
/// </summary>
internal enum OtlpProtocolKind
{
    /// <summary>
    /// gRPC over HTTP/2 — the OTel SDK default. Default endpoint port is
    /// 4317.
    /// </summary>
    Grpc,

    /// <summary>
    /// Protobuf over HTTP/1.1. Default endpoint port is 4318. Pick this
    /// when the collector or the network does not allow HTTP/2.
    /// </summary>
    HttpProtobuf,
}
