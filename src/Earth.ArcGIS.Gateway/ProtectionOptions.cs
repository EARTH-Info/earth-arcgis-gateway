namespace Earth.ArcGIS.Gateway;

public sealed class SourceRateLimitOptions
{
    public bool Enabled { get; set; } = false;
    public int TokenLimit { get; set; } = 1_000;
    public int TokensPerPeriod { get; set; } = 1_000;
    public int ReplenishmentPeriodSeconds { get; set; } = 60;
}

public sealed class ProtectionOptions
{
    public long MaxConcurrentConnections { get; set; } = 1_024;
    public long MaxRequestBodyBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxRequestHeadersTotalSize { get; set; } = 32 * 1024;
    public int MaxRequestHeaderCount { get; set; } = 64;
    public int RequestHeadersTimeoutSeconds { get; set; } = 10;
    public int GatewayRequestTimeoutSeconds { get; set; } = 45;
    public int MaxConcurrentArcGisRequests { get; set; } = 64;
    public int MaxRequestPathLength { get; set; } = 4_096;
    public int MaxQueryStringLength { get; set; } = 16_384;
    public string[] TrustedProxies { get; set; } = Array.Empty<string>();
    public SourceRateLimitOptions SourceRateLimit { get; set; } = new();

    public void Validate()
    {
        if (MaxConcurrentConnections <= 0 ||
            MaxRequestBodyBytes <= 0 ||
            MaxRequestHeadersTotalSize <= 0 ||
            MaxRequestHeaderCount <= 0 ||
            RequestHeadersTimeoutSeconds <= 0 ||
            GatewayRequestTimeoutSeconds <= 0 ||
            MaxConcurrentArcGisRequests <= 0 ||
            MaxRequestPathLength <= 0 ||
            MaxQueryStringLength <= 0)
        {
            throw new InvalidOperationException("Protection limits must all be positive.");
        }

        if (SourceRateLimit.Enabled &&
            (SourceRateLimit.TokenLimit <= 0 ||
             SourceRateLimit.TokensPerPeriod <= 0 ||
             SourceRateLimit.ReplenishmentPeriodSeconds <= 0))
        {
            throw new InvalidOperationException(
                "Protection:SourceRateLimit limits must be positive when enabled.");
        }
    }
}
