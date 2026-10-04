using System.Security.Claims;
using System.Threading.RateLimiting;
using Earth.ArcGIS.Gateway;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ApplicationOptions>, ApplicationOptionsValidator>();
builder.Services.AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection("Gateway"))
    .ValidateOnStart();
builder.Services.AddOptions<ApplicationOptions>()
    .Bind(builder.Configuration.GetSection("Applications"))
    .ValidateOnStart();
builder.Services.AddSingleton<IApplicationIdentityResolver, ApplicationIdentityResolver>();
builder.Services.AddSingleton<IArcGisResourceResolver, ArcGisResourceResolver>();
builder.Services.AddSingleton<IArcGisOperationPolicy, ArcGisOperationPolicy>();
builder.Services.AddSingleton<IRateCostPolicy, RateCostPolicy>();

var rateLimitOptions = new GatewayRateLimitOptions
{
    BurstCapacity = builder.Configuration.GetValue("RateLimit:BurstCapacity", 240),
    BurstWindowSeconds = builder.Configuration.GetValue("RateLimit:BurstWindowSeconds", 60),
    SustainedCapacity = builder.Configuration.GetValue("RateLimit:SustainedCapacity", 1_800),
    SustainedWindowSeconds = builder.Configuration.GetValue("RateLimit:SustainedWindowSeconds", 15 * 60),
    DailyCapacity = builder.Configuration.GetValue("RateLimit:DailyCapacity", 20_000),
    DailyWindowSeconds = builder.Configuration.GetValue("RateLimit:DailyWindowSeconds", 24 * 60 * 60)
};
rateLimitOptions.Validate();
builder.Services.AddSingleton(rateLimitOptions);

var redisConnection = builder.Configuration["Redis:ConnectionString"];
if (string.IsNullOrWhiteSpace(redisConnection))
{
    builder.Services.AddSingleton<IGatewayRateLimiter, InMemoryGatewayRateLimiter>();
}
else
{
    var redisOptions = new RedisRateLimitOptions { ConnectionString = redisConnection };
    var redisConfiguration = ConfigurationOptions.Parse(redisConnection);
    redisConfiguration.AbortOnConnectFail = false;
    redisConfiguration.ConnectRetry = 1;
    redisConfiguration.ConnectTimeout = 2_000;
    redisConfiguration.SyncTimeout = 1_000;
    redisConfiguration.AsyncTimeout = 1_000;

    builder.Services.AddSingleton(redisOptions);
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(redisConfiguration));
    builder.Services.AddSingleton<IGatewayRateLimiter, RedisGatewayRateLimiter>();
}
builder.Services.AddHttpClient("access-policy", c => c.Timeout = TimeSpan.FromSeconds(2))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        MaxResponseHeadersLength = 32
    });
builder.Services.AddSingleton<IAccessPolicyClient, AccessPolicyClient>();
builder.Services.AddSingleton<ITelemetryQueue, TelemetryQueue>();
builder.Services.AddSingleton<ITelemetrySink, LoggingTelemetrySink>();
builder.Services.AddHostedService<TelemetryWorker>();
builder.Services.AddHttpClient("arcgis-token", c => c.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxResponseHeadersLength = 32
    });
builder.Services.AddHttpClient("arcgis-oauth", c => c.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxResponseHeadersLength = 32
    });
builder.Services.AddSingleton<ArcGisTokenProvider>();
builder.Services.AddSingleton<OAuthArcGisCredentialProvider>();
builder.Services.AddSingleton<IArcGisCredentialProvider>(sp =>
{
    var cfg = sp.GetRequiredService<IOptions<GatewayOptions>>().Value;
    return cfg.CredentialProvider.Trim().ToLowerInvariant() switch
    {
        "federated-11.3" => sp.GetRequiredService<ArcGisTokenProvider>(),
        "oauth-11.5" => sp.GetRequiredService<OAuthArcGisCredentialProvider>(),
        _ => throw new InvalidOperationException(
            "Gateway:CredentialProvider must be 'federated-11.3' or 'oauth-11.5'.")
    };
});
builder.Services.AddHttpClient("arcgis", c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxConnectionsPerServer = 64,
        MaxResponseHeadersLength = 64
    });

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

app.MapGet("/health", (ITelemetryQueue telemetry) => Results.Ok(new
{
    status = "ok",
    telemetry = new { accepted = telemetry.Accepted, dropped = telemetry.Dropped }
}));

app.MapMethods("/arcgis/{**path}", new[] { "GET", "POST" }, GatewayHandler.HandleAsync)
   .RequireAuthorization("earthid-user")
   .RequireRateLimiting("earthid-user");

app.Run();

public partial class Program { }