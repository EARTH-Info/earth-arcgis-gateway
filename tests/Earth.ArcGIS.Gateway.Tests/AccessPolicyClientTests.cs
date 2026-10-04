using System.Net;
using System.Text;
using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class AccessPolicyClientTests
{
    [Fact]
    public async Task ExplicitAllow_IsAllowed()
    {
        var client = Create(HttpStatusCode.OK, """{"decision":"ALLOW","reasonCode":"matched","policyVersion":"v1"}""");
        var result = await client.AuthorizeAsync(App(), Request(), CancellationToken.None);
        Assert.True(result.Allowed);
        Assert.Equal("v1", result.PolicyVersion);
    }

    [Fact]
    public async Task ExplicitDeny_IsDenied()
    {
        var client = Create(HttpStatusCode.OK, """{"decision":"DENY","reasonCode":"layer_denied"}""");
        var result = await client.AuthorizeAsync(App(), Request(), CancellationToken.None);
        Assert.False(result.Allowed);
        Assert.Equal("layer_denied", result.ReasonCode);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"decision":"UNKNOWN"}""")]
    [InlineData("""{"decision":true}""")]
    public async Task MalformedOrUnknownDecision_FailsClosed(string json)
    {
        var result = await Create(HttpStatusCode.OK, json)
            .AuthorizeAsync(App(), Request(), CancellationToken.None);
        Assert.False(result.Allowed);
    }

    [Fact]
    public async Task NonSuccessHttp_FailsClosed()
    {
        var result = await Create(HttpStatusCode.ServiceUnavailable, "{}")
            .AuthorizeAsync(App(), Request(), CancellationToken.None);
        Assert.False(result.Allowed);
        Assert.Equal("access_api_http_error", result.ReasonCode);
    }

    [Fact]
    public async Task MissingAccessApi_FailsClosed()
    {
        var result = await Create(HttpStatusCode.OK, "{}")
            .AuthorizeAsync(new GatewayApplication("jtuwma", null), Request(), CancellationToken.None);
        Assert.False(result.Allowed);
        Assert.Equal("access_api_not_configured", result.ReasonCode);
    }

    [Fact]
    public async Task HttpAuthorizeEndpoint_FailsClosedWithoutRequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"decision":"ALLOW"}""");
        var client = Create(handler);
        var result = await client.AuthorizeAsync(
            new GatewayApplication("jtuwma", "http://policy.test/authorize"),
            Request(),
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("access_api_invalid_configuration", result.ReasonCode);
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task ConfiguredAuthorizeEndpoint_IsUsedExactly()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"decision":"ALLOW"}""");
        var client = Create(handler);
        var result = await client.AuthorizeAsync(App(), Request(), CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal(new Uri("https://policy.test/authorize"), handler.LastRequestUri);
    }

    [Fact]
    public async Task OversizedPolicyResponse_FailsClosed()
    {
        var body = new string('x', 70 * 1024);
        var result = await Create(HttpStatusCode.OK, body)
            .AuthorizeAsync(App(), Request(), CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("access_api_response_too_large", result.ReasonCode);
    }

    private static AccessPolicyClient Create(HttpStatusCode status, string body) =>
        Create(new StubHandler(status, body));

    private static AccessPolicyClient Create(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://policy.test/authorize") };
        return new AccessPolicyClient(new StubFactory(http), NullLogger<AccessPolicyClient>.Instance);
    }

    private static GatewayApplication App() => new("jtuwma", "https://policy.test/authorize");
    private static AccessPolicyRequest Request() => new("sub-1", "tenant-1", "jtuwma",
        "Land/Parcels", "FeatureServer", 0, "query", "GET", "cid-1");

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
