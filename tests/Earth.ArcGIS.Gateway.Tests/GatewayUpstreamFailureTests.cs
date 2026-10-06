using System.Net;
using System.Security.Claims;
using System.Text;
using Earth.ArcGIS.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class GatewayUpstreamFailureTests
{
    private const string Path =
        "arcgis/rest/services/Land/Parcels/FeatureServer/0/query";

    [Fact]
    public async Task NetworkFailureReturns502AndTelemetryReason()
    {
        var queue = new TelemetryQueue();
        var context = CreateContext();

        await ExecuteAsync(
            context,
            new HttpClient(new NetworkFailureHandler()),
            queue);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal("upstream_unavailable", Assert.Single(queue.GetRecent(10)).ReasonCode);
    }

    [Fact]
    public async Task HttpClientTimeoutReturns504AndTelemetryReason()
    {
        var queue = new TelemetryQueue();
        var context = CreateContext();
        var http = new HttpClient(new SlowHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(50)
        };

        await ExecuteAsync(context, http, queue);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
        Assert.Equal("upstream_timeout", Assert.Single(queue.GetRecent(10)).ReasonCode);
    }

    [Fact]
    public async Task MidStreamIOExceptionIsControlledAndAudited()
    {
        var queue = new TelemetryQueue();
        var context = CreateContext();

        await ExecuteAsync(
            context,
            new HttpClient(new StreamFailureHandler()),
            queue);

        var failure = Assert.Single(queue.GetRecent(10));
        Assert.Equal("upstream_stream_failure", failure.ReasonCode);
        Assert.Equal("DENY", failure.Decision);
    }

    private static async Task ExecuteAsync(
        DefaultHttpContext context,
        HttpClient http,
        ITelemetryQueue queue)
    {
        using var upstreamGate = new ArcGisUpstreamGate(4);
        await GatewayHandler.HandleAsync(
            context,
            Path,
            new StubHttpClientFactory(http),
            new StaticCredentialProvider(),
            Options.Create(new GatewayOptions
            {
                ArcGisBaseUrl = "https://arcgis.test",
                AllowedPathPrefixes = ["/arcgis/rest/services/Land/Parcels"]
            }),
            Options.Create(new ProtectionOptions()),
            new StaticApplicationResolver(),
            new ArcGisResourceResolver(),
            new ArcGisOperationPolicy(),
            new RequestActivityClassifier(new GisCostOptions()),
            new AllowRateLimiter(),
            new AllowConcurrencyGate(),
            upstreamGate,
            new UserBlockStore(),
            new AllowAccessPolicy(),
            queue,
            NullLoggerFactory.Instance);
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "cid-upstream-failure";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "sub-1"),
            new Claim("tenant_id", "tenant-1")
        ], "test"));
        context.Request.Method = "GET";
        context.Request.QueryString = new QueryString("?returnCountOnly=true&f=json");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticCredentialProvider : IArcGisCredentialProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("server-token");
        public void InvalidateToken() { }
    }

    private sealed class StaticApplicationResolver : IApplicationIdentityResolver
    {
        public bool TryResolve(
            ClaimsPrincipal principal,
            out GatewayApplication application)
        {
            application = new GatewayApplication(
                "jtuwma",
                "https://policy.test/authorize");
            return true;
        }
    }

    private sealed class AllowAccessPolicy : IAccessPolicyClient
    {
        public Task<AccessPolicyDecision> AuthorizeAsync(
            GatewayApplication application,
            AccessPolicyRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AccessPolicyDecision(true, "allow", "v1"));
    }

    private sealed class AllowRateLimiter : IUserActivityRateLimiter
    {
        public ValueTask<UserRateDecision> ConsumeAsync(
            UserActivityRateKey key,
            RequestActivity activity,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new UserRateDecision(
                true,
                100,
                100,
                0,
                "rate_allow",
                "test"));
    }

    private sealed class AllowConcurrencyGate : IUserConcurrencyGate
    {
        public ValueTask<IUserConcurrencyLease?> TryEnterAsync(
            UserConcurrencyKey key,
            bool heavy,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IUserConcurrencyLease?>(new Lease());

        private sealed class Lease : IUserConcurrencyLease
        {
            public string Class => "interactive";
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NetworkFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated upstream failure");
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            };
        }
    }

    private sealed class StreamFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowAfterFirstReadStream())
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowAfterFirstReadStream : Stream
    {
        private readonly byte[] data = Encoding.UTF8.GetBytes("partial");
        private bool readOnce;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (readOnce)
                throw new IOException("simulated stream failure");
            readOnce = true;
            var size = Math.Min(count, data.Length);
            Array.Copy(data, 0, buffer, offset, size);
            return size;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (readOnce)
                throw new IOException("simulated stream failure");
            readOnce = true;
            var size = Math.Min(buffer.Length, data.Length);
            data.AsMemory(0, size).CopyTo(buffer);
            return ValueTask.FromResult(size);
        }
    }
}
