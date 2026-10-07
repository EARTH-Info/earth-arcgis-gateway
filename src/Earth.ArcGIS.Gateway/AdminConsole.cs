using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Earth.ArcGIS.Gateway;

public sealed record AdminBlockRequest(
    string EarthIdSub,
    string Reason,
    int? Minutes);

public static class AdminConsole
{
    public const string CookieScheme = "gateway-admin-cookie";
    public const string OidcScheme = "gateway-admin-oidc";
    public const string PolicyScheme = "gateway-admin-session";

    public static void MapRoutes(WebApplication app)
    {
        app.MapGet("/admin/login", (string? returnUrl) =>
        {
            var redirect = IsLocalReturnUrl(returnUrl) ? returnUrl! : "/admin";
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = redirect },
                [OidcScheme]);
        }).AllowAnonymous();

        app.MapGet("/admin/denied", () =>
            Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Admin access denied"))
            .AllowAnonymous();

        app.MapPost("/admin/logout", async (
            HttpContext context,
            IAntiforgery antiforgery) =>
        {
            if (!await ValidateMutationAsync(context, antiforgery))
                return Results.BadRequest(new { error = "invalid antiforgery token" });

            await context.SignOutAsync(CookieScheme);
            return Results.Redirect("/admin/login");
        }).RequireAuthorization("gateway-viewer");

        var group = app.MapGroup("/admin")
            .RequireAuthorization("gateway-viewer");

        group.MapGet("", (
            HttpContext context,
            IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            var tokenJson = JsonSerializer.Serialize(tokens.RequestToken ?? string.Empty);
            return Results.Content(
                Html.Replace("__CSRF_TOKEN_JSON__", tokenJson, StringComparison.Ordinal),
                "text/html; charset=utf-8");
        });

        group.MapGet("/api/overview", async (
            ITelemetryQueue telemetry,
            TelemetryPersistenceHealth persistence,
            FileTelemetrySpool spool,
            IUserBlockStore blocks,
            IArcGisUpstreamGate upstreamGate,
            IConnectionMultiplexerAccessor redisAccessor,
            HttpContext context) =>
        {
            var recent = telemetry.GetRecent(500);
            var activeBlocks = await blocks.GetActiveAsync(context.RequestAborted);
            var now = DateTimeOffset.UtcNow;
            var today = recent.Count(x => x.Timestamp.UtcDateTime.Date == now.UtcDateTime.Date);

            return Results.Ok(new
            {
                timestamp = now,
                requestsTodayVisibleWindow = today,
                allowed = recent.Count(x => x.Decision == "ALLOW"),
                denied = recent.Count(x => x.Decision == "DENY"),
                throttled = recent.Count(x => x.Decision == "THROTTLE"),
                telemetryAccepted = telemetry.Accepted,
                telemetryDropped = telemetry.Dropped,
                pendingSpoolFiles = spool.CountPendingFiles(),
                pendingSpoolBytes = spool.CountPendingBytes(),
                quarantinedSpoolFiles = persistence.QuarantinedSpoolFiles,
                storageFailures = persistence.StorageFailures,
                activeBlocks = activeBlocks.Count,
                upstreamConcurrency = new
                {
                    limit = upstreamGate.Limit,
                    available = upstreamGate.Available
                },
                redis = new
                {
                    configured = redisAccessor.Configured,
                    connected = redisAccessor.IsConnected
                }
            });
        });

        group.MapGet("/api/applications", (
            IOptions<ApplicationOptions> applications) =>
        {
            var result = applications.Value.Registrations
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new
                {
                    id = x.Key,
                    audiences = x.Value.Audiences,
                    clientIds = x.Value.ClientIds,
                    accessApiHost = TryHost(x.Value.AccessApiAuthorizeUrl)
                });
            return Results.Ok(result);
        });

        group.MapGet("/api/resources", (
            IOptions<GatewayOptions> options) =>
            Results.Ok(options.Value.AllowedPathPrefixes.Select(prefix => new { prefix })));

        group.MapGet("/api/rate-profiles", (
            UserCentricRateLimitOptions rate,
            GisCostOptions gisCost,
            UserConcurrencyOptions concurrency,
            ProtectionOptions protection) =>
            Results.Ok(new
            {
                userRate = new
                {
                    profileVersion = rate.ProfileVersion,
                    aggregateBurstCapacity = rate.AggregateBurstCapacity,
                    aggregateRefillUnitsPerSecond = rate.AggregateRefillUnitsPerSecond,
                    sustainedCapacity = rate.SustainedCapacity,
                    sustainedWindowSeconds = rate.SustainedWindowSeconds,
                    dailyCapacity = rate.DailyCapacity,
                    dailyWindowSeconds = rate.DailyWindowSeconds,
                    resourceCapacity = rate.ResourceCapacity,
                    resourceWindowSeconds = rate.ResourceWindowSeconds
                },
                gisCost = new
                {
                    gisCost.ProfileVersion,
                    gisCost.Metadata,
                    gisCost.Tile,
                    gisCost.SceneNode,
                    gisCost.CountOnly,
                    gisCost.IdsOnly,
                    gisCost.ExtentOnly,
                    gisCost.AttributeQuery,
                    gisCost.GeometryQuery,
                    gisCost.SpatialSurcharge,
                    gisCost.AllFieldsSurcharge,
                    gisCost.PaginationSurcharge,
                    gisCost.Identify,
                    gisCost.Find,
                    gisCost.Attachment,
                    gisCost.Export,
                    gisCost.Fallback,
                    gisCost.HeavyThreshold
                },
                concurrency = new
                {
                    interactiveLimit = concurrency.InteractiveLimit,
                    heavyLimit = concurrency.HeavyLimit,
                    leaseSeconds = concurrency.LeaseSeconds
                },
                sourceRateLimit = new
                {
                    enabled = protection.SourceRateLimit.Enabled,
                    protection.SourceRateLimit.TokenLimit,
                    protection.SourceRateLimit.TokensPerPeriod,
                    protection.SourceRateLimit.ReplenishmentPeriodSeconds
                }
            }));

        group.MapGet("/api/live-traffic", (
            ITelemetryQueue telemetry,
            AdminOptions admin,
            int? limit) =>
        {
            var max = Math.Clamp(admin.RecentTelemetryLimit, 1, 1000);
            return Results.Ok(telemetry.GetRecent(Math.Clamp(limit ?? Math.Min(100, max), 1, max)));
        });

        group.MapGet("/api/audit", async (
            IAuditQueryStore audit,
            HttpContext context,
            string? sub,
            string? application,
            string? service,
            int? layerId,
            string? operation,
            string? decision,
            int? statusCode,
            string? correlationId,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit) =>
        {
            var result = await audit.QueryAsync(
                new AuditQuery(
                    sub,
                    application,
                    service,
                    layerId,
                    operation,
                    decision,
                    statusCode,
                    correlationId,
                    from,
                    to,
                    Math.Clamp(limit ?? 250, 1, 1000)),
                context.RequestAborted);
            return Results.Ok(result);
        });

        group.MapGet("/api/security", (
            ITelemetryQueue telemetry,
            int? limit) =>
        {
            var max = Math.Clamp(limit ?? 100, 1, 500);
            var events = telemetry.GetRecent(1000)
                .Where(x =>
                    x.Decision is "DENY" or "THROTTLE" ||
                    x.StatusCode >= 400)
                .Take(max)
                .ToArray();
            return Results.Ok(events);
        });

        group.MapGet("/api/auth-health", (
            IOptions<GatewayOptions> options) =>
        {
            var cfg = options.Value;
            return Results.Ok(new
            {
                credentialProvider = cfg.CredentialProvider,
                arcGisHost = TryHost(cfg.ArcGisBaseUrl),
                tokenEndpointHost = cfg.CredentialProvider.Equals(
                    "oauth-11.5",
                    StringComparison.OrdinalIgnoreCase)
                    ? TryHost(cfg.OAuthTokenEndpoint)
                    : TryHost(cfg.PortalTokenEndpoint),
                secretsExposed = false
            });
        });

        group.MapGet("/api/blocks", async (
            IUserBlockStore blocks,
            HttpContext context) =>
            Results.Ok(await blocks.GetActiveAsync(context.RequestAborted)));

        group.MapGet("/api/admin-audit", async (
            IAdminAuditStore audit,
            HttpContext context,
            int? limit) =>
            Results.Ok(await audit.GetRecentAsync(
                Math.Clamp(limit ?? 100, 1, 500),
                context.RequestAborted)));

        group.MapPost("/api/blocks", async (
            AdminBlockRequest request,
            HttpContext context,
            IUserBlockStore blocks,
            IAdminAuditStore audit,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory) =>
        {
            if (!await ValidateMutationAsync(context, antiforgery))
                return Results.BadRequest(new { error = "invalid antiforgery token" });

            if (string.IsNullOrWhiteSpace(request.EarthIdSub) ||
                string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest(new { error = "earthIdSub and reason are required" });
            }

            if (request.Minutes is <= 0 or > 10_080)
                return Results.BadRequest(new { error = "minutes must be between 1 and 10080 when supplied" });

            var adminSubject = context.User.FindFirstValue("sub") ?? "unknown";
            TimeSpan? duration = request.Minutes is null
                ? null
                : TimeSpan.FromMinutes(request.Minutes.Value);
            var planned = UserBlockStore.CreateBlock(
                request.EarthIdSub,
                request.Reason,
                adminSubject,
                duration);

            await audit.AddAsync(
                new AdminEnforcementEvent(
                    planned.CreatedAt,
                    adminSubject,
                    "BLOCK_REQUESTED",
                    planned.EarthIdSub,
                    planned.Reason,
                    planned.ExpiresAt,
                    context.TraceIdentifier),
                context.RequestAborted);

            var block = await blocks.BlockAsync(
                request.EarthIdSub,
                request.Reason,
                adminSubject,
                duration,
                context.RequestAborted);

            await audit.AddAsync(
                new AdminEnforcementEvent(
                    DateTimeOffset.UtcNow,
                    adminSubject,
                    "BLOCK_APPLIED",
                    block.EarthIdSub,
                    block.Reason,
                    block.ExpiresAt,
                    context.TraceIdentifier),
                context.RequestAborted);

            loggerFactory.CreateLogger("AdminAudit").LogWarning(
                "admin_enforcement_change admin_sub={AdminSubject} action={Action} target_sub={TargetSubject} reason={Reason} expires_at={ExpiresAt} correlation_id={CorrelationId}",
                adminSubject,
                "BLOCK_APPLIED",
                block.EarthIdSub,
                block.Reason,
                block.ExpiresAt,
                context.TraceIdentifier);

            return Results.Ok(block);
        }).RequireAuthorization("gateway-security-operator");

        group.MapDelete("/api/blocks/{earthIdSub}", async (
            string earthIdSub,
            HttpContext context,
            IUserBlockStore blocks,
            IAdminAuditStore audit,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory) =>
        {
            if (!await ValidateMutationAsync(context, antiforgery))
                return Results.BadRequest(new { error = "invalid antiforgery token" });

            var adminSubject = context.User.FindFirstValue("sub") ?? "unknown";
            await audit.AddAsync(
                new AdminEnforcementEvent(
                    DateTimeOffset.UtcNow,
                    adminSubject,
                    "UNBLOCK_REQUESTED",
                    earthIdSub,
                    "manual_unblock",
                    null,
                    context.TraceIdentifier),
                context.RequestAborted);

            var removed = await blocks.UnblockAsync(
                earthIdSub,
                context.RequestAborted);

            await audit.AddAsync(
                new AdminEnforcementEvent(
                    DateTimeOffset.UtcNow,
                    adminSubject,
                    removed ? "UNBLOCK_APPLIED" : "UNBLOCK_NOT_FOUND",
                    earthIdSub,
                    removed ? "manual_unblock" : "not_found",
                    null,
                    context.TraceIdentifier),
                context.RequestAborted);

            loggerFactory.CreateLogger("AdminAudit").LogWarning(
                "admin_enforcement_change admin_sub={AdminSubject} action={Action} target_sub={TargetSubject} result={Result} correlation_id={CorrelationId}",
                adminSubject,
                "UNBLOCK",
                earthIdSub,
                removed ? "removed" : "not_found",
                context.TraceIdentifier);

            return removed
                ? Results.NoContent()
                : Results.NotFound(new { error = "block not found" });
        }).RequireAuthorization("gateway-security-operator");

        group.MapPost("/api/policy-cache/{applicationId}/invalidate", async (
            string applicationId,
            HttpContext context,
            IAccessPolicyCache cache,
            IOptions<ApplicationOptions> applications,
            IAdminAuditStore audit,
            IAntiforgery antiforgery) =>
        {
            if (!await ValidateMutationAsync(context, antiforgery))
                return Results.BadRequest(new { error = "invalid antiforgery token" });

            if (!applications.Value.Registrations.ContainsKey(applicationId))
                return Results.NotFound(new { error = "application not found" });

            var adminSubject = context.User.FindFirstValue("sub") ?? "unknown";
            await audit.AddAsync(
                new AdminEnforcementEvent(
                    DateTimeOffset.UtcNow,
                    adminSubject,
                    "POLICY_CACHE_INVALIDATE_REQUESTED",
                    applicationId,
                    "administrative_invalidation",
                    null,
                    context.TraceIdentifier),
                context.RequestAborted);

            var generation = await cache.InvalidateApplicationAsync(
                applicationId,
                context.RequestAborted);

            await audit.AddAsync(
                new AdminEnforcementEvent(
                    DateTimeOffset.UtcNow,
                    adminSubject,
                    "POLICY_CACHE_INVALIDATED",
                    applicationId,
                    $"generation:{generation}",
                    null,
                    context.TraceIdentifier),
                context.RequestAborted);

            return Results.Ok(new { applicationId, generation });
        }).RequireAuthorization("gateway-admin");
    }

    private static bool IsLocalReturnUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith("/", StringComparison.Ordinal) &&
        !value.StartsWith("//", StringComparison.Ordinal) &&
        !value.StartsWith("/\\", StringComparison.Ordinal);

    private static async Task<bool> ValidateMutationAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        if (context.Request.Headers.Authorization.ToString()
            .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static string? TryHost(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : null;

    private const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>EI ArcGIS Gateway Admin</title>
<style>
:root{font-family:Inter,ui-sans-serif,system-ui,sans-serif;color:#172033;background:#f5f7fb}
*{box-sizing:border-box} body{margin:0} header{padding:20px 28px;background:#111827;color:#fff}
header h1{font-size:20px;margin:0} header p{opacity:.72;margin:5px 0 0;font-size:13px}
main{padding:24px;max-width:1500px;margin:auto}.grid{display:grid;gap:14px}
.cards{grid-template-columns:repeat(auto-fit,minmax(150px,1fr));margin-bottom:18px}
.card,.panel{background:#fff;border:1px solid #e4e8f0;border-radius:12px;box-shadow:0 1px 2px #00000008}
.card{padding:16px}.card strong{display:block;font-size:24px;margin-top:6px}.muted{color:#687386;font-size:12px}
.panels{grid-template-columns:minmax(0,2fr) minmax(280px,1fr)}.panel{padding:18px;margin-bottom:16px}
h2{font-size:15px;margin:0 0 14px} table{width:100%;border-collapse:collapse;font-size:12px}
th,td{text-align:left;padding:8px;border-bottom:1px solid #eef1f5;vertical-align:top}th{color:#687386}
.badge{display:inline-block;padding:2px 7px;border-radius:999px;background:#eef2ff;font-size:11px}
form{display:grid;gap:8px}input,textarea,button{font:inherit;padding:9px;border-radius:8px;border:1px solid #d7dce5}
button{background:#111827;color:#fff;border:0;cursor:pointer}.danger{background:#991b1b}
pre{white-space:pre-wrap;word-break:break-word;font-size:11px}.status{font-size:12px;margin-top:8px}
@media(max-width:900px){.panels{grid-template-columns:1fr}}
</style>
</head>
<body>
<header><h1>EI ArcGIS Gateway</h1><p>Identity-aware ArcGIS Enterprise gateway administration</p></header>
<main>
<section id="cards" class="grid cards"></section>
<section class="grid panels">
<div>
  <div class="panel"><h2>Live traffic</h2><div id="traffic"></div></div>
  <div class="panel"><h2>Security events</h2><div id="security"></div></div>
  <div class="panel"><h2>Applications & resources</h2><pre id="configuration"></pre></div>
</div>
<div>
  <div class="panel">
    <h2>Temporary user block</h2>
    <form id="blockForm">
      <input id="blockSub" placeholder="EarthID subject (sub)" required>
      <textarea id="blockReason" placeholder="Reason" required></textarea>
      <input id="blockMinutes" type="number" min="1" max="10080" placeholder="Minutes (blank = until removed)">
      <button type="submit" class="danger">Block user</button>
    </form>
    <div id="actionStatus" class="status muted"></div>
  </div>
  <div class="panel"><h2>Active blocks</h2><div id="blocks"></div></div>
  <div class="panel">
    <h2>Remove user block</h2>
    <form id="unblockForm">
      <input id="unblockSub" placeholder="EarthID subject (sub)" required>
      <button type="submit">Unblock user</button>
    </form>
    <div id="unblockStatus" class="status muted"></div>
  </div>
  <div class="panel"><h2>Rate policy</h2><pre id="rate"></pre></div>
  <div class="panel"><h2>Authentication health</h2><pre id="auth"></pre></div>
</div>
</section>
</main>
<script>
const csrfToken=__CSRF_TOKEN_JSON__;
const getJson=async p=>{const r=await fetch(p);if(!r.ok)throw new Error(await r.text());return r.json()};
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function table(items,cols){
 if(!items.length)return '<span class="muted">No records</span>';
 return '<table><thead><tr>'+cols.map(c=>'<th>'+esc(c[0])+'</th>').join('')+'</tr></thead><tbody>'+
 items.map(x=>'<tr>'+cols.map(c=>'<td>'+esc(c[1](x))+'</td>').join('')+'</tr>').join('')+'</tbody></table>';
}
async function refresh(){
 const [o,t,s,b,a,r,apps,res]=await Promise.all([
  getJson('/admin/api/overview'),getJson('/admin/api/live-traffic?limit=50'),
  getJson('/admin/api/security?limit=50'),getJson('/admin/api/blocks'),
  getJson('/admin/api/auth-health'),getJson('/admin/api/rate-profiles'),
  getJson('/admin/api/applications'),getJson('/admin/api/resources')]);
 const cards=[['Allowed',o.allowed],['Denied',o.denied],['Throttled',o.throttled],
 ['Queue dropped',o.telemetryDropped],['Spool files',o.pendingSpoolFiles],['Active blocks',o.activeBlocks],
 ['ArcGIS slots',o.upstreamConcurrency.available+'/'+o.upstreamConcurrency.limit]];
 document.getElementById('cards').innerHTML=cards.map(x=>'<div class="card"><span class="muted">'+esc(x[0])+'</span><strong>'+esc(x[1])+'</strong></div>').join('');
 const cols=[['Time',x=>x.timestamp],['User',x=>x.earthIdSub],['App',x=>x.application],
 ['Resource',x=>(x.service||'')+(x.layerId==null?'':'/'+x.layerId)],['Op',x=>x.operation],['Decision',x=>x.decision],['Status',x=>x.statusCode]];
 document.getElementById('traffic').innerHTML=table(t,cols);
 document.getElementById('security').innerHTML=table(s,cols);
 document.getElementById('blocks').innerHTML=table(b,[['User',x=>x.earthIdSub],['Reason',x=>x.reason],['Expires',x=>x.expiresAt||'manual'],['',x=>'DELETE /admin/api/blocks/'+x.earthIdSub]]);
 document.getElementById('configuration').textContent=JSON.stringify({applications:apps,resources:res},null,2);
 document.getElementById('auth').textContent=JSON.stringify(a,null,2);
 document.getElementById('rate').textContent=JSON.stringify(r,null,2);
}
document.getElementById('blockForm').addEventListener('submit',async e=>{
 e.preventDefault();const minutes=document.getElementById('blockMinutes').value;
 const body={earthIdSub:document.getElementById('blockSub').value,reason:document.getElementById('blockReason').value,minutes:minutes?Number(minutes):null};
 const r=await fetch('/admin/api/blocks',{method:'POST',headers:{'content-type':'application/json','X-CSRF-TOKEN':csrfToken},body:JSON.stringify(body)});
 document.getElementById('actionStatus').textContent=r.ok?'Block applied':'Failed: '+await r.text();if(r.ok)await refresh();
});
document.getElementById('unblockForm').addEventListener('submit',async e=>{
 e.preventDefault();const sub=document.getElementById('unblockSub').value;
 const r=await fetch('/admin/api/blocks/'+encodeURIComponent(sub),{method:'DELETE',headers:{'X-CSRF-TOKEN':csrfToken}});
 document.getElementById('unblockStatus').textContent=r.ok?'Block removed':'Failed: '+await r.text();if(r.ok)await refresh();
});
refresh().catch(e=>document.getElementById('cards').innerHTML='<div class="panel">Admin API error: '+esc(e.message)+'</div>');
setInterval(()=>refresh().catch(()=>{}),5000);
</script>
</body>
</html>
""";
}
