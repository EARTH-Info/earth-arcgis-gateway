using System.Security.Claims;
using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class ApplicationIdentityResolverTests
{
    private static ApplicationIdentityResolver Create() =>
        new(Options.Create(new ApplicationOptions
        {
            Registrations = new Dictionary<string, ApplicationRegistration>
            {
                ["jtuwma"] = new() { Audiences = ["gateway-api"], ClientIds = ["jtuwma-web"] },
                ["geoforest"] = new() { Audiences = ["gateway-api"], ClientIds = ["geoforest-web"] }
            }
        }));


    [Fact]
    public void ProductionConfigurationShapeBindsRegistrations()
    {
        var values = new Dictionary<string, string?>
        {
            ["Applications:Registrations:jtuwma:Audiences:0"] = "gateway-api",
            ["Applications:Registrations:jtuwma:ClientIds:0"] = "jtuwma-web",
            ["Applications:Registrations:jtuwma:AccessApiAuthorizeUrl"] =
                "https://policy.test/authorize"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var options = configuration
            .GetSection("Applications")
            .Get<ApplicationOptions>();

        Assert.NotNull(options);
        Assert.True(options.Registrations.ContainsKey("jtuwma"));
        Assert.Equal(
            "https://policy.test/authorize",
            options.Registrations["jtuwma"].AccessApiAuthorizeUrl);
    }

    [Fact]
    public void Resolves_TrustedClient()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("aud", "gateway-api"), new Claim("client_id", "jtuwma-web")], "test"));
        Assert.True(Create().TryResolve(p, out var app));
        Assert.Equal("jtuwma", app.Id);
    }

    [Fact]
    public void RejectsRegistrationWithNoTrustedClientIdsEvenIfValidatorIsBypassed()
    {
        var resolver = new ApplicationIdentityResolver(
            Options.Create(new ApplicationOptions
            {
                Registrations = new Dictionary<string, ApplicationRegistration>
                {
                    ["unsafe"] = new()
                    {
                        Audiences = ["gateway-api"],
                        ClientIds = []
                    }
                }
            }));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("aud", "gateway-api")],
            "test"));

        Assert.False(resolver.TryResolve(principal, out _));
    }

    [Fact]
    public void Rejects_UnknownClient()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("aud", "gateway-api"), new Claim("client_id", "browser-supplied")], "test"));
        Assert.False(Create().TryResolve(p, out _));
    }
}
