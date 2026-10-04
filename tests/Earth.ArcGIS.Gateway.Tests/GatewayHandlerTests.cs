using System.Net;
using System.Security.Claims;
using System.Text;
using Earth.ArcGIS.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class GatewayHandlerTests
{
    private const string Path =
        "arcgis/rest/services/Land/Parcels/FeatureServer/0/query";

    [Fact]
    public async Task AccessDenyNeverCallsCredentialProviderOrArcGis()
    {
        var upstream = new RecordingHandler();
        var credentials = new FakeCredentialProvider();
        var access = new FakeAccessPolicyClient(
            new AccessPolicyDecision(false, "layer_denied", "v1"));
        var rate = new FakeRateLimiter();

        var context = CreateContext("GET");

        await HandleAsync(context, upstream, credentials, access, rate);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(1, access.Calls);
        Assert.Equal(0, credentials.GetCalls);
        Assert.Equal(0, upstream.Requests.Count);
    }

    [Fact]
    public async Task OversizedPostIsRejectedBeforeRateAccessAndArcGis()
    {
        var upstream = new RecordingHandler();
        var credentials = new FakeCredentialProvider();
        var access = new FakeAccessPolicyClient(
            new AccessPolicyDecision(true, "allow", "v1"));
        var rate = new FakeRateLimiter();

        var context = CreateContext("POST");
        context.Request.ContentLength = 2 * 1024 * 1024 + 1;

        await HandleAsync(context, upstream, credentials, access, rate);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Equal(0, rate.Calls);
        Assert.Equal(0, access.Calls);
        Assert.Equal(0, credentials.GetCalls);
        Assert.Equal(0, upstream.Requests.Count);
    }

    [Fact]
    public async Task AuthFailureRetriesOnceAndReplaysPostBodyExactly()
    {
        var upstream = new RecordingHandler(
            Json("""{"error":{"code":498}}"""),
            Json("""{"features":[]}"""));
        var credentials = new FakeCredentialProvider("token-1", "token-2");
        var access = new FakeAccessPolicyClient(
            new AccessPolicyDecision(true, "allow", "v1"));
        var rate = new FakeRateLimiter();

        var body = "where=1%3D1&outFields=*&f=json";
        var context = CreateContext("POST", body);

        await HandleAsync(context, upstream, credentials, access, rate);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(2, upstream.Requests.Count);
        Assert.All(upstream.Requests, request => Assert.Equal(body, request.Body));
        Assert.Equal("Bearer token-1", upstream.Requests[0].EsriAuthorization);
        Assert.Equal("Bearer token-2", upstream.Requests[1].EsriAuthorization);
        Assert.Equal(2, credentials.GetCalls);
        Assert.Equal(1, credentials.InvalidateCalls);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        Assert.Equal("""{"features":[]}""", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task SecondAuthFailureIsReturnedWithoutRetryLoop()
    {
        var upstream = new RecordingHandler(
            Json("""{"error":{"code":"498"}}"""),
            Json("""{"error":{"code":499}}"""));
        var credentials = new FakeCredentialProvider("token-1", "token-2");
        var access = new FakeAccessPolicyClient(
            new AccessPolicyDecision(true, "allow", "v1"));
        var rate = new FakeRateLimiter();

        var context = CreateContext("POST", "f=json");

        await HandleAsync(context, upstream, credentials, access, rate);

        Assert.Equal(2, upstream.Requests.Count);
        Assert.Equal(2, credentials.GetCalls);
        Assert.Equal(1, credentials.InvalidateCalls);
    }

    [Fact]
    public async Task PrefixComparisonRequiresPathBoundary()
    {
        var upstream = new RecordingHandler();
        var credentials = new FakeCredentialProvider();
        var access = new FakeAccessPolicyClient(
            new AccessPolicyDecision(true, "allow", "v1"));
        var rate = new FakeRateLimiter();
        var context = CreateContext("GET");

        await HandleAsync(
            context,
            upstream,
            credentials,
            access,
            rate,
            allowedPrefix: "/arcgis/rest/services/Land/Parcel");

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(0, rate.Calls);
        Assert.Equal(0, access.Calls);
        Assert.Equal(0, credentials.GetCalls);
        Assert.Equal(0, upstream.Requests.Count);
    }

    private static async Task HandleAsync(
        DefaultHttpContext context,
        RecordingHandler upstream,
        FakeCredentialProvider credentials,
        FakeAccessPolicyClient access,
        FakeRateLimiter rate,
        string allowedPrefix = "/arcgis/rest/services/Land/Parcels")
    {
        var http = new HttpClient(upstream);
        await GatewayHandler.HandleAsync(
            context,
            Path,
            new StubHttpClientFactory(http),
            credentials,
            Options.Create(new GatewayOptions
            {
                ArcGisBaseUrl = "https://arcgis.test",
                AllowedPathPrefixes = [allowedPrefix]
            }),
            new FakeApplicationResolver(),
            new ArcGisResourceResolver(),
            new ArcGisOperationPolicy(),
            new RateCostPolicy(),
            rate,
            access,
            new TelemetryQueue(),
            NullLoggerFactory.Instance);
    }

    private static DefaultHttpContext CreateContext(string method, string? body = null)
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "cid-1";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "sub-1"),
            new Claim("tenant_id", "tenant-1")
        ], "test"));
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();

        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/x-www-form-urlencoded";
        }

        return context;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class FakeApplicationResolver : IApplicationIdentityResolver
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

    private sealed class FakeRateLimiter : IGatewayRateLimiter
    {
        public int Calls { get; private set; }

        public ValueTask<RateLimitDecision> ConsumeAsync(
            RateLimitKey key,
            int units,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(
                new RateLimitDecision(true, 100, 0, "rate_allow"));
        }
    }

    private sealed class FakeAccessPolicyClient(AccessPolicyDecision decision)
        : IAccessPolicyClient
    {
        public int Calls { get; private set; }

        public Task<AccessPolicyDecision> AuthorizeAsync(
            GatewayApplication application,
            AccessPolicyRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(decision);
        }
    }

    private sealed class FakeCredentialProvider(params string[] tokens)
        : IArcGisCredentialProvider
    {
        private int tokenIndex;

        public int GetCalls { get; private set; }
        public int InvalidateCalls { get; private set; }

        public Task<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            GetCalls++;
            var value = tokens.Length == 0
                ? "token"
                : tokens[Math.Min(tokenIndex++, tokens.Length - 1)];
            return Task.FromResult(value);
        }

        public void InvalidateToken() => InvalidateCalls++;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private int responseIndex;
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var authorization = request.Headers.TryGetValues(
                "X-Esri-Authorization",
                out var values)
                ? values.Single()
                : null;

            Requests.Add(new RecordedRequest(body, authorization));

            if (responses.Length == 0)
                throw new InvalidOperationException("ArcGIS upstream should not have been called.");

            var index = Math.Min(responseIndex++, responses.Length - 1);
            return responses[index];
        }
    }

    private sealed record RecordedRequest(
        string? Body,
        string? EsriAuthorization);
}
