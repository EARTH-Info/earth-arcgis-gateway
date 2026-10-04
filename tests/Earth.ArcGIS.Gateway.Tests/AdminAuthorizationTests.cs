using System.Security.Claims;
using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class AdminAuthorizationTests
{
    [Fact]
    public void ExplicitAdminSubjectIsAuthorized()
    {
        var principal = Principal(
            new Claim("sub", "admin-sub"));

        var allowed = AdminAuthorization.IsAuthorized(
            principal,
            new AdminOptions
            {
                AllowedSubjects = ["admin-sub"],
                AllowedRoles = []
            });

        Assert.True(allowed);
    }

    [Fact]
    public void ConfiguredAdminRoleIsAuthorized()
    {
        var principal = Principal(
            new Claim("sub", "operator-sub"),
            new Claim("role", "gateway-admin"));

        var allowed = AdminAuthorization.IsAuthorized(
            principal,
            new AdminOptions
            {
                AllowedSubjects = [],
                AllowedRoles = ["gateway-admin"]
            });

        Assert.True(allowed);
    }

    [Fact]
    public void UntrustedSubjectAndRoleAreRejected()
    {
        var principal = Principal(
            new Claim("sub", "ordinary-user"),
            new Claim("role", "viewer"));

        var allowed = AdminAuthorization.IsAuthorized(
            principal,
            new AdminOptions
            {
                AllowedSubjects = ["admin-sub"],
                AllowedRoles = ["gateway-admin"]
            });

        Assert.False(allowed);
    }

    [Fact]
    public void MissingStableSubjectIsRejectedEvenWithRole()
    {
        var principal = Principal(
            new Claim("role", "gateway-admin"));

        Assert.False(EarthIdAuthentication.HasStableSubject(principal));
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));
}
