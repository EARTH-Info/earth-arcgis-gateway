# EI ArcGIS Gateway — User-Centric GIS Rate Limiting Plan

Parent issue: #17  
Remediation epic: #15  
Audit gate: #25

## 1. Architecture decision

EI ArcGIS Gateway serves authenticated GIS applications. The primary limiter identity is therefore the validated EarthID user, not source IP.

Source-IP rate limiting is optional pre-auth/edge protection only. It exists for unauthenticated flood, invalid-JWT storm and obvious network abuse. It must not be used as the normal GIS usage quota because many legitimate users can share one agency NAT/proxy address and ArcGIS map interactions naturally generate short multi-layer request bursts.

The primary enforcement identity is:

`earthIdSub + tenant + application`

Secondary resource/activity dimensions are:

`earthIdSub + tenant + application + service + layer + operation + time-window`

## 2. Non-negotiable design rules

1. A normal pan/zoom/toggle-layer burst must not be treated as abuse merely because many requests happen at once.
2. Adding more visible layers must not multiply the user's total allowance.
3. Sustained automated extraction must eventually throttle even if it spreads requests across many layers/services.
4. One authenticated user must not be able to occupy all ArcGIS upstream capacity.
5. ClickHouse is analytical/audit storage and must never be queried synchronously for every ArcGIS request.
6. Redis holds live enforcement state: token buckets, rolling counters, concurrency leases and temporary risk/profile state.
7. The same normalized GIS activity model feeds both enforcement and telemetry so the two cannot drift.
8. No JWT, ArcGIS token, client secret, password or raw sensitive query content may be persisted in limiter/telemetry state.
9. Production limiter behavior must be deterministic and testable. AI may analyse telemetry but cannot become the sole synchronous enforcement mechanism.
10. Initial numeric thresholds are provisional until calibrated against JTUWMA/GeoForest staging traces.

## 3. Request flow

```text
Request
  |
  +--> Optional source/IP edge limiter
  |      - pre-auth flood protection only
  |      - may be disabled behind approved WAF/reverse proxy
  |
  +--> EarthID JWT validation
  |
  +--> Trusted application identity resolution
  |
  +--> Normalize GIS request into RequestActivity
  |      - resource
  |      - operation
  |      - request class
  |      - cost units
  |      - spatial/geometry/pagination features
  |      - normalized query fingerprint
  |
  +--> Aggregate user/application token bucket (Redis)
  |
  +--> Secondary user/resource/time controls (Redis)
  |
  +--> Access API authorization / approved short policy cache
  |
  +--> Per-user distributed concurrency gate (Redis)
  |
  +--> Global ArcGIS upstream bulkhead
  |
  +--> ArcGIS Enterprise
  |
  +--> Async telemetry -> ClickHouse
```

## 4. Work packages

### #26 — Optional source-IP edge limiter

Purpose: protect the unauthenticated edge, not manage GIS user consumption.

Required configuration shape should support at least:

```json
{
  "Protection": {
    "SourceRateLimit": {
      "Enabled": false,
      "BurstCapacity": 1000,
      "RefillPerSecond": 50
    }
  }
}
```

Exact defaults are provisional. When disabled in Production, deployment documentation must require equivalent WAF/reverse-proxy connection/flood protection.

### #27 — Aggregate EarthID user/application token bucket

Primary budget key:

`earthIdSub + tenant + application`

Use a Redis token-bucket/refill algorithm rather than a fixed requests-per-minute window.

Required profile fields:

- burst capacity units;
- refill units per second;
- optional sustained ceiling/window;
- optional daily ceiling;
- Retry-After behavior;
- profile/version identifier.

Every protected GIS request consumes this aggregate budget using the normalized request cost. The budget spans all layers and services.

Example behavior:

- user pans map with 8 visible layers -> short burst consumes several units quickly but remains within burst capacity;
- user pauses -> bucket refills;
- automated scraper continuously queries -> consumption outruns refill and throttles;
- scraper cannot bypass aggregate limit by switching layers.

### #28 — GIS activity classification + secondary user/layer/time controls

Introduce one normalized model, for example:

```csharp
public sealed record RequestActivity(
    string Class,
    int CostUnits,
    bool IsSpatial,
    bool ReturnsGeometry,
    bool IsPaged,
    bool IsHeavy,
    string? QueryFingerprint);
```

The exact type/name may differ, but normalization must happen once and be reused by limiter and telemetry.

Minimum request classes:

- navigation/static: metadata, tile, vector-tile/static resources, scene nodes;
- count-only;
- ids-only;
- extent-only;
- normal attribute query;
- query returning geometry by default;
- spatial query/filter;
- `outFields=*`;
- pagination/result offsets;
- identify/find;
- export/exportImage;
- bulk/extraction pattern.

Correct ArcGIS semantics: omitted `returnGeometry` must not automatically be interpreted as `false` for operations whose default returns geometry.

Secondary Redis counters use user/resource/operation/time dimensions to understand and selectively control behavior. These counters do not replace the aggregate user budget.

### #29 — Per-user distributed concurrency

Rate and concurrency are independent controls.

Required classes:

- interactive/navigation/query concurrency;
- heavy/extraction concurrency with a lower ceiling.

Suggested starting model for staging only:

- interactive user/app concurrency: approximately 8–16;
- heavy concurrency: approximately 2–4;
- global ArcGIS concurrency remains independently bounded.

These are not production defaults until measured.

Production leases must be stored atomically in Redis with TTL/crash recovery. Cancellation, upstream timeout and application failure must release exactly once.

### #30 — Telemetry and adaptive profile signals

Each request emits asynchronous telemetry with at least:

- timestamp;
- EarthID sub;
- tenant/application;
- service/layer/operation;
- request class;
- configured cost units;
- aggregate user budget remaining;
- concurrency outcome;
- duration/status;
- response bytes;
- record/feature count where safely measurable;
- spatial flag;
- geometry-return flag;
- pagination markers;
- normalized query fingerprint;
- ALLOW/THROTTLE/DENY reason.

Redis may maintain short-lived live signals such as:

- cost/request rate;
- per-layer recent activity;
- sequential pagination indicator;
- interactive/heavy concurrency;
- temporary risk/profile state.

Long-term ClickHouse analysis may derive NORMAL/WATCH/THROTTLED/TEMP_BLOCK recommendations. Any signal used synchronously must first be materialized into Redis/control-plane state with a TTL, version and reason.

### #31 — Staging calibration

Do not choose production thresholds by intuition.

Capture real JTUWMA and GeoForest usage including:

- ordinary desktop map load;
- large/high-resolution screen;
- many visible layers;
- rapid pan/zoom;
- layer toggle bursts;
- identify/select/search;
- idle then resume;
- multiple users behind same NAT;
- deliberate pagination/extraction.

Measure:

- burst requests/sec;
- cost units/sec;
- burst duration;
- refill/idle intervals;
- concurrent requests;
- heavy concurrency;
- layers touched per interaction;
- response bytes/feature counts;
- P50/P95/P99 latency;
- false throttle count.

Final profile must demonstrate that representative interactive traces pass while sustained extraction traces throttle predictably.

## 5. Recommended implementation sequence

1. #28 normalized GIS activity model first, because every later component depends on consistent request classification.
2. #27 aggregate user/app Redis token bucket.
3. #29 per-user interactive/heavy distributed concurrency.
4. #26 make source-IP limiter optional and decouple it from user quota.
5. #30 extend telemetry and Redis live signals.
6. Integrate #19 durable ClickHouse audit/query changes without putting ClickHouse on request path.
7. #31 staging calibration and profile tuning.
8. #22 load/security scenario validation.
9. #25 independent Tier-1 re-audit.

## 6. Initial policy behavior

The gateway should respond differently to legitimate burst versus sustained extraction.

### Legitimate interactive burst

```text
pan/zoom -> multiple visible-layer requests -> short high burst -> idle/refill
```

Expected result: ALLOW unless user concurrency or infrastructure bulkhead is genuinely exhausted.

### Sustained extraction

```text
continuous query/pagination -> no meaningful idle -> repeated/high-cost activity -> aggregate bucket drains
```

Expected result: THROTTLE with deterministic reason and Retry-After. Repeated high-risk activity may later move the user into a temporary stricter profile or TEMP_BLOCK state through explicit deterministic policy.

## 7. Audit acceptance criteria

The final auditor must verify:

- source IP is not the authenticated user quota;
- user/app aggregate allowance cannot be multiplied through layers;
- same-NAT users are independent;
- normal map bursts are accepted under calibrated profile;
- sustained extraction is throttled;
- concurrency fairness prevents one user monopolizing ArcGIS;
- Redis operations are atomic and bounded;
- ClickHouse outage cannot block normal ArcGIS requests;
- telemetry accurately matches the limiter decision and activity classification;
- all thresholds/profile versions are configuration-visible and auditable;
- negative, multi-node and failure-recovery tests pass on exact PR head.
