using Amazon.Runtime;
using System.Net;
using System.Net.Http;

namespace OSVFS.ObjectStore.S3;

/// <summary>
/// AWS SDK <see cref="HttpClientFactory"/> that produces a single shared
/// <see cref="HttpClient"/> backed by an explicitly-tuned
/// <see cref="SocketsHttpHandler"/>. The defaults the SDK would otherwise
/// inherit from <c>SocketsHttpHandler</c> leave <c>PooledConnectionLifetime</c>
/// at <see cref="Timeout.InfiniteTimeSpan"/> (so DNS changes are never picked
/// up) and <c>MaxConnectionsPerServer</c> at <see cref="int.MaxValue"/> (which
/// can starve the host of file descriptors under burst load). This factory
/// pins both to operationally-safe values and opts the connection pool into
/// multiple HTTP/2 connections so a single AmazonS3Client can sustain
/// long-lived high-throughput sessions.
/// </summary>
internal sealed class OsvfsHttpClientFactory : HttpClientFactory, IDisposable
{
    /// <summary>
    /// Default lifetime for a pooled connection. Forcing the pool to discard
    /// idle connections every 5 minutes lets the SDK pick up DNS changes
    /// (e.g. S3 endpoint rotation, VPC endpoint failover) without a process
    /// restart.
    /// </summary>
    public static readonly TimeSpan DefaultPooledConnectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default idle timeout for a pooled connection. Connections idle longer
    /// than this are closed so the host releases TCP slots promptly when a
    /// burst of traffic ends.
    /// </summary>
    public static readonly TimeSpan DefaultPooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly SocketsHttpHandler socketsHandler;

    private readonly HttpClient httpClient;

    /// <summary>
    /// The underlying <see cref="SocketsHttpHandler"/>. Exposed for unit tests
    /// to assert on the handler configuration; not part of the public API.
    /// </summary>
    internal SocketsHttpHandler SocketsHandler => socketsHandler;

    /// <summary>
    /// True when the factory wraps requests with a handler that opts the
    /// outbound <see cref="HttpRequestMessage.Version"/> up to HTTP/2 (with
    /// fall-back to HTTP/1.1 via
    /// <see cref="HttpVersionPolicy.RequestVersionOrLower"/>).
    /// </summary>
    internal bool Http2Enabled { get; }

    /// <summary>
    /// Creates a factory that produces one shared <see cref="HttpClient"/>.
    /// <paramref name="maxConnectionsPerServer"/> caps the SDK's per-host
    /// connection pool so it cannot exceed the OSVFS in-flight ceilings.
    /// <paramref name="enableHttp2"/> controls whether outbound requests are
    /// promoted to HTTP/2 (with HTTP/1.1 fall-back).
    /// <paramref name="pooledConnectionLifetime"/> /
    /// <paramref name="pooledConnectionIdleTimeout"/> override the lifetime
    /// knobs; null falls back to the static defaults above.
    /// </summary>
    public OsvfsHttpClientFactory(
        int maxConnectionsPerServer,
        bool enableHttp2 = true,
        TimeSpan? pooledConnectionLifetime = null,
        TimeSpan? pooledConnectionIdleTimeout = null)
    {
        if (maxConnectionsPerServer < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConnectionsPerServer),
                maxConnectionsPerServer,
                "Must be >= 1.");
        }

        socketsHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = pooledConnectionLifetime ?? DefaultPooledConnectionLifetime,
            PooledConnectionIdleTimeout = pooledConnectionIdleTimeout ?? DefaultPooledConnectionIdleTimeout,
            MaxConnectionsPerServer = maxConnectionsPerServer,
            // Without this, the pool is capped at one HTTP/2 connection per
            // origin even when SETTINGS_MAX_CONCURRENT_STREAMS is exhausted —
            // a hard ceiling we don't want for S3 burst traffic.
            EnableMultipleHttp2Connections = true,
            // S3 endpoints support gzip; let the SDK trade a bit of CPU for
            // smaller list-page payloads.
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        Http2Enabled = enableHttp2;
        HttpMessageHandler outerHandler = enableHttp2
            ? new Http2RequestVersionHandler { InnerHandler = socketsHandler }
            : socketsHandler;

        httpClient = new HttpClient(outerHandler, disposeHandler: true);
    }

    /// <inheritdoc/>
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => httpClient;

    /// <summary>
    /// Returning <c>false</c> tells the SDK not to cache the
    /// <see cref="HttpClient"/> in its internal map: the factory already owns
    /// and reuses the single shared instance for the lifetime of the
    /// <see cref="S3Backend"/>.
    /// </summary>
    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    /// <summary>
    /// Returning <c>false</c> stops the SDK from disposing the shared
    /// <see cref="HttpClient"/> after each request — disposal is handled
    /// when <see cref="Dispose"/> runs at backend teardown.
    /// </summary>
    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

    /// <summary>
    /// Disposes the shared <see cref="HttpClient"/> (and, transitively, the
    /// underlying <see cref="SocketsHttpHandler"/>).
    /// </summary>
    public void Dispose() => httpClient.Dispose();

    /// <summary>
    /// <see cref="DelegatingHandler"/> that promotes outbound requests to
    /// <see cref="HttpVersion.Version20"/> with
    /// <see cref="HttpVersionPolicy.RequestVersionOrLower"/>. The policy lets
    /// the connection negotiate down to HTTP/1.1 transparently when the
    /// remote endpoint does not advertise <c>h2</c> via ALPN, so flipping
    /// the knob is safe even against S3-compatible servers (LocalStack,
    /// MinIO) that may speak only HTTP/1.1.
    /// </summary>
    private sealed class Http2RequestVersionHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
