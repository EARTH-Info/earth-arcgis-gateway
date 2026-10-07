CREATE TABLE IF NOT EXISTS gateway_telemetry
(
    timestamp DateTime64(3, 'UTC'),
    earthIdSub String,
    tenant Nullable(String),
    application LowCardinality(String),
    service String,
    serviceType LowCardinality(String),
    layerId Nullable(Int32),
    operation LowCardinality(String),
    method LowCardinality(String),
    statusCode UInt16,
    durationMs UInt64,
    decision LowCardinality(String),
    reasonCode LowCardinality(String),
    policyVersion Nullable(String),
    correlationId String,
    responseBytes Nullable(UInt64),
    rateLimitRemaining Nullable(Int32),
    activityClass Nullable(String),
    rateCostUnits Nullable(Int32),
    resourceRateRemaining Nullable(Int32),
    rateProfileVersion Nullable(String),
    concurrencyClass Nullable(String),
    isSpatial Nullable(Bool),
    returnsGeometry Nullable(Bool),
    isPaged Nullable(Bool),
    isHeavy Nullable(Bool),
    isExtractionLike Nullable(Bool),
    queryFingerprint Nullable(String),
    recordCount Nullable(UInt64)
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(timestamp)
ORDER BY (application, service, timestamp, correlationId)
TTL timestamp + INTERVAL 180 DAY DELETE;

-- Idempotent upgrade statements for deployments created from an older schema.
ALTER TABLE gateway_telemetry ADD COLUMN IF NOT EXISTS activityClass Nullable(String);
ALTER TABLE gateway_telemetry ADD COLUMN IF NOT EXISTS rateCostUnits Nullable(Int32);
ALTER TABLE gateway_telemetry ADD COLUMN IF NOT EXISTS resourceRateRemaining Nullable(Int32);
ALTER TABLE gateway_telemetry ADD COLUMN IF NOT EXISTS rateProfileVersion Nullable(String);
ALTER TABLE gateway_telemetry ADD COLUMN IF NOT EXISTS concurrencyClass Nullable(String);
ALTER TABLE gateway_telemetry ADD COLUMN IF NOT EXISTS isSpatial Nullable(Bool);
ALTER TABLE gateway_telemetry ADD COLUMN IF NOT EXISTS returnsGeometry Nullable(Bool);
ALTER TABLE gateway_telemetry ADD COLUMN IF NOT EXISTS isPaged Nullable(Bool);
ALTER TABLE gateway_telemetry ADD COLUMN IF NOT EXISTS isHeavy Nullable(Bool);
ALTER TABLE gateway_telemetry ADD COLUMN IF NOT EXISTS isExtractionLike Nullable(Bool);
ALTER TABLE gateway_telemetry ADD COLUMN IF NOT EXISTS queryFingerprint Nullable(String);
ALTER TABLE gateway_telemetry ADD COLUMN IF NOT EXISTS recordCount Nullable(UInt64);
ALTER TABLE gateway_telemetry MODIFY TTL timestamp + INTERVAL 180 DAY DELETE;

CREATE TABLE IF NOT EXISTS gateway_admin_audit
(
    timestamp DateTime64(3, 'UTC'),
    adminSubject String,
    action LowCardinality(String),
    targetSubject String,
    reason String,
    expiresAt Nullable(DateTime64(3, 'UTC')),
    correlationId String
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(timestamp)
ORDER BY (timestamp, adminSubject, targetSubject, correlationId)
TTL timestamp + INTERVAL 365 DAY DELETE;
