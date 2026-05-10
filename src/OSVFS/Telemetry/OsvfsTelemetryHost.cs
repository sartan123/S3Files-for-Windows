using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OSVFS.Configuration;
using OSVFS.Diagnostics;

namespace OSVFS.Telemetry;

/// <summary>
/// Builds and owns the OpenTelemetry pipeline (TracerProvider +
/// MeterProvider) for an OSVFS process invocation. Disposing the host
/// flushes pending spans / metrics through the OTLP exporter and tears
/// the providers down.
/// </summary>
internal sealed class OsvfsTelemetryHost : IDisposable
{
    /// <summary>
    /// Default <c>service.name</c> resource attribute when the operator
    /// does not override it via <c>[telemetry] service-name</c>. Picked
    /// to match the assembly / executable name so dashboards remain
    /// readable out of the box.
    /// </summary>
    public const string DefaultServiceName = "osvfs";

    /// <summary>
    /// Backing TracerProvider. Disposed by <see cref="Dispose"/>.
    /// </summary>
    private readonly TracerProvider tracerProvider;

    /// <summary>
    /// Backing MeterProvider. Disposed by <see cref="Dispose"/>.
    /// </summary>
    private readonly MeterProvider meterProvider;

    private OsvfsTelemetryHost(TracerProvider tracerProvider, MeterProvider meterProvider)
    {
        this.tracerProvider = tracerProvider;
        this.meterProvider = meterProvider;
    }

    /// <summary>
    /// Builds the OTel pipeline against <paramref name="config"/>. Returns
    /// null when telemetry is disabled (no endpoint configured) so the
    /// caller can short-circuit without paying the SDK initialization
    /// cost.
    /// </summary>
    public static OsvfsTelemetryHost? Create(OsvfsTelemetryConfig? config)
    {
        var endpoint = config?.OtlpEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new OsvfsConfigException(
                $"telemetry otlp-endpoint '{endpoint}' is not a valid absolute URI.");
        }

        var protocol = (config?.OtlpProtocol ?? OtlpProtocolKind.Grpc) switch
        {
            OtlpProtocolKind.HttpProtobuf => OtlpExportProtocol.HttpProtobuf,
            _ => OtlpExportProtocol.Grpc,
        };
        var serviceName = string.IsNullOrWhiteSpace(config?.ServiceName)
            ? DefaultServiceName
            : config!.ServiceName!;

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .ConfigureResource(b => b.AddService(serviceName))
            .AddSource(OsvfsTelemetry.S3SourceName)
            .AddSource(OsvfsTelemetry.ProjFsSourceName)
            .AddOtlpExporter(opt =>
            {
                opt.Endpoint = endpointUri;
                opt.Protocol = protocol;
            })
            .Build()!;

        var meterProvider = Sdk.CreateMeterProviderBuilder()
            .ConfigureResource(b => b.AddService(serviceName))
            .AddMeter(OsvfsTelemetry.S3SourceName)
            .AddMeter(OsvfsTelemetry.ProjFsSourceName)
            .AddOtlpExporter(opt =>
            {
                opt.Endpoint = endpointUri;
                opt.Protocol = protocol;
            })
            .Build()!;

        return new OsvfsTelemetryHost(tracerProvider, meterProvider);
    }

    /// <summary>
    /// Resolves the effective <see cref="OsvfsTelemetryConfig"/> from the
    /// CLI override <paramref name="cliOtlpEndpoint"/> layered on top of
    /// the file-derived <paramref name="fileConfig"/>. Returns null when
    /// neither source supplies an endpoint so the caller can skip
    /// pipeline construction entirely.
    /// </summary>
    public static OsvfsTelemetryConfig? ResolveEffectiveConfig(
        OsvfsTelemetryConfig? fileConfig, string? cliOtlpEndpoint)
    {
        if (string.IsNullOrWhiteSpace(cliOtlpEndpoint))
        {
            return fileConfig;
        }
        // The CLI override only carries an endpoint; preserve protocol /
        // service-name from the file when both are configured at once.
        return new OsvfsTelemetryConfig
        {
            OtlpEndpoint = cliOtlpEndpoint,
            OtlpProtocol = fileConfig?.OtlpProtocol,
            ServiceName = fileConfig?.ServiceName,
        };
    }

    /// <summary>
    /// Disposes the providers in reverse build order so spans flush
    /// before metrics; both calls swallow exceptions so a noisy
    /// shutdown cannot mask the host's exit code.
    /// </summary>
    public void Dispose()
    {
        tracerProvider.Dispose();
        meterProvider.Dispose();
    }
}
