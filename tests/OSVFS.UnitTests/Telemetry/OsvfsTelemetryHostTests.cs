using OSVFS.Configuration;
using OSVFS.Telemetry;
using Xunit;

namespace OSVFS.UnitTests.Telemetry;

/// <summary>
/// Verifies the small amount of resolution and validation logic the
/// telemetry host owns. The OpenTelemetry pipeline itself is not built
/// here because the OTLP exporter would attempt to dial out on construction;
/// integration coverage lives next to the S3 backend tests.
/// </summary>
public class OsvfsTelemetryHostTests
{
    [Fact]
    public void Create_returns_null_when_no_endpoint_configured()
    {
        Assert.Null(OsvfsTelemetryHost.Create(null));
        Assert.Null(OsvfsTelemetryHost.Create(new OsvfsTelemetryConfig()));
        Assert.Null(OsvfsTelemetryHost.Create(new OsvfsTelemetryConfig { OtlpEndpoint = "" }));
        Assert.Null(OsvfsTelemetryHost.Create(new OsvfsTelemetryConfig { OtlpEndpoint = "   " }));
    }

    [Fact]
    public void Create_throws_when_endpoint_is_not_an_absolute_uri()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsTelemetryHost.Create(new OsvfsTelemetryConfig { OtlpEndpoint = "not-a-uri" }));
        Assert.Contains("not-a-uri", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveEffectiveConfig_returns_file_when_no_cli_override()
    {
        var fileConfig = new OsvfsTelemetryConfig
        {
            OtlpEndpoint = "http://collector:4317",
            OtlpProtocol = OtlpProtocolKind.Grpc,
            ServiceName = "from-file",
        };

        var effective = OsvfsTelemetryHost.ResolveEffectiveConfig(fileConfig, null);

        Assert.Same(fileConfig, effective);
    }

    [Fact]
    public void ResolveEffectiveConfig_substitutes_cli_endpoint_but_keeps_file_protocol_and_service()
    {
        var fileConfig = new OsvfsTelemetryConfig
        {
            OtlpEndpoint = "http://from-file:4317",
            OtlpProtocol = OtlpProtocolKind.HttpProtobuf,
            ServiceName = "preserved-service",
        };

        var effective = OsvfsTelemetryHost.ResolveEffectiveConfig(fileConfig, "http://from-cli:4318");

        Assert.NotNull(effective);
        Assert.Equal("http://from-cli:4318", effective!.OtlpEndpoint);
        Assert.Equal(OtlpProtocolKind.HttpProtobuf, effective.OtlpProtocol);
        Assert.Equal("preserved-service", effective.ServiceName);
    }

    [Fact]
    public void ResolveEffectiveConfig_returns_null_when_neither_source_supplies_endpoint()
    {
        Assert.Null(OsvfsTelemetryHost.ResolveEffectiveConfig(null, null));
        Assert.Null(OsvfsTelemetryHost.ResolveEffectiveConfig(null, "   "));
    }
}
