using System.Security.Claims;
using Earth.ArcGIS.Gateway;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class AdminAuthorizationTests
{
    [Fact]
    public void ExplicitAdminSubjectIsAdministrator()
    {
        var principal = Principal(new Claim("sub", "admin-sub"));
        var options = Options();
        options.AllowedSubjects = ["admin-sub"];

        Assert.True(AdminAuthorization.IsAuthorized(
            principal,
            options,
            AdminAccessLevel.Administrator));
    }

    [Fact]
    public void ViewerCanReadButCannotOperateSecurityControls()
    {
        var principal = Principal(
            new Claim("sub", "viewer-sub"),
            new Claim("role", "gateway-viewer"));
        var options = Options();

        Assert.True(AdminAuthorization.IsAuthorized(
            principal,
            options,
            AdminAccessLevel.Viewer));
        Assert.False(AdminAuthorization.IsAuthorized(
            principal,
            options,
            AdminAccessLevel.SecurityOperator));
        Assert.False(AdminAuthorization.IsAuthorized(
            principal,
            options,
            AdminAccessLevel.Administrator));
    }

    [Fact]
    public void SecurityOperatorInheritsViewerButNotAdministrator()
    {
        var principal = Principal(
            new Claim("sub", "operator-sub"),
            new Claim("role", "gateway-security-operator"));
        var options = Options();

        Assert.True(AdminAuthorization.IsAuthorized(
            principal,
            options,
            AdminAccessLevel.Viewer));
        Assert.True(AdminAuthorization.IsAuthorized(
            principal,
            options,
            AdminAccessLevel.SecurityOperator));
        Assert.False(AdminAuthorization.IsAuthorized(
            principal,
            options,
            AdminAccessLevel.Administrator));
    }

    [Fact]
    public void AdministratorInheritsAllLevels()
    {
        var principal = Principal(
            new Claim("sub", "admin-role-sub"),
            new Claim("roles", "gateway-admin"));
        var options = Options();

        Assert.True(AdminAuthorization.IsAuthorized(
            principal,
            options,
            AdminAccessLevel.Viewer));
        Assert.True(AdminAuthorization.IsAuthorized(
            principal,
            options,
            AdminAccessLevel.SecurityOperator));
        Assert.True(AdminAuthorization.IsAuthorized(
            principal,
            options,
            AdminAccessLevel.Administrator));
    }

    [Fact]
    public void LegacyAllowedRoleRemainsAdministratorEquivalent()
    {
        var principal = Principal(
            new Claim("sub", "legacy-sub"),
            new Claim("role", "legacy-admin"));
        var options = Options();
        options.AllowedRoles = ["legacy-admin"];

        Assert.True(AdminAuthorization.IsAuthorized(
            principal,
            options,
            AdminAccessLevel.Administrator));
    }

    [Fact]
    public void UntrustedSubjectAndRoleAreRejected()
    {
        var principal = Principal(
            new Claim("sub", "ordinary-user"),
            new Claim("role", "ordinary-role"));

        Assert.False(AdminAuthorization.IsAuthorized(
            principal,
            Options(),
            AdminAccessLevel.Viewer));
    }

    [Fact]
    public void MissingStableSubjectFailsIdentityRequirement()
    {
        var principal = Principal(new Claim("role", "gateway-admin"));
        Assert.False(EarthIdAuthentication.HasStableSubject(principal));
    }

    private static AdminOptions Options() => new()
    {
        AllowedSubjects = [],
        ViewerRoles = ["gateway-viewer"],
        SecurityOperatorRoles = ["gateway-security-operator"],
        AdministratorRoles = ["gateway-admin"],
        AllowedRoles = []
    };

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));
}
