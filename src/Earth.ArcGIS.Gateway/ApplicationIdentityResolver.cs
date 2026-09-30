using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public sealed record GatewayApplication(string Id, string? AccessApiBaseUrl);

public interface IApplicationIdentityResolver
{
    bool TryResolve(ClaimsPrincipal principal, out GatewayApplication application);
}

public sealed class ApplicationIdentityResolver(IOptions<ApplicationOptions> options) : IApplicationIdentityResolver
{
    public bool TryResolve(ClaimsPrincipal principal, out GatewayApplication application)
    {
        var audiences = principal.FindAll("aud").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var clientId = principal.FindFirst("client_id")?.Value ?? principal.FindFirst("azp")?.Value;

        var matches = options.Value.Registrations
            .Where(x => x.Value.Audiences.Any(audiences.Contains)
                && (x.Value.ClientIds.Length == 0 ||
                    (clientId is not null && x.Value.ClientIds.Contains(clientId, StringComparer.Ordinal))))
            .Take(2)
            .ToArray();

        if (matches.Length != 1)
        {
            application = null!;
            return false;
        }

        application = new GatewayApplication(matches[0].Key, matches[0].Value.AccessApiBaseUrl);
        return true;
    }
}
