# EI ArcGIS Gateway — Tier-1 Remediation Plan

Audit baseline: `7913d91c94cd4a1d2b1bf6ff550d6be4b67701c7`

Audit verdict at baseline: **CRITICAL REJECT**

Parent product epic: #2  
Tier-1 remediation epic: #15  
PR under audit: #1  
Final independent audit gate: #25

## Governance rule

This document is the implementation/audit contract for the Tier-1 remediation pass. An item is not complete because code exists or because a developer says it is done.

Every product-side item requires:

1. implementation;
2. negative/boundary tests;
3. integration evidence where applicable;
4. docs/config alignment;
5. green CI on the exact head;
6. no unresolved Critical/Major audit blocker.

External environment unavailability must remain BLOCKED EXTERNAL and must never be converted into a false PASS.

## Execution order

### P0 — merge blockers

#### #16 Access API contract + client ArcGIS credential rejection

- Canonical Access API response is `allowed: true/false` plus `reasonCode`, `policyVersion`, optional `rateLimitProfile`, optional `cacheTtlSeconds`.
- Align external request field names, including `applicationId` where defined by the contract.
- Fail closed on malformed/missing/wrong-type decisions, timeout, network error, non-2xx and oversized responses.
- Reject browser-supplied ArcGIS `token` in query/form and `X-Esri-Authorization` before upstream processing.
- Prove upstream receives only the gateway credential.

### P1 — product-side merge blockers

#### #17 User-centric GIS-aware rate limiting

Canonical design: `docs/USER-CENTRIC-GIS-RATE-LIMITING.md`.

Architecture decision:
- validated EarthID user/application is the primary limiter identity;
- source-IP limiter is optional pre-auth/edge protection only;
- aggregate user/app budget spans all layers/services;
- secondary user/layer/operation/time counters support targeted controls and analysis;
- Redis is the synchronous enforcement store;
- ClickHouse telemetry stays asynchronous/off the request path;
- per-user interactive/heavy concurrency prevents one user monopolizing ArcGIS;
- final thresholds are calibrated from JTUWMA/GeoForest staging rather than guessed.

Child work packages:
- #26 optional source-IP edge limiter;
- #27 aggregate EarthID user/application Redis token bucket;
- #28 GIS request classification + secondary user/layer/time controls;
- #29 per-user interactive/heavy distributed concurrency;
- #30 telemetry + materialized risk/profile signals;
- #31 JTUWMA/GeoForest staging calibration.

#### #18 Proxy/request/token/stream hardening

- One validated ProtectionOptions source of truth for request/body/path/query limits.
- Bound Portal/OAuth/federated token response JSON.
- Distinguish credential-provider and ArcGIS-service failures.
- Correctly handle mid-stream upstream failures.
- Align encoded-resource canonicalization between resolver and allowlist.
- Verify safe request/response headers and ArcGIS REST compatibility corpus.

#### #19 Durable telemetry/audit

- Durable ClickHouse audit query abstraction.
- Admin audit explorer queries durable history, not only node-local recent events.
- Persist admin enforcement audit durably.
- Extend privacy-safe telemetry dimensions required for abuse analysis.
- Add retention/TTL policy.
- Bound local spool by files and bytes; quarantine corrupt spool files.
- Real ClickHouse schema/insert/query/outage/recovery tests in CI.

#### #20 Admin Console + versioned control plane

- Viewer / security operator / gateway administrator role tiers.
- Versioned applications/resources/rate-profile configuration with actor/timestamp/before/after/version/rollback.
- PostgreSQL is the recommended control-plane source of truth; Redis may cache runtime state.
- No secrets in ordinary control-plane records or API/UI responses.
- Complete real-data audit/security/health/analytics/rate/config surfaces required for the intended MVP.
- Durable audit all enforcement/config mutations.

#### #21 Hosted HTTP + runtime CI

- Hosted ASP.NET integration tests against the actual middleware/authentication pipeline.
- JWT negative tests through HTTP.
- Admin bearer/cookie/RBAC/CSRF tests through HTTP.
- Real ClickHouse CI service.
- Production non-root/read-only container runtime smoke with writable mounted spool.
- Secret scan and container image vulnerability scan.
- Publish test/security artifacts.

#### #22 Full SEC-004 attack/load suite

Required runnable scenarios:

1. unauthenticated flood;
2. invalid JWT flood;
3. valid-user burst;
4. many valid users concurrently;
5. expensive spatial-query burst;
6. oversized POST/body;
7. slow-client/slow-body;
8. random/nonexistent ArcGIS paths;
9. repeated 498/499 retry-amplification test;
10. Access API degradation;
11. Redis degradation/recovery;
12. ClickHouse outage/recovery;
13. ArcGIS slow/unavailable/recovery;
14. Admin endpoint isolation;
15. spoofed forwarded-IP/header attempts.

Capture exact commit/image digest, P50/P95/P99, throughput, CPU/memory, open connections, queue depth, rejections, ArcGIS/Access API request counts, telemetry state, Redis state, upstream-concurrency peak and post-load recovery.

#### #24 Production operations/hardening documentation

Complete and rehearse security hardening, reverse-proxy/WAF, ArcGIS, EarthID/OIDC, Access API, Admin/operator, incident, Redis/ClickHouse recovery, credential rotation, backup/restore, HA, monitoring/alerts, retention/privacy and upgrade/rollback procedures.

## External/reference validation

### #10 JTUWMA Enterprise 11.3

- Real EarthID subject and application identity.
- Representative user × service/layer × operation ALLOW/DENY matrix.
- ArcGIS JS SDK protected traffic through gateway.
- No ArcGIS token in browser and no direct protected-service bypass.
- Real token expiry and 498/499 retry-once evidence.
- Durable audit attribution.

### #23 GeoForest

- Same gateway binary and contract, configuration/provider changes only.
- Validate actual Feature/Map/Scene/Image/VectorTile resources used by GeoForest.
- Prove app/rate/audit isolation from JTUWMA.

### #11 Enterprise 11.5 OAuth

- Real client-credentials compatibility and least-privilege permissions.
- Determine direct app-token vs federated exchange behavior against actual target services.
- Expiry/retry and credential-leakage checks.
- Preserve identical EarthID/Access API/rate/audit contract.

## Final audit gate — #25

The final audit is auditor-owned. The implementation developer must not self-approve it.

Before audit:

- #16–#22 and #24 must contain required evidence for the claimed merge/release level;
- exact PR head CI must be green;
- completion matrix must match actual evidence;
- unresolved external gates must remain explicit.

Final audit must recompute the full diff against `master`, inspect every changed file line-by-line, verify CI/test evidence on the exact head, and issue one verdict:

- **CRITICAL REJECT** — build/security/core-contract/data-integrity blocker;
- **CHANGES REQUIRED** — Major merge blocker remains;
- **APPROVED** — no unresolved Critical/Major merge blocker for the claimed release level.

## PR #1 merge rule

PR #1 remains blocked until #25 gives approval. Green CI alone is insufficient.