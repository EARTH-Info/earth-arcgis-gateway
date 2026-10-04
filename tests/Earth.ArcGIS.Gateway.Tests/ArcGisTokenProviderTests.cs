using System.Net;
using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class ArcGisTokenProviderTests
{
    [Fact]
    public async Task ServerTokenIsCachedAndInvalidationOnlyReExchangesServerToken()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds();
        var handler = new StubHandler(expires);
        var provider = Create(handler);

        Assert.Equal("server-1", await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal("server-1", await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, handler.PortalRequests);
        Assert.Equal(1, handler.ServerExchangeRequests);

        provider.InvalidateToken();

        Assert.Equal("server-2", await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, handler.PortalRequests);
        Assert.Equal(2, handler.ServerExchangeRequests);
    }

    [Fact]
    public async Task TokenInsideRefreshSkewIsProactivelyRefreshed()
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds();
        var handler = new StubHandler(expires);
        var provider = Create(handler);

        Assert.Equal("server-1", await provider.GetTokenAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal("server-2", await provider.GetTokenAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.PortalRequests);
        Assert.Equal(2, handler.ServerExchangeRequests);
    }

    [Fact]
    public async Task ExchangeUsesExactConfiguredFederatedServerUrl()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds();
        var handler = new StubHandler(expires);
        var provider = Create(handler);

        await provider.GetTokenAsync(CancellationToken.None);

        Assert.NotNull(handler.LastExchangeBody);
        Assert.Contains(
            "serverUrl=https%3A%2F%2Fserver.test%2Farcgis",
            handler.LastExchangeBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedTokenResponseIsRejected()
    {
        var handler = new MalformedHandler();
        var provider = Create(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetTokenAsync(CancellationToken.None));
    }

    private static ArcGisTokenProvider Create(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new ArcGisTokenProvider(
            new StubFactory(http),
            Options.Create(new GatewayOptions
            {
                PortalTokenEndpoint = "https://portal.test/sharing/rest/generateToken",
                FederatedServerUrl = "https://server.test/arcgis",
                Username = "gateway-user",
                Password = "gateway-password",
                TokenExpirationMinutes = 30,
                RefreshSkewSeconds = 120
            }),
            NullLogger<ArcGisTokenProvider>.Instance);
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(long expires) : HttpMessageHandler
    {
        public int PortalRequests { get; private set; }
        public int ServerExchangeRequests { get; private set; }
        public string? LastExchangeBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (body.Contains("username=", StringComparison.Ordinal))
            {
                PortalRequests++;
                return Json($$"""{"token":"portal-1","expires":{{expires}}}""");
            }

            ServerExchangeRequests++;
            LastExchangeBody = body;
            return Json($$"""{"token":"server-{{ServerExchangeRequests}}","expires":{{expires}}}""");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private sealed class MalformedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"token":"","expires":"not-a-number"}""")
            });
    }
}
