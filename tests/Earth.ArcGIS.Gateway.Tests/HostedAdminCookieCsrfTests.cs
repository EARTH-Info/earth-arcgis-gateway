using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Earth.ArcGIS.Gateway;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class HostedAdminCookieCsrfTests
{
    [Fact]
    public async Task CookieMutationWithoutAntiforgeryTokenIsRejected()
    {
        await using var factory = new CookieFactory();
        using var client = factory.CreateClient(ClientOptions());
        var authCookie = CreateAuthCookie(factory, "gateway-security-operator");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/api/blocks")
        {
            Content = Json("""{"earthIdSub":"target","reason":"csrf-test","minutes":5}""")
        };
        request.Headers.TryAddWithoutValidation("Cookie", authCookie);

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CookieMutationWithIssuedAntiforgeryTokenIsAccepted()
    {
        await using var factory = new CookieFactory();
        using var client = factory.CreateClient(ClientOptions());
        var authCookie = CreateAuthCookie(factory, "gateway-security-operator");

        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, "/admin");
        pageRequest.Headers.TryAddWithoutValidation("Cookie", authCookie);
        using var pageResponse = await client.SendAsync(
            pageRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);

        var html = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var token = ExtractCsrfToken(html);
        var csrfCookie = ExtractCookie(
            pageResponse,
            "__Host-eiag-csrf");

        using var mutation = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/api/blocks")
        {
            Content = Json("""{"earthIdSub":"target-csrf","reason":"csrf-test","minutes":5}""")
        };
        mutation.Headers.TryAddWithoutValidation(
            "Cookie",
            authCookie + "; " + csrfCookie);
        mutation.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);

        using var response = await client.SendAsync(
            mutation,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string CreateAuthCookie(
        CookieFactory factory,
        string role)
    {
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(AdminConsole.CookieScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "cookie-admin-sub"),
            new Claim("role", role)
        ], AdminConsole.CookieScheme));
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties(),
            AdminConsole.CookieScheme);
        var protectedTicket = options.TicketDataFormat.Protect(ticket);
        return $"__Host-eiag-admin={protectedTicket}";
    }

    private static string ExtractCsrfToken(string html)
    {
        const string marker = "const csrfToken=";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += marker.Length;
        var end = html.IndexOf(';', start);
        Assert.True(end > start);
        return JsonSerializer.Deserialize<string>(html[start..end])
               ?? throw new InvalidOperationException("CSRF token was not rendered.");
    }

    private static string ExtractCookie(
        HttpResponseMessage response,
        string name)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values));
        var prefix = name + "=";
        var value = values
            .Select(x => x.Split(';', 2)[0])
            .Single(x => x.StartsWith(prefix, StringComparison.Ordinal));
        return value;
    }

    private static StringContent Json(string value) =>
        new(value, Encoding.UTF8, "application/json");

    private static WebApplicationFactoryClientOptions ClientOptions() =>
        new()
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        };

    private sealed class CookieFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EarthId:Authority"] = "https://earthid.test",
                    ["EarthId:Audience"] = "gateway-api",
                    ["Gateway:ArcGisBaseUrl"] = "https://arcgis.test",
                    ["Gateway:CredentialProvider"] = "federated-11.3",
                    ["Gateway:PortalTokenEndpoint"] = "https://portal.test/sharing/rest/generateToken",
                    ["Gateway:FederatedServerUrl"] = "https://arcgis.test/arcgis",
                    ["Gateway:Username"] = "gateway-user",
                    ["Gateway:Password"] = "test-secret",
                    ["Gateway:AllowedPathPrefixes:0"] = "/arcgis/rest/services/Land/Parcels",
                    ["Applications:Registrations:jtuwma:Audiences:0"] = "gateway-api",
                    ["Applications:Registrations:jtuwma:ClientIds:0"] = "jtuwma-web",
                    ["Applications:Registrations:jtuwma:AccessApiAuthorizeUrl"] = "https://policy.test/authorize",
                    ["Admin:SecurityOperatorRoles:0"] = "gateway-security-operator",
                    ["Protection:SourceRateLimit:Enabled"] = "false"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<OpenIdConnectOptions>(
                    AdminConsole.OidcScheme,
                    options =>
                    {
                        options.ClientId = "gateway-admin-test-client";
                        options.ClientSecret = "gateway-admin-test-secret";
                    });
            });
        }
    }
}
