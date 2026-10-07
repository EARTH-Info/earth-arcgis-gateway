using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Earth.ArcGIS.Gateway;

public static class EarthIdAuthentication
{
    public static TokenValidationParameters CreateValidationParameters(
        string audience) =>
        new()
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };

    public static bool HasStableSubject(ClaimsPrincipal principal) =>
        !string.IsNullOrWhiteSpace(principal.FindFirstValue("sub"));
}
