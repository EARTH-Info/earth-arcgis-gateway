using System.Security.Claims;
using System.Threading.RateLimiting;
using Earth.ArcGIS.Gateway;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.Configure<ApplicationOptions>(builder.Configuration.GetSection("Applications"));
builder.Services.AddSingleton<IApplicationIdentityResolver, ApplicationIdentityResolver>();
builder.Services.AddSingleton<IArcGisResourceResolver, ArcGisResourceResolver>();
builder.Services.AddSingleton<IArcGisOperationPolicy, ArcGisOperationPolicy>();
builder.Services.AddSingleton<IRateCostPolicy, RateCostPolicy>();
builder.Services.AddSingleton<IGatewayRateLimiter, InMemoryGatewayRateLimiter>();
builder.Services.AddHttpClient("access-policy", c => c.Timeout = TimeSpan.FromSeconds(2));
builder.Services.AddSingleton<IAccessPolicyClient, AccessPolicyClient>();
builder.Services.AddSingleton<ITelemetryQueue, TelemetryQueue>();
builder.Services.AddSingleton<ITelemetrySink, LoggingTelemetrySink>();
builder.Services.AddHostedService<TelemetryWorker>();
builder.Services.AddHttpClient<ArcGisTokenProvider>();
builder.Services.AddHttpClient("arcgis", c => c.Timeout = TimeSpan.FromSeconds(100));

var authority = builder.Configuration["EarthId:Authority"]
    ?? throw new InvalidOperationException("EarthId:Authority is required.");
var audience = builder.Configuration["EarthId:Audience"]
    ?? throw new InvalidOperationException("EarthId:Audience is required.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = authority;
        o.Audience = audience;
        o.RequireHttpsMetadata = true;
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("earthid-user", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("sub");
    });
});

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("earthid-user", context =>
    {
        var subject = context.User.FindFirstValue("sub") ?? "anonymous";
        return RateLimitPartition.GetTokenBucketLimiter(subject, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 120,
            TokensPerPeriod = 120,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            QueueLimit = 0
        });
    });
});

var app = builder.Build();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapMethods("/arcgis/{**path}", new[] { "GET", "POST" }, GatewayHandler.HandleAsync)
   .RequireAuthorization("earthid-user")
   .RequireRateLimiting("earthid-user");

app.Run();

public partial class Program { }