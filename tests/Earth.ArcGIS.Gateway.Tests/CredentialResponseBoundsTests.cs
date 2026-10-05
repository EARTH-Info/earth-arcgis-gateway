using System.Net;
using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class CredentialResponseBoundsTests
{
    [Fact]
    public async Task OAuthOversizedResponseIsRejected()
    {
        var http = new HttpClient(new OversizedHandler());
        var provider = new OAuthArcGisCredentialProvider(
            new StubFactory(http),
            Options.Create(new GatewayOptions
            {
                OAuthTokenEndpoint = "https://portal.test/sharing/rest/oauth2/token",
                OAuthClientId = "client-1",
                OAuthClientSecret = "secret-1",
                OAuthExchangeForFederatedServer = false,
                RefreshSkewSeconds = 120
            }),
            NullLogger<OAuthArcGisCredentialProvider>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetTokenAsync(TestContext.Current.CancellationToken));

        Assert.Contains("maximum allowed size", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 70 * 1024))
            });
    }
}
