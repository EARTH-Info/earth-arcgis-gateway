# EI ArcGIS Gateway — Product Implementation Plan

Status: Execution backlog
Role ownership: System Architecture / ArcGIS Integration / EarthID Enterprise Engineering
Reference integrations: JTUWMA first, GeoForest second

## Product objective

Provide a reusable identity-aware security, governance and observability gateway between custom ArcGIS JS SDK applications and secured ArcGIS Enterprise resources.

The application user authenticates with EarthID/OIDC. ArcGIS never receives or provisions that application user. The gateway authenticates upstream to ArcGIS using a restricted machine/service identity.

Stable contract:

EarthID user -> App -> EI ArcGIS Gateway -> App Access API -> policy/rate enforcement -> ArcGIS Enterprise -> audit/analytics.

An ArcGIS Enterprise upgrade must not require JTUWMA/GeoForest to change their EarthID session model, application authorization model or gateway URL.

## Non-negotiable security boundaries

1. ArcGIS credentials/tokens never reach browser clients.
2. EarthID JWT subject is obtained only from a cryptographically validated token, never a browser parameter/header supplied as identity.
3. Authorization is fail closed.
4. Gateway global resource allowlist is an upper bound. Access API can reduce access; it cannot expand it.
5. Read-only mode uses explicit allowed ArcGIS operations, not only a write-operation blocklist.
6. Every allow, deny, throttle and block is auditable.
7. Secrets/JWTs/tokens/passwords are never logged.
8. AI may analyse telemetry but cannot be the sole enforcement mechanism or permanently block users autonomously.
9. ArcGIS upstream authentication is replaceable independently of application authorization.

# Workstreams and tasks

## WS1 — Product foundation and build gate

### GATE-001 Repository/build baseline
- Select currently supported .NET LTS after deployment compatibility verification.
- Pin supported packages.
- Add solution file and test projects.
- Add GitHub Actions restore/build/test.
- Add dependency/security scanning.
- Document configuration and secret injection.

PASS:
- Clean CI restore/build succeeds.
- Automated tests execute on every PR.
- No committed secrets.
- Dependency scan has no unresolved critical finding.

## WS2 — EarthID identity boundary

### ID-001 JWT validation
Validate issuer, audience, signature, lifetime and required immutable sub. Disable ambiguous inbound claim mapping.

PASS:
- Valid EarthID JWT accepted.
- Expired/wrong issuer/wrong audience/invalid signature rejected.
- Missing sub rejected.
- Browser cannot override sub.

### ID-002 Application identity
Resolve application/tenant from gateway-owned configuration and trusted token/client context. Do not trust arbitrary application IDs from query strings.

PASS:
- Known app resolves.
- Unknown/mismatched app denied and audited.

### ID-003 Generic OIDC compatibility
Keep EarthID as reference identity provider while avoiding EarthID-specific coupling in request authorization primitives.

PASS:
- Identity interface exposes stable subject/tenant/app claims.
- EarthID remains first production provider.

## WS3 — ArcGIS upstream authentication/version isolation

### AG-001 Enterprise 11.3 federation provider
Portal service identity -> Portal token -> federated server token using exact registered serverUrl. Cache using returned expires and refresh proactively.

PASS:
- Valid secured FeatureServer/MapServer request succeeds.
- Tokens never leave gateway.

### AG-002 Auth failure recovery
On first 498/499 (including ArcGIS JSON error under HTTP 200) or applicable 401: invalidate server token, reacquire and retry once.

PASS:
- Forced stale token recovers.
- POST body/query is unchanged after retry.
- Second failure returns upstream failure; no loop.

### AG-003 OAuth provider for Enterprise 11.4/11.5+
Implement replaceable upstream credential provider supporting OAuth app credentials where target ArcGIS resources/privileges permit.

PASS:
- Switching provider requires configuration, not client/API changes.
- Gateway policy/audit/rate controls remain identical.

### AG-004 ArcGIS request compatibility
Support required GET/POST ArcGIS REST semantics, safe request/response headers, compression, caching metadata, streaming and large-response controls.

PASS:
- ArcGIS JS SDK reference scenarios render correctly.
- Query POST, metadata, attachments/related operations approved by policy work.
- No credential leakage.

### AG-005 Canonical resource resolver
Parse canonical Portal/server/service/layer/operation identities. Prevent encoded path bypass, traversal, SSRF, unapproved redirect and host escape.

PASS:
- Security test corpus passes.
- Unknown/unapproved resource is denied before upstream call.

### AG-006 WebMap/Scene integration
Handle secured WebMap/operational layer URLs without allowing browser to bypass gateway. Define safe URL rewriting/application construction strategy. Include WebScene/SceneServer testing for GeoForest/JTU use where applicable.

PASS:
- Browser network trace shows protected operational requests through gateway.
- No direct secured ArcGIS URL requiring client ArcGIS token.

## WS4 — Application Access API authorization

### AUTHZ-001 Access API client
Request effective access using trusted values:
earthIdSub, tenant, application, service, layer, operation, method, correlationId.

Response:
allowed, reasonCode, policyVersion, optional rateLimitProfile, optional cacheTtlSeconds.

PASS:
- ALLOW proceeds.
- DENY returns 403 without ArcGIS call.
- timeout/error/malformed/unknown fails closed.

### AUTHZ-002 Policy cache
Allow short-lived decision caching only when policy response permits it. Cache key includes identity/app/resource/operation/policy-relevant context.

PASS:
- Expiry respected.
- Deny/allow cannot bleed between users/resources.
- Administrative invalidation mechanism exists.

### AUTHZ-003 JTUWMA adapter
Integrate existing JTUWMA effective-access API.

PASS:
- Existing EarthID user permissions produce expected layer allow/deny.
- Current JTUWMA business authorization remains source of truth.

### AUTHZ-004 GeoForest adapter
Implement equivalent GeoForest policy integration without JTUWMA-specific gateway code.

PASS:
- Same gateway binary supports both apps via configuration/provider interface.

## WS5 — Rate limiting and protection

### RATE-001 Multi-dimensional limits
Support limits by subject, app, service, layer and operation with burst + sustained windows and concurrency.

### RATE-002 Weighted GIS cost
Assign configurable weights to metadata, count, attribute query, spatial query, geometry, pagination/export/large responses.

### RATE-003 Distributed state
Use shared state such as Redis for multiple gateway nodes; local development fallback may be in-memory.

PASS for WS5:
- Limits work consistently across nodes.
- 429 includes appropriate retry guidance.
- Allow/throttle/block decision is audited.
- One user cannot consume another user's budget.

## WS6 — Audit and telemetry

### AUD-001 Structured request decision log
Minimum fields:
timestamp, correlationId, earthIdSub, tenant, application, service, layer, operation, method, decision, reasonCode, policyVersion, status, latencyMs, responseBytes, recordCount where safely measurable, rate-limit state, client network metadata according to privacy policy.

### AUD-002 Sensitive-data controls
Redact Authorization, JWT, ArcGIS tokens, passwords/secrets and configured sensitive query values. Prefer normalized query fingerprint/features over indefinite raw where-clause retention.

### AUD-003 Telemetry storage
Separate operational logs from analytical telemetry. Define retention, indexing and export to EDH/SIEM.

PASS:
- Every enforcement decision can be traced by correlationId.
- Secret scanning of representative logs finds no credential.
- Admin can reconstruct who accessed which protected resource and when.

## WS7 — Security analytics and abuse controls

### SEC-001 Deterministic detection
Metrics: request rate, bytes, features, spatial-query frequency, sequential pagination, repeated query fingerprint, errors, services accessed, time pattern, baseline deviation.

### SEC-002 Enforcement
ALLOW / MONITOR / THROTTLE / TEMP_BLOCK / ADMIN_REVIEW. Permanent/revocation action requires authorized administrative decision.

### SEC-003 AI analysis
AI summarizes anomalies, profiles consumption and explains why behavior is suspicious using stored telemetry. AI recommendations remain reviewable.

PASS:
- Simulated scraping/bulk extraction triggers deterministic control.
- Reason and evidence visible to admin.
- False-positive handling/unblock is audited.

## WS8 — EI ArcGIS Gateway Admin Console

### UI-001 Overview dashboard
Requests, allowed, denied, throttled, blocked, ArcGIS errors, P50/P95/P99, active identities/apps/services/layers, gateway health.

### UI-002 Applications
Configure JTUWMA, GeoForest and future applications; identity/access provider bindings and environment status.

### UI-003 ArcGIS resources
Manage approved Enterprise sites, services, layers, operations and resource allowlists. Never display secrets.

### UI-004 Access policy view
Show effective Access API decision, reason and policy version; test a policy decision without executing an ArcGIS request.

### UI-005 Live traffic
Near-real-time filter by app/user/service/layer/decision/status/correlation ID.

### UI-006 Audit explorer
Search/filter/export authorized audit data with retention controls.

### UI-007 User/resource analytics
Consumption history, top services/layers, bytes/features, denial/throttle history and behavior baseline.

### UI-008 Rate-limit management
Profiles and assignments with change history.

### UI-009 Security center
Anomalies, temporary blocks, evidence, review, unblock and administrative audit.

### UI-010 Gateway/ArcGIS health
Nodes, version, Access API health, ArcGIS upstream health, auth-provider health/expiry metadata without tokens.

PASS:
- RBAC protects administrative functions.
- Every configuration/enforcement change is audited.
- Console cannot expose upstream secrets.

## WS9 — Configuration and administration

### CFG-001 Versioned configuration
Configuration changes have actor, timestamp, before/after, version and rollback.

### CFG-002 Admin RBAC
At minimum viewer, security operator and gateway administrator roles; map to EarthID groups/claims where appropriate.

### CFG-003 Multi-application/multi-agency isolation
Design data/configuration boundaries so one deployment can safely host multiple applications; evaluate tenant isolation before multi-agency hosting.

## WS10 — JTUWMA production validation

### JTU-001 Existing app compatibility
Existing ArcGIS JS SDK map workflow uses gateway while EarthID remains application identity.

### JTU-002 Access correctness
Build a test matrix of representative EarthID users × services/layers × expected decision.

### JTU-003 Long-duration/token-expiry test
Keep application active across Portal/federated token expiration and prove uninterrupted authorized map/query use.

### JTU-004 Enterprise upgrade rehearsal
Run same client/access test suite against 11.3 baseline and 11.5 staging target.

PASS:
- No client identity architecture change.
- Expected authorization matrix identical.
- No ArcGIS token reaches browser.
- No unexpected direct secured-service calls.
- Audit identifies each EarthID user.

## WS11 — GeoForest reference validation

Repeat equivalent identity/access/render/query/expiry/upgrade tests against GeoForest. Include map/scene services actually used by GeoForest.

PASS:
- Gateway core has no JTU-specific dependency.
- GeoForest works by configuration/provider integration.

## WS12 — Performance, HA and resilience

### PERF-001 Load baseline
Measure overhead added by identity validation, Access API, rate limiting, audit and proxying.

### PERF-002 Failure modes
Test Access API unavailable, ArcGIS unavailable, Redis unavailable, telemetry unavailable, token endpoint unavailable and slow upstream.

### PERF-003 HA
Multiple stateless gateway nodes behind load balancer; distributed policy/rate state; health/readiness endpoints.

PASS:
- Security components fail closed where authorization is uncertain.
- Telemetry failure policy is explicitly documented.
- No silent authorization bypass during dependency failure.

## WS13 — Deployment and operations

Containerized/on-prem deployment appropriate for government networks; environment-separated secrets; TLS; reverse proxy/load balancer; backup of configuration/audit according to retention policy; monitoring/alerts; runbooks; upgrade/rollback.

Deliver:
- installation guide
- security hardening guide
- ArcGIS registration/config guide
- EarthID/OIDC guide
- Access API integration guide
- admin/operator guide
- incident runbook
- version upgrade guide

## WS14 — Product release

### R1 Developer Preview
Core EarthID -> Access API -> ArcGIS pipeline + audit + tests.

### R2 JTUWMA Pilot
JTUWMA reference integration, rate limiting, operational dashboard.

### R3 GeoForest Pilot
Second application validates reusable architecture.

### R4 Product MVP
Admin Console, audit explorer, security controls, packaged deployment, documentation.

### R5 Enterprise 11.5 Validation
11.3 and 11.5 compatibility matrix green; OAuth upstream provider validated where applicable.

## Definition of Done

A task is not DONE because code was committed. It is DONE only when:
1. implementation exists;
2. automated tests pass;
3. security-negative tests pass where relevant;
4. integration evidence exists for integration tasks;
5. docs/config are updated;
6. CI is green;
7. no unresolved critical security defect exists.

## Immediate execution order

1. GATE-001 CI/build/test baseline.
2. ID-001/002 identity hardening.
3. AG-001/002 federation + expiry tests.
4. AG-005 canonical resource policy.
5. AUTHZ-001 Access API client and fail-closed middleware.
6. AUD-001 structured decision audit.
7. RATE-001 first per-sub/app/resource limiter.
8. JTU-001/002 first JTUWMA end-to-end validation.
9. AG-003 11.5 OAuth provider.
10. Admin Console foundation.
11. GeoForest integration.
12. HA/performance/security analytics and product packaging.
