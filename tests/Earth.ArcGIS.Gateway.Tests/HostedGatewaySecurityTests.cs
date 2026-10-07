using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Earth.ArcGIS.Gateway;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class HostedGatewaySecurityTests
{
    private const string Issuer = "https://earthid.test";
    private const string Audience = "gateway-api";
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("hosted-earthid-test-signing-key-32b!"));

    [Fact]
    public async Task HealthIsReachableWithoutAuthentication()
    {
        await using var factory = new GatewayFactory();
        using var client = factory.CreateClient(NoRedirect());

        using var response = await client.GetAsync(
            "/health/live",
            TestContext.Current.CancellationToken);

        await AssertStatusAsync(HttpStatusCode.OK, response);
    }

    [Fact]
    public async Task ArcGisRouteRejectsUnauthenticatedRequest()
    {
        await using var factory = new GatewayFactory();
        using var client = factory.CreateClient(NoRedirect());

        using var response = await client.GetAsync(
            ResourcePath(),
            TestContext.Current.CancellationToken);

        await AssertStatusAsync(HttpStatusCode.Unauthorized, response);
        Assert.Equal(0, factory.ArcGis.RequestCount);
    }

    [Fact]
    public async Task ValidEarthIdUserTraversesRealMiddlewareAndReachesArcGis()
    {
        await using var factory = new GatewayFactory();
        using var client = factory.CreateClient(NoRedirect());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(includeSubject: true));

        using var response = await client.GetAsync(
            ResourcePath(),
            TestContext.Current.CancellationToken);

        await AssertStatusAsync(HttpStatusCode.OK, response);
        Assert.Equal(1, factory.ArcGis.RequestCount);
        Assert.Equal(1, factory.Access.Calls);
    }

    [Fact]
    public async Task AuthenticatedTokenWithoutSubjectFailsAuthorizationPolicy()
    {
        await using var factory = new GatewayFactory();
        using var client = factory.CreateClient(NoRedirect());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(includeSubject: false));

        using var response = await client.GetAsync(
            ResourcePath(),
            TestContext.Current.CancellationToken);

        await AssertStatusAsync(HttpStatusCode.Forbidden, response);
        Assert.Equal(0, factory.ArcGis.RequestCount);
    }

    [Fact]
    public async Task WrongSignatureIsRejectedBeforeGatewayHandler()
    {
        await using var factory = new GatewayFactory();
        using var client = factory.CreateClient(NoRedirect());
        var wrongKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("wrong-hosted-signing-key-32-bytes!!"));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                CreateToken(includeSubject: true, signingKey: wrongKey));

        using var response = await client.GetAsync(
            ResourcePath(),
            TestContext.Current.CancellationToken);

        await AssertStatusAsync(HttpStatusCode.Unauthorized, response);
        Assert.Equal(0, factory.ArcGis.RequestCount);
    }

    [Fact]
    public async Task BrowserSuppliedArcGisTokenIsRejectedThroughHostedPipeline()
    {
        await using var factory = new GatewayFactory();
        using var client = factory.CreateClient(NoRedirect());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken(includeSubject: true));

        using var response = await client.GetAsync(
            ResourcePath() + "&token=browser-secret",
            TestContext.Current.CancellationToken);

        await AssertStatusAsync(HttpStatusCode.BadRequest, response);
        Assert.Equal(0, factory.ArcGis.RequestCount);
        Assert.Equal(0, factory.Access.Calls);
    }

    [Fact]
    public async Task AdminApiRequiresAuthentication()
    {
        await using var factory = new GatewayFactory();
        using var client = factory.CreateClient(NoRedirect());

        using var response = await client.GetAsync(
            "/admin/api/overview",
            TestContext.Current.CancellationToken);

        await AssertStatusAsync(HttpStatusCode.Redirect, response);
        Assert.Equal("/admin/login", response.Headers.Location?.AbsolutePath);
    }

    private static async Task AssertStatusAsync(
        HttpStatusCode expected,
        HttpResponseMessage response)
    {
        if (response.StatusCode == expected)
            return;

        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.Fail(
            $"Expected HTTP {(int)expected} {expected}, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
    }

    private static WebApplicationFactoryClientOptions NoRedirect() =>
        new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        };

    private static string ResourcePath() =>
        "/arcgis/rest/services/Land/Parcels/FeatureServer/0/query?returnCountOnly=true&f=json";

    private static string CreateToken(
        bool includeSubject,
        SecurityKey? signingKey = null)
    {
        var claims = new List<Claim>
        {
            new("client_id", "jtuwma-web")
        };
        if (includeSubject)
            claims.Add(new Claim("sub", "sub-hosted"));

        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                signingKey ?? SigningKey,
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class GatewayFactory : WebApplicationFactory<Program>
    {
        public RecordingArcGisHandler ArcGis { get; } = new();
        public AllowAccessPolicy Access { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["EarthId:Authority"] = Issuer,
                    ["EarthId:Audience"] = Audience,
                    ["Gateway:ArcGisBaseUrl"] = "https://arcgis.test",
                    ["Gateway:CredentialProvider"] = "federated-11.3",
                    ["Gateway:PortalTokenEndpoint"] = "https://portal.test/sharing/rest/generateToken",
                    ["Gateway:FederatedServerUrl"] = "https://arcgis.test/arcgis",
                    ["Gateway:Username"] = "gateway-user",
                    ["Gateway:Password"] = "test-secret",
                    ["Gateway:AllowedPathPrefixes:0"] = "/arcgis/rest/services/Land/Parcels",
                    ["Applications:Registrations:jtuwma:Audiences:0"] = Audience,
                    ["Applications:Registrations:jtuwma:ClientIds:0"] = "jtuwma-web",
                    ["Applications:Registrations:jtuwma:AccessApiAuthorizeUrl"] = "https://policy.test/authorize",
                    ["Protection:SourceRateLimit:Enabled"] = "false"
                };
                configuration.AddInMemoryCollection(values);
            });

            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options =>
                    {
                        var oidc = new OpenIdConnectConfiguration
                        {
                            Issuer = Issuer
                        };
                        oidc.SigningKeys.Add(SigningKey);
                        options.Configuration = oidc;
                        options.ConfigurationManager =
                            new StaticConfigurationManager<OpenIdConnectConfiguration>(oidc);
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidIssuer = Issuer,
                            ValidateAudience = true,
                            ValidAudience = Audience,
                            ValidateLifetime = true,
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKey = SigningKey,
                            ClockSkew = TimeSpan.FromMinutes(1)
                        };
                    });

                services.PostConfigure<OpenIdConnectOptions>(
                    AdminConsole.OidcScheme,
                    options =>
                    {
                        options.ClientId = "gateway-admin-test-client";
                        options.ClientSecret = "gateway-admin-test-secret";
                    });

                services.RemoveAll<IAccessPolicyClient>();
                services.AddSingleton<IAccessPolicyClient>(Access);

                services.RemoveAll<IArcGisCredentialProvider>();
                services.AddSingleton<IArcGisCredentialProvider>(
                    new StaticCredentialProvider());

                services.AddHttpClient("arcgis")
                    .ConfigurePrimaryHttpMessageHandler(() => ArcGis);
            });
        }
    }

    private sealed class AllowAccessPolicy : IAccessPolicyClient
    {
        public int Calls { get; private set; }

        public Task<AccessPolicyDecision> AuthorizeAsync(
            GatewayApplication application,
            AccessPolicyRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(
                new AccessPolicyDecision(true, "test_allow", "v-test"));
        }
    }

    private sealed class StaticCredentialProvider : IArcGisCredentialProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult("server-token");

        public void InvalidateToken()
        {
        }
    }

    public sealed class RecordingArcGisHandler : HttpMessageHandler
    {
        private int requestCount;
        public int RequestCount => Volatile.Read(ref requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"count\":12}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
