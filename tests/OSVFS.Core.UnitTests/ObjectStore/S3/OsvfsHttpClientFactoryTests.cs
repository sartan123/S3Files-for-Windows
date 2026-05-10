using Amazon.Runtime;
using Amazon.S3;
using OSVFS.ObjectStore.S3;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Xunit;

namespace OSVFS.Core.UnitTests.ObjectStore.S3;

public class OsvfsHttpClientFactoryTests
{
    [Fact]
    public void Sockets_handler_uses_explicit_keepalive_and_pool_settings()
    {
        using var factory = new OsvfsHttpClientFactory(maxConnectionsPerServer: 16);

        var handler = factory.SocketsHandler;
        Assert.Equal(OsvfsHttpClientFactory.DefaultPooledConnectionLifetime, handler.PooledConnectionLifetime);
        Assert.Equal(OsvfsHttpClientFactory.DefaultPooledConnectionIdleTimeout, handler.PooledConnectionIdleTimeout);
        Assert.Equal(16, handler.MaxConnectionsPerServer);
        Assert.True(handler.EnableMultipleHttp2Connections);
    }

    [Fact]
    public void Custom_lifetime_and_idle_timeout_flow_through()
    {
        var lifetime = TimeSpan.FromMinutes(10);
        var idle = TimeSpan.FromMinutes(3);

        using var factory = new OsvfsHttpClientFactory(
            maxConnectionsPerServer: 8,
            pooledConnectionLifetime: lifetime,
            pooledConnectionIdleTimeout: idle);

        Assert.Equal(lifetime, factory.SocketsHandler.PooledConnectionLifetime);
        Assert.Equal(idle, factory.SocketsHandler.PooledConnectionIdleTimeout);
    }

    [Fact]
    public void Same_HttpClient_instance_is_reused_across_calls()
    {
        using var factory = new OsvfsHttpClientFactory(maxConnectionsPerServer: 4);

        var first = factory.CreateHttpClient(new AmazonS3Config());
        var second = factory.CreateHttpClient(new AmazonS3Config());

        Assert.Same(first, second);
    }

    [Fact]
    public void SDK_caching_and_post_use_dispose_are_disabled()
    {
        using var factory = new OsvfsHttpClientFactory(maxConnectionsPerServer: 4);

        // The factory caches itself; the SDK's own cache is bypassed and the
        // SDK must not dispose between requests.
        Assert.False(factory.UseSDKHttpClientCaching(new AmazonS3Config()));
        Assert.False(factory.DisposeHttpClientsAfterUse(new AmazonS3Config()));
    }

    [Fact]
    public async Task Http2_handler_promotes_outbound_request_version_to_2_0()
    {
        // Build the factory with HTTP/2 enabled, then swap the inner sockets
        // handler for a recorder so we can observe the version the
        // DelegatingHandler applied without making a real network call.
        using var factory = new OsvfsHttpClientFactory(
            maxConnectionsPerServer: 4,
            enableHttp2: true);

        var recorder = new VersionRecordingHandler();
        var pipelineRoot = ExtractClientHandler(factory.CreateHttpClient(new AmazonS3Config()));
        // The HTTP/2 handler is the outermost DelegatingHandler; swap its inner.
        var http2Handler = Assert.IsAssignableFrom<DelegatingHandler>(pipelineRoot);
        http2Handler.InnerHandler = recorder;

        using var client = new HttpClient(http2Handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/");
        await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpVersion.Version20, recorder.LastVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, recorder.LastVersionPolicy);
    }

    [Fact]
    public void Http2_disabled_skips_DelegatingHandler_wrapping()
    {
        using var factory = new OsvfsHttpClientFactory(
            maxConnectionsPerServer: 4,
            enableHttp2: false);

        var pipelineRoot = ExtractClientHandler(factory.CreateHttpClient(new AmazonS3Config()));
        Assert.IsType<SocketsHttpHandler>(pipelineRoot);
        Assert.False(factory.Http2Enabled);
    }

    [Fact]
    public void Constructor_rejects_zero_or_negative_max_connections()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OsvfsHttpClientFactory(maxConnectionsPerServer: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OsvfsHttpClientFactory(maxConnectionsPerServer: -1));
    }

    /// <summary>
    /// HttpClient hides its handler behind a private field; reflection is the
    /// only seam available to assert on the inner pipeline shape.
    /// </summary>
    private static HttpMessageHandler ExtractClientHandler(HttpClient client)
    {
        var field = typeof(HttpMessageInvoker).GetField(
            "_handler",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handler = field!.GetValue(client) as HttpMessageHandler;
        Assert.NotNull(handler);
        return handler!;
    }

    /// <summary>
    /// Captures the request <see cref="HttpRequestMessage.Version"/> /
    /// <see cref="HttpRequestMessage.VersionPolicy"/> the pipeline produced
    /// and returns a synthetic 200 so SendAsync completes without a socket.
    /// </summary>
    private sealed class VersionRecordingHandler : HttpMessageHandler
    {
        public Version? LastVersion { get; private set; }
        public HttpVersionPolicy? LastVersionPolicy { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastVersion = request.Version;
            LastVersionPolicy = request.VersionPolicy;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
