using System.Security.Claims;
using System.Threading.RateLimiting;
using Earth.ArcGIS.Gateway;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
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
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();

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
   .RequireAuthorization()
   .RequireRateLimiting("earthid-user");

app.Run();

public partial class Program { }