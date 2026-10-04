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

    private static AccessPolicyClient Create(HttpStatusCode status, string body)
    {
        var http = new HttpClient(new StubHandler(status, body)) { BaseAddress = new Uri("https://policy.test/") };
        return new AccessPolicyClient(new StubFactory(http), NullLogger<AccessPolicyClient>.Instance);
    }

    private static GatewayApplication App() => new("jtuwma", "https://policy.test/");
    private static AccessPolicyRequest Request() => new("sub-1", "tenant-1", "jtuwma",
        "Land/Parcels", "FeatureServer", 0, "query", "GET", "cid-1");

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
