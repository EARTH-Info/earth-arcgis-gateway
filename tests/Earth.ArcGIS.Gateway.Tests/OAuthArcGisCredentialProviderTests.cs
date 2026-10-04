using System.Net;
using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class OAuthArcGisCredentialProviderTests
{
    [Fact]
    public async Task TokenIsCachedUntilInvalidated()
    {
        var handler = new StubHandler(
            """{"access_token":"token-1","expires_in":1800}""",
            """{"access_token":"token-2","expires_in":1800}""");
        var provider = Create(handler);

        Assert.Equal("token-1", await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal("token-1", await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);

        provider.InvalidateToken();

        Assert.Equal("token-2", await provider.GetTokenAsync(CancellationToken.None));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task TokenInsideRefreshSkewIsProactivelyRefreshed()
    {
        var handler = new StubHandler(
            """{"access_token":"token-1","expires_in":30}""",
            """{"access_token":"token-2","expires_in":30}""");
        var provider = Create(handler);

        Assert.Equal("token-1", await provider.GetTokenAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal("token-2", await provider.GetTokenAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task UsesClientCredentialsGrantWithoutExposingSecretInUri()
    {
        var handler = new StubHandler("""{"access_token":"token-1","expires_in":1800}""");
        var provider = Create(handler);

        await provider.GetTokenAsync(CancellationToken.None);

        Assert.Equal(new Uri("https://portal.test/sharing/rest/oauth2/token"), handler.LastRequestUri);
        Assert.NotNull(handler.LastBody);
        Assert.Contains("grant_type=client_credentials", handler.LastBody);
        Assert.Contains("client_id=client-1", handler.LastBody);
        Assert.Contains("client_secret=secret-1", handler.LastBody);
        Assert.DoesNotContain("secret-1", handler.LastRequestUri!.ToString());
    }

    [Fact]
    public async Task InvalidEndpointIsRejectedBeforeNetworkCall()
    {
        var handler = new StubHandler("""{"access_token":"token-1","expires_in":1800}""");
        var provider = Create(handler, endpoint: "http://portal.test/oauth2/token");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetTokenAsync(CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task MalformedResponseIsRejected()
    {
        var handler = new StubHandler("""{"access_token":"","expires_in":0}""");
        var provider = Create(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetTokenAsync(CancellationToken.None));
    }

    private static OAuthArcGisCredentialProvider Create(
        StubHandler handler,
        string endpoint = "https://portal.test/sharing/rest/oauth2/token")
    {
        var http = new HttpClient(handler);
        return new OAuthArcGisCredentialProvider(
            new StubFactory(http),
            Options.Create(new GatewayOptions
            {
                OAuthTokenEndpoint = endpoint,
                OAuthClientId = "client-1",
                OAuthClientSecret = "secret-1",
                RefreshSkewSeconds = 120
            }),
            NullLogger<OAuthArcGisCredentialProvider>.Instance);
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(params string[] bodies) : HttpMessageHandler
    {
        private int index;

        public int RequestCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var body = bodies[Math.Min(index, bodies.Length - 1)];
            index++;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            };
        }
    }
}
