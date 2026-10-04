using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Earth.ArcGIS.Gateway;
using Microsoft.IdentityModel.Tokens;

namespace Earth.ArcGIS.Gateway.Tests;

public sealed class EarthIdAuthenticationTests
{
    private const string Issuer = "https://earthid.test";
    private const string Audience = "gateway-api";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("earthid-test-signing-key-32-bytes!!"));

    [Fact]
    public void ValidTokenWithSubjectIsAccepted()
    {
        var principal = Validate(CreateToken(
            issuer: Issuer,
            audience: Audience,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingKey: SigningKey,
            includeSubject: true));

        Assert.True(EarthIdAuthentication.HasStableSubject(principal));
        Assert.Equal("sub-1", principal.FindFirstValue("sub"));
    }

    [Fact]
    public void ExpiredTokenIsRejected()
    {
        var token = CreateToken(
            Issuer,
            Audience,
            DateTime.UtcNow.AddMinutes(-2),
            SigningKey,
            includeSubject: true);

        Assert.ThrowsAny<SecurityTokenException>(() => Validate(token));
    }

    [Fact]
    public void WrongIssuerIsRejected()
    {
        var token = CreateToken(
            "https://wrong-issuer.test",
            Audience,
            DateTime.UtcNow.AddMinutes(5),
            SigningKey,
            includeSubject: true);

        Assert.ThrowsAny<SecurityTokenException>(() => Validate(token));
    }

    [Fact]
    public void WrongAudienceIsRejected()
    {
        var token = CreateToken(
            Issuer,
            "different-api",
            DateTime.UtcNow.AddMinutes(5),
            SigningKey,
            includeSubject: true);

        Assert.ThrowsAny<SecurityTokenException>(() => Validate(token));
    }

    [Fact]
    public void WrongSignatureIsRejected()
    {
        var wrongKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("different-test-signing-key-32-byte!"));
        var token = CreateToken(
            Issuer,
            Audience,
            DateTime.UtcNow.AddMinutes(5),
            wrongKey,
            includeSubject: true);

        Assert.ThrowsAny<SecurityTokenException>(() => Validate(token));
    }

    [Fact]
    public void MissingSubjectFailsGatewayIdentityRequirement()
    {
        var principal = Validate(CreateToken(
            Issuer,
            Audience,
            DateTime.UtcNow.AddMinutes(5),
            SigningKey,
            includeSubject: false));

        Assert.False(EarthIdAuthentication.HasStableSubject(principal));
    }

    private static ClaimsPrincipal Validate(string token)
    {
        var parameters = EarthIdAuthentication.CreateValidationParameters(Audience);
        parameters.ValidIssuer = Issuer;
        parameters.IssuerSigningKey = SigningKey;

        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false
        };

        return handler.ValidateToken(
            token,
            parameters,
            out _);
    }

    private static string CreateToken(
        string issuer,
        string audience,
        DateTime expires,
        SecurityKey signingKey,
        bool includeSubject)
    {
        var claims = new List<Claim>();
        if (includeSubject)
            claims.Add(new Claim("sub", "sub-1"));

        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            notBefore: expires.AddMinutes(-10),
            expires: expires,
            signingCredentials: new SigningCredentials(
                signingKey,
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
