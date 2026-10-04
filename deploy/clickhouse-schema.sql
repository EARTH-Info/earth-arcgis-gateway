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
    rateLimitRemaining Nullable(Int32)
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(timestamp)
ORDER BY (application, service, timestamp, correlationId);
