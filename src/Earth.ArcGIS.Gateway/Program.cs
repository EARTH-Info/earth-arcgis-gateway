using System.Threading.RateLimiting;
using Earth.ArcGIS.Gateway;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var protection =
    builder.Configuration.GetSection("Protection").Get<ProtectionOptions>()
    ?? new ProtectionOptions();
protection.Validate();

var gisCostOptions =
    builder.Configuration.GetSection("GisCost").Get<GisCostOptions>()
    ?? new GisCostOptions();
gisCostOptions.Validate();

var userRateOptions =
    builder.Configuration.GetSection("UserRateLimit").Get<UserCentricRateLimitOptions>()
    ?? new UserCentricRateLimitOptions();
userRateOptions.Validate();

var userConcurrencyOptions =
    builder.Configuration.GetSection("UserConcurrency").Get<UserConcurrencyOptions>()
    ?? new UserConcurrencyOptions();
userConcurrencyOptions.Validate(protection.GatewayRequestTimeoutSeconds);

var trustedProxyAddresses = new List<System.Net.IPAddress>();
foreach (var configuredProxy in protection.TrustedProxies)
{
    if (string.IsNullOrWhiteSpace(configuredProxy))
        continue;

    if (!System.Net.IPAddress.TryParse(configuredProxy, out var address))
    {
        throw new InvalidOperationException(
            $"Protection:TrustedProxies contains an invalid IP address: '{configuredProxy}'.");
    }

    trustedProxyAddresses.Add(address);
}

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxConcurrentConnections = protection.MaxConcurrentConnections;
    serverOptions.Limits.MaxRequestBodySize = protection.MaxRequestBodyBytes;
    serverOptions.Limits.MaxRequestHeadersTotalSize = protection.MaxRequestHeadersTotalSize;
    serverOptions.Limits.MaxRequestHeaderCount = protection.MaxRequestHeaderCount;
    serverOptions.Limits.RequestHeadersTimeout =
        TimeSpan.FromSeconds(protection.RequestHeadersTimeoutSeconds);
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    foreach (var address in trustedProxyAddresses)
        options.KnownProxies.Add(address);
});

builder.Services.AddRequestTimeouts(options =>
{
    options.AddPolicy(
        "arcgis-gateway",
        new RequestTimeoutPolicy
        {
            Timeout = TimeSpan.FromSeconds(protection.GatewayRequestTimeoutSeconds),
            TimeoutStatusCode = StatusCodes.Status504GatewayTimeout
        });
});

builder.Services.AddSingleton<IValidateOptions<GatewayOptions>, GatewayOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ApplicationOptions>, ApplicationOptionsValidator>();
builder.Services.AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection("Gateway"))
    .ValidateOnStart();
builder.Services.AddOptions<ApplicationOptions>()
    .Bind(builder.Configuration.GetSection("Applications"))
    .ValidateOnStart();
builder.Services.AddOptions<ProtectionOptions>()
    .Bind(builder.Configuration.GetSection("Protection"))
    .Validate(options =>
    {
        try
        {
            options.Validate();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }, "Protection configuration is invalid.")
    .ValidateOnStart();

builder.Services.AddSingleton(protection);
builder.Services.AddSingleton(gisCostOptions);
builder.Services.AddSingleton(userRateOptions);
builder.Services.AddSingleton(userConcurrencyOptions);
builder.Services.AddSingleton<IApplicationIdentityResolver, ApplicationIdentityResolver>();
builder.Services.AddSingleton<IArcGisResourceResolver, ArcGisResourceResolver>();
builder.Services.AddSingleton<IArcGisOperationPolicy, ArcGisOperationPolicy>();
builder.Services.AddSingleton<IRequestActivityClassifier, RequestActivityClassifier>();
builder.Services.AddSingleton<IArcGisUpstreamGate>(_ =>
    new ArcGisUpstreamGate(protection.MaxConcurrentArcGisRequests));
builder.Services.AddSingleton<AdminAuditStore>();
builder.Services.AddSingleton<IConnectionMultiplexerAccessor, ConnectionMultiplexerAccessor>();

var adminOptions = new AdminOptions
{
    AllowedSubjects =
        builder.Configuration.GetSection("Admin:AllowedSubjects").Get<string[]>() ?? Array.Empty<string>(),
    AllowedRoles =
        builder.Configuration.GetSection("Admin:AllowedRoles").Get<string[]>() ?? ["gateway-admin"],
    RecentTelemetryLimit =
        builder.Configuration.GetValue("Admin:RecentTelemetryLimit", 500)
};
builder.Services.AddSingleton(adminOptions);

var adminOidcClientId = builder.Configuration["Admin:OidcClientId"];
var adminOidcClientSecret = builder.Configuration["Admin:OidcClientSecret"];
if (builder.Environment.IsProduction() &&
    (string.IsNullOrWhiteSpace(adminOidcClientId) ||
     string.IsNullOrWhiteSpace(adminOidcClientSecret)))
{
    throw new InvalidOperationException(
        "Admin:OidcClientId and Admin:OidcClientSecret are required in Production for the browser Admin Console.");
}

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-eiag-csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});

var redisConnection = builder.Configuration["Redis:ConnectionString"];
if (string.IsNullOrWhiteSpace(redisConnection))
{
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "Redis:ConnectionString is required in Production so user rate limits, concurrency and blocks remain distributed.");
    }

    builder.Services.AddSingleton<IUserActivityRateLimiter, InMemoryUserActivityRateLimiter>();
    builder.Services.AddSingleton<IUserConcurrencyGate, InMemoryUserConcurrencyGate>();
    builder.Services.AddSingleton<IUserBlockStore, UserBlockStore>();
}
else
{
    var redisConfiguration = ConfigurationOptions.Parse(redisConnection);
    redisConfiguration.AbortOnConnectFail = false;
    redisConfiguration.ConnectRetry = 1;
    redisConfiguration.ConnectTimeout = 2_000;
    redisConfiguration.SyncTimeout = 1_000;
    redisConfiguration.AsyncTimeout = 1_000;

    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(redisConfiguration));
    builder.Services.AddSingleton<IUserActivityRateLimiter, RedisUserActivityRateLimiter>();
    builder.Services.AddSingleton<IUserConcurrencyGate, RedisUserConcurrencyGate>();
    builder.Services.AddSingleton<IUserBlockStore, RedisUserBlockStore>();
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
builder.Services.Configure<TelemetryPersistenceOptions>(
    builder.Configuration.GetSection("Telemetry"));
builder.Services.AddSingleton<TelemetryPersistenceHealth>();
builder.Services.AddSingleton<FileTelemetrySpool>();

var clickHouseBaseUrl = builder.Configuration["Telemetry:ClickHouseBaseUrl"];
if (string.IsNullOrWhiteSpace(clickHouseBaseUrl))
{
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "Telemetry:ClickHouseBaseUrl is required in Production so audit telemetry remains durable.");
    }

    builder.Services.AddSingleton<ITelemetrySink, LoggingTelemetrySink>();
}
else
{
    builder.Services.AddHttpClient("clickhouse", client =>
        client.Timeout = TimeSpan.FromSeconds(5))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            MaxResponseHeadersLength = 32
        });
    builder.Services.AddSingleton<ClickHouseTelemetrySink>();
    builder.Services.AddSingleton<ITelemetrySink, ResilientTelemetrySink>();
}
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
        AutomaticDecompression =
            System.Net.DecompressionMethods.GZip |
            System.Net.DecompressionMethods.Deflate |
            System.Net.DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxConnectionsPerServer = protection.MaxConcurrentArcGisRequests,
        MaxResponseHeadersLength = 64
    });

var authority = builder.Configuration["EarthId:Authority"]
    ?? throw new InvalidOperationException("EarthId:Authority is required.");
var audience = builder.Configuration["EarthId:Audience"]
    ?? throw new InvalidOperationException("EarthId:Audience is required.");

if (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) ||
    authorityUri.Scheme != Uri.UriSchemeHttps ||
    !string.IsNullOrEmpty(authorityUri.UserInfo) ||
    !string.IsNullOrEmpty(authorityUri.Query) ||
    !string.IsNullOrEmpty(authorityUri.Fragment))
{
    throw new InvalidOperationException(
        "EarthId:Authority must be an absolute HTTPS URL without user-info, query, or fragment.");
}

if (string.IsNullOrWhiteSpace(audience))
    throw new InvalidOperationException("EarthId:Audience cannot be empty.");

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o =>
    {
        o.Authority = authority;
        o.Audience = audience;
        o.RequireHttpsMetadata = true;
        o.MapInboundClaims = false;
        o.TokenValidationParameters =
            EarthIdAuthentication.CreateValidationParameters(audience);
    })
    .AddCookie(AdminConsole.CookieScheme, options =>
    {
        options.Cookie.Name = "__Host-eiag-admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.LoginPath = "/admin/login";
        options.AccessDeniedPath = "/admin/denied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    })
    .AddOpenIdConnect(AdminConsole.OidcScheme, options =>
    {
        options.SignInScheme = AdminConsole.CookieScheme;
        options.Authority = authority;
        options.ClientId = adminOidcClientId ?? "";
        options.ClientSecret = adminOidcClientSecret;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.CallbackPath = "/signin-oidc";
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
    })
    .AddPolicyScheme(
        AdminConsole.PolicyScheme,
        AdminConsole.PolicyScheme,
        options =>
        {
            options.ForwardDefaultSelector = context =>
                context.Request.Headers.Authorization.ToString()
                    .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? JwtBearerDefaults.AuthenticationScheme
                    : AdminConsole.CookieScheme;
        });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("earthid-user", policy =>
    {
        policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
            EarthIdAuthentication.HasStableSubject(context.User));
    });

    options.AddPolicy("gateway-admin", policy =>
    {
        policy.AddAuthenticationSchemes(AdminConsole.PolicyScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
            EarthIdAuthentication.HasStableSubject(context.User) &&
            AdminAuthorization.IsAuthorized(context.User, adminOptions));
    });
});

if (protection.SourceRateLimit.Enabled)
{
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter =
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var source =
                    context.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown";

                return RateLimitPartition.GetTokenBucketLimiter(
                    source,
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = protection.SourceRateLimit.TokenLimit,
                        TokensPerPeriod = protection.SourceRateLimit.TokensPerPeriod,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(
                            protection.SourceRateLimit.ReplenishmentPeriodSeconds),
                        AutoReplenishment = true,
                        QueueLimit = 0
                    });
            });
    });
}

var app = builder.Build();
app.UseForwardedHeaders();

if (app.Environment.IsProduction())
    app.UseHsts();

app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        if (context.Request.Path.StartsWithSegments("/admin"))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; " +
                "form-action 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'";
        }

        return Task.CompletedTask;
    });

    await next();
});

if (protection.SourceRateLimit.Enabled)
    app.UseRateLimiter();

app.UseRequestTimeouts();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", (
    ITelemetryQueue telemetry,
    TelemetryPersistenceHealth persistence,
    IServiceProvider services) =>
{
    var redis = services.GetService<IConnectionMultiplexer>();
    var redisReady = redis is null || redis.IsConnected;
    var status = redisReady ? "ready" : "degraded";

    return Results.Json(
        new
        {
            status,
            redis = new
            {
                configured = redis is not null,
                connected = redis?.IsConnected
            },
            sourceRateLimit = new
            {
                enabled = protection.SourceRateLimit.Enabled
            },
            telemetry = new
            {
                accepted = telemetry.Accepted,
                dropped = telemetry.Dropped,
                persistedBatches = persistence.PersistedBatches,
                spooledBatches = persistence.SpooledBatches,
                replayedBatches = persistence.ReplayedBatches,
                droppedSpoolBatches = persistence.DroppedSpoolBatches,
                storageFailures = persistence.StorageFailures,
                lastSuccess = persistence.LastSuccess,
                lastFailure = persistence.LastFailure
            }
        },
        statusCode: redisReady
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/health", () => Results.Redirect("/health/ready"));

app.MapMethods(
        "/arcgis/{**path}",
        new[] { "GET", "POST" },
        GatewayHandler.HandleAsync)
    .RequireAuthorization("earthid-user")
    .WithRequestTimeout("arcgis-gateway");

AdminConsole.MapRoutes(app);

app.Run();

public partial class Program { }
