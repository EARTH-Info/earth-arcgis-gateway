using System.Net;
using System.Text;
using System.Text.Json;
using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class AccessPolicyClientTests
{
    [Fact]
    public async Task AllowedTrue_IsAllowed()
    {
        var client = Create(
            HttpStatusCode.OK,
            """{"allowed":true,"reasonCode":"matched","policyVersion":"v1","cacheTtlSeconds":30,"rateLimitProfile":{"name":"interactive"}}""");

        var result = await client.AuthorizeAsync(
            App(), Request(), TestContext.Current.CancellationToken);

        Assert.True(result.Allowed);
        Assert.Equal("matched", result.ReasonCode);
        Assert.Equal("v1", result.PolicyVersion);
        Assert.Equal(30, result.CacheTtlSeconds);
        Assert.True(result.RateLimitProfile.HasValue);
        Assert.Equal("interactive", result.RateLimitProfile.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task AllowedFalse_IsDenied()
    {
        var result = await Create(
                HttpStatusCode.OK,
                """{"allowed":false,"reasonCode":"layer_denied","policyVersion":"v7"}""")
            .AuthorizeAsync(App(), Request(), TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        Assert.Equal("layer_denied", result.ReasonCode);
        Assert.Equal("v7", result.PolicyVersion);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"allowed":null}""")]
    [InlineData("""{"allowed":"true"}""")]
    [InlineData("""{"decision":"ALLOW"}""")]
    [InlineData("""{"allowed":true,"cacheTtlSeconds":-1}""")]
    [InlineData("""{"allowed":true,"cacheTtlSeconds":301}""")]
    [InlineData("""{"allowed":true,"rateLimitProfile":"interactive"}""")]
    public async Task MalformedCanonicalDecision_FailsClosed(string json)
    {
        var result = await Create(HttpStatusCode.OK, json)
            .AuthorizeAsync(App(), Request(), TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        Assert.Equal("access_api_malformed", result.ReasonCode);
    }

    [Fact]
    public async Task RequestSerializesCanonicalApplicationId()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """{"allowed":true,"reasonCode":"matched"}""");
        var client = Create(handler);

        var result = await client.AuthorizeAsync(
            App(), Request(), TestContext.Current.CancellationToken);

        Assert.True(result.Allowed);
        Assert.NotNull(handler.LastBody);
        using var json = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("jtuwma", json.RootElement.GetProperty("applicationId").GetString());
        Assert.False(json.RootElement.TryGetProperty("application", out _));
    }

    [Fact]
    public async Task NonSuccessHttp_FailsClosed()
    {
        var result = await Create(HttpStatusCode.ServiceUnavailable, "{}")
            .AuthorizeAsync(App(), Request(), TestContext.Current.CancellationToken);
        Assert.False(result.Allowed);
        Assert.Equal("access_api_http_error", result.ReasonCode);
    }

    [Fact]
    public async Task MissingAccessApi_FailsClosed()
    {
        var result = await Create(HttpStatusCode.OK, "{}")
            .AuthorizeAsync(
                new GatewayApplication("jtuwma", null),
                Request(),
                TestContext.Current.CancellationToken);
        Assert.False(result.Allowed);
        Assert.Equal("access_api_not_configured", result.ReasonCode);
    }

    [Fact]
    public async Task HttpAuthorizeEndpoint_FailsClosedWithoutRequest()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"allowed":true}""");
        var client = Create(handler);
        var result = await client.AuthorizeAsync(
            new GatewayApplication("jtuwma", "http://policy.test/authorize"),
            Request(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        Assert.Equal("access_api_invalid_configuration", result.ReasonCode);
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task ConfiguredAuthorizeEndpoint_IsUsedExactly()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"allowed":true}""");
        var client = Create(handler);
        var result = await client.AuthorizeAsync(
            App(), Request(), TestContext.Current.CancellationToken);

        Assert.True(result.Allowed);
        Assert.Equal(new Uri("https://policy.test/authorize"), handler.LastRequestUri);
    }

    [Fact]
    public async Task OversizedPolicyResponse_FailsClosed()
    {
        var body = new string('x', 70 * 1024);
        var result = await Create(HttpStatusCode.OK, body)
            .AuthorizeAsync(App(), Request(), TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        Assert.Equal("access_api_response_too_large", result.ReasonCode);
    }

    [Fact]
    public async Task NetworkFailure_FailsClosed()
    {
        var result = await Create(new ThrowingHandler())
            .AuthorizeAsync(App(), Request(), TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        Assert.Equal("access_api_unavailable", result.ReasonCode);
    }

    [Fact]
    public async Task HttpClientTimeout_FailsClosed()
    {
        var result = await Create(
                new SlowHandler(),
                TimeSpan.FromMilliseconds(50))
            .AuthorizeAsync(App(), Request(), TestContext.Current.CancellationToken);

        Assert.False(result.Allowed);
        Assert.Equal("access_api_timeout", result.ReasonCode);
    }

    private static AccessPolicyClient Create(HttpStatusCode status, string body) =>
        Create(new RecordingHandler(status, body));

    private static AccessPolicyClient Create(
        HttpMessageHandler handler,
        TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(2)
        };
        return new AccessPolicyClient(
            new StubFactory(http),
            NullLogger<AccessPolicyClient>.Instance);
    }

    private static GatewayApplication App() =>
        new("jtuwma", "https://policy.test/authorize");

    private static AccessPolicyRequest Request() =>
        new(
            "sub-1",
            "tenant-1",
            "jtuwma",
            "Land/Parcels",
            "FeatureServer",
            0,
            "query",
            "GET",
            "cid-1");

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Access API unavailable.");
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
                Content = new StringContent(
                    """{"allowed":true}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class RecordingHandler(
        HttpStatusCode status,
        string body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
