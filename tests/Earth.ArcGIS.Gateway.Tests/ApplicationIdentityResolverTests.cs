using System.Security.Claims;
using Earth.ArcGIS.Gateway;
using Microsoft.Extensions.Options;

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
    public void Resolves_TrustedClient()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("aud", "gateway-api"), new Claim("client_id", "jtuwma-web")], "test"));
        Assert.True(Create().TryResolve(p, out var app));
        Assert.Equal("jtuwma", app.Id);
    }

    [Fact]
    public void Rejects_UnknownClient()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("aud", "gateway-api"), new Claim("client_id", "browser-supplied")], "test"));
        Assert.False(Create().TryResolve(p, out _));
    }
}
