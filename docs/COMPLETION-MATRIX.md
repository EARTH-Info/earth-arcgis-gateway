# EI ArcGIS Gateway Completion Matrix

This file is the release truth for Epic #2. An item is COMPLETE only when its GitHub acceptance criteria have implementation and evidence.

Tier-1 audit baseline: `7913d91c94cd4a1d2b1bf6ff550d6be4b67701c7`  
Last verified implementation CI at that baseline: run #416 — build PASS, 114/114 tests PASS, production container build PASS, dependency vulnerability audit clean.  
Tier-1 audit verdict: **CRITICAL REJECT**. Green CI did not prove the canonical Access API contract or browser ArcGIS credential boundary.

Canonical remediation plan: `docs/TIER1-REMEDIATION-PLAN.md`  
Remediation epic: #15  
Final independent audit gate: #25

| Issue | Workstream | Status | Evidence / remaining gate |
|---|---|---|---|
| #3 | GATE-001 CI/build/test baseline | BASELINE COMPLETE / EXPANSION REQUIRED | Existing CI is green; #21 adds hosted HTTP, real ClickHouse, runtime-container, secret and image-security gates |
| #4 | EarthID identity/app resolution | PRODUCT-SIDE VERIFIED / HOSTED HTTP EVIDENCE REQUIRED | Component JWT/app tests exist; #21 must prove production middleware/auth pipeline |
| #5 | ArcGIS 11.3 federation/retry | PRODUCT-SIDE PARTIAL / EXTERNAL VALIDATION | Token lifecycle/retry tests exist; #18 adds remaining token/stream bounds; #10 requires real 11.3 evidence |
| #6 | Canonical resource/security boundary | CHANGES REQUIRED | Current path defenses are substantial; #16 must reject browser ArcGIS credentials and #18 must align encoded-resource allowlist/canonicalization |
| #7 | Access API fail-closed | **CRITICAL CONTRACT FIX REQUIRED** | Audit found implementation expects `decision=ALLOW/DENY` while canonical contract/issue specifies `allowed=true/false`; tracked in #16 |
| #8 | Structured audit | CHANGES REQUIRED | Async write path exists; durable admin query/history, extended telemetry dimensions and durable admin enforcement audit tracked in #19 |
| #9 | Rate controls | CHANGES REQUIRED | Redis multi-window budgets exist; ArcGIS cost semantics and per-user distributed concurrency tracked in #17 |
| #10 | JTUWMA reference integration | BLOCKED EXTERNAL | Detailed real EarthID/JTUWMA Access API/ArcGIS 11.3/browser matrix recorded in #10 |
| #11 | Enterprise 11.5 OAuth provider | PRODUCT-SIDE PARTIAL / EXTERNAL VALIDATION | Fake-handler provider tests exist; real 11.5 least-privilege/direct-vs-exchange compatibility remains in #11 |
| #12 | Admin Console | FOUNDATION ONLY / CHANGES REQUIRED | Current RBAC/live console exists; durable audit, role tiers, versioned configuration/control plane and MVP surfaces tracked in #19/#20 |
| #13 | Async telemetry/analytics | CHANGES REQUIRED / EXTERNAL PERFORMANCE EVIDENCE | Queue/spool/ClickHouse write path exists; durable query, retention, byte bounds, real ClickHouse CI in #19/#21; measured evidence in #22 |
| #14 | DDoS/overload resilience | CHANGES REQUIRED / EXTERNAL LOAD EVIDENCE | Core Kestrel/pre-auth/upstream bounds exist; per-user concurrency #17, proxy bounds #18, full 15-scenario harness/evidence #22 remain |
| #15 | Tier-1 remediation epic | OPEN / MERGE BLOCKER | Parent backlog for all audit remediation work |
| #16 | Access contract + browser ArcGIS credential rejection | P0 OPEN | Must close before merge |
| #17 | Rate-cost + distributed user concurrency | P1 OPEN | Must close before merge |
| #18 | Proxy/request/token/stream hardening | P1 OPEN | Must close before merge |
| #19 | Durable telemetry/audit | P1 OPEN | Must close before merge |
| #20 | Admin Console/control plane | P1 OPEN | Required before claiming Product MVP completion |
| #21 | Hosted HTTP/ClickHouse/container CI | P1 OPEN | Must close before merge |
| #22 | Full attack/load suite | P1 OPEN | Harness required before resilience claims; measured staging evidence required before production |
| #23 | GeoForest reference validation | BLOCKED EXTERNAL / FUTURE REFERENCE GATE | Required to prove reusable second-application product behavior |
| #24 | Production operations/hardening docs | P1 OPEN | Required before pilot/production release |
| #25 | Final independent Tier-1 re-audit | OPEN / FINAL MERGE GATE | Implementation developer must not self-approve; PR #1 remains blocked until auditor verdict is APPROVED |

## Merge rule

PR #1 must not merge until:

1. #16–#19 and #21 are closed with evidence;
2. #17/#18 security/performance boundary changes have negative tests;
3. #20 is either completed for the claimed release level or explicitly re-scoped by the product owner without falsely marking Product MVP complete;
4. exact PR head CI is green;
5. completion matrix matches actual evidence;
6. #25 final independent Tier-1 audit returns **APPROVED** with no unresolved Critical/Major merge blocker.

## External release gates

For pilot/production claims, additionally require applicable evidence from:

- #10 JTUWMA Enterprise 11.3 staging;
- #23 GeoForest second-application validation;
- #11 Enterprise 11.5 OAuth staging where that deployment mode is claimed;
- #22 measured performance/security-load runs.

Unavailable external environments must never be converted into a false PASS.
