namespace Earth.ArcGIS.Gateway;

public sealed record TelemetryEvent(
    DateTimeOffset Timestamp,
    string EarthIdSub,
    string? Tenant,
    string Application,
    string Service,
    string ServiceType,
    int? LayerId,
    string Operation,
    string Method,
    int StatusCode,
    long DurationMs,
    string Decision,
    string ReasonCode,
    string? PolicyVersion,
    string CorrelationId);
