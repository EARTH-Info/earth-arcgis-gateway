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

public sealed class HostedAdminSecurityTests
{
    private const string Issuer = "https://earthid.test";
    private const string Audience = "gateway-api";
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("hosted-admin-test-signing-key-32b!"));

    [Fact]
    public async Task ViewerCanReadOverviewButCannotBlock()
    {
        await using var factory = new AdminFactory();
        using var client = factory.CreateClient(NoRedirect());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken("gateway-viewer"));

        using var read = await client.GetAsync(
            "/admin/api/overview",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var write = await client.PostAsync(
            "/admin/api/blocks",
            Json("""{"earthIdSub":"target","reason":"test","minutes":5}"""),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task SecurityOperatorCanBlockButCannotInvalidateApplicationCache()
    {
        await using var factory = new AdminFactory();
        using var client = factory.CreateClient(NoRedirect());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken("gateway-security-operator"));

        using var block = await client.PostAsync(
            "/admin/api/blocks",
            Json("""{"earthIdSub":"target","reason":"test","minutes":5}"""),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, block.StatusCode);

        using var invalidate = await client.PostAsync(
            "/admin/api/policy-cache/jtuwma/invalidate",
            new StringContent("", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, invalidate.StatusCode);
    }

    [Fact]
    public async Task AdministratorCanInvalidateApplicationCache()
    {
        await using var factory = new AdminFactory();
        using var client = factory.CreateClient(NoRedirect());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken("gateway-admin"));

        using var response = await client.PostAsync(
            "/admin/api/policy-cache/jtuwma/invalidate",
            new StringContent("", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuditFailurePreventsBlockMutation()
    {
        var blockStore = new CountingBlockStore();
        await using var factory = new AdminFactory(
            configureServices: services =>
            {
                services.RemoveAll<IUserBlockStore>();
                services.AddSingleton<IUserBlockStore>(blockStore);
                services.RemoveAll<IAdminAuditStore>();
                services.AddSingleton<IAdminAuditStore>(new ThrowingAuditStore());
            });
        using var client = factory.CreateClient(NoRedirect());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken("gateway-security-operator"));

        using var response = await client.PostAsync(
            "/admin/api/blocks",
            Json("""{"earthIdSub":"target","reason":"test","minutes":5}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, blockStore.BlockCalls);
    }

    private static StringContent Json(string value) =>
        new(value, Encoding.UTF8, "application/json");

    private static WebApplicationFactoryClientOptions NoRedirect() =>
        new()
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        };

    private static string CreateToken(string role)
    {
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            [
                new Claim("sub", "admin-test-sub"),
                new Claim("role", role),
                new Claim("client_id", "jtuwma-web")
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(
                SigningKey,
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class AdminFactory(
        Action<IServiceCollection>? configureServices = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
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
                    ["Admin:ViewerRoles:0"] = "gateway-viewer",
                    ["Admin:SecurityOperatorRoles:0"] = "gateway-security-operator",
                    ["Admin:AdministratorRoles:0"] = "gateway-admin",
                    ["Admin:AllowedRoles:0"] = "",
                    ["Protection:SourceRateLimit:Enabled"] = "false"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options =>
                    {
                        var oidc = new OpenIdConnectConfiguration { Issuer = Issuer };
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

                configureServices?.Invoke(services);
            });
        }
    }

    private sealed class CountingBlockStore : IUserBlockStore
    {
        public int BlockCalls { get; private set; }

        public ValueTask<UserBlock?> GetActiveAsync(
            string earthIdSub,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<UserBlock?>(null);

        public ValueTask<UserBlock> BlockAsync(
            string earthIdSub,
            string reason,
            string adminSubject,
            TimeSpan? duration,
            CancellationToken cancellationToken)
        {
            BlockCalls++;
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new UserBlock(
                earthIdSub.Trim(),
                reason.Trim(),
                adminSubject.Trim(),
                now,
                duration is null ? null : now.Add(duration.Value)));
        }

        public ValueTask<bool> UnblockAsync(
            string earthIdSub,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<IReadOnlyList<UserBlock>> GetActiveAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<UserBlock>>(Array.Empty<UserBlock>());
    }

    private sealed class ThrowingAuditStore : IAdminAuditStore
    {
        public Task AddAsync(
            AdminEnforcementEvent item,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("audit unavailable");

        public Task<IReadOnlyList<AdminEnforcementEvent>> GetRecentAsync(
            int max,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AdminEnforcementEvent>>(
                Array.Empty<AdminEnforcementEvent>());
    }
}
