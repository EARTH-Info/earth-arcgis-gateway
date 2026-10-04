# EI ArcGIS Gateway Completion Matrix

This file is the release truth for Epic #2. An item is COMPLETE only when its GitHub acceptance criteria have implementation and evidence.

Current product-side verification head: CI run #357 — build PASS, 105/105 tests PASS, production container build PASS, dependency vulnerability audit clean.

| Issue | Workstream | Status | Evidence / remaining gate |
|---|---|---|---|
| #3 | GATE-001 CI/build/test baseline | COMPLETE | CI #357: restore/build/test/container/vulnerability audit green |
| #4 | EarthID identity/app resolution | COMPLETE | JWT negative suite covers expired/wrong issuer/wrong audience/wrong signature/missing sub; trusted app identity tests green |
| #5 | ArcGIS 11.3 federation/retry | PRODUCT-SIDE COMPLETE / EXTERNAL VALIDATION | token cache/expiry/invalidation, exact serverUrl, 498/499 retry-once and POST replay tests green; real 11.3 staging proof still required |
| #6 | Canonical resource/security boundary | COMPLETE | traversal/double-encoding/host/path boundary/negative layer tests + explicit read-operation/tail policy green |
| #7 | Access API fail-closed | COMPLETE | ALLOW/DENY/malformed/unknown/non-success/network/timeout/oversized response tests + DENY-no-ArcGIS gateway test green |
| #8 | Structured audit | COMPLETE | allow/deny/throttle/block paths emit structured telemetry; async persistence/spool/admin query paths implemented and tested |
| #9 | Rate controls | COMPLETE | subject/tenant/app/service/layer/operation key, burst+sustained+daily budgets, real Redis concurrency/fixed-window/operation-isolation tests green |
| #10 | JTUWMA reference integration | BLOCKED EXTERNAL | requires JTU staging EarthID + Access API + ArcGIS Enterprise and browser/network-trace evidence |
| #11 | Enterprise 11.5 OAuth provider | PRODUCT-SIDE COMPLETE / EXTERNAL VALIDATION | config-switchable client-credentials provider + optional federated exchange implemented/tested; real Enterprise 11.5 compatibility suite still required |
| #12 | Admin Console | COMPLETE | EarthID RBAC, live real telemetry, applications/resources/rate/security/auth health, distributed block/unblock and admin audit implemented |
| #13 | Async telemetry/analytics | PRODUCT-SIDE COMPLETE / EXTERNAL PERFORMANCE EVIDENCE | bounded async queue, ClickHouse sink, durable local spool/replay, health, failure tests and load procedure implemented; staging P50/P95/P99/CPU/memory evidence still required |
| #14 | DDoS/overload resilience | PRODUCT-SIDE COMPLETE / EXTERNAL LOAD EVIDENCE | pre-auth source limiting, Kestrel bounds, body/header limits, trusted proxies, upstream concurrency/bounded connections/timeouts, fail-closed Redis/block store, load/security harness implemented; measured staging attack/load evidence still required |

## Release rule

Do not close Epic #2 or merge PR #1 as a production release until the remaining external gates are attached:

1. JTUWMA end-to-end staging validation (#10, including actual EarthID subject/access matrix and browser trace).
2. ArcGIS Enterprise 11.5 staging validation for the OAuth provider (#11).
3. Measured performance/security-load evidence for #13/#14 using the documented staging procedure.

Unavailable external environments must never be converted into a false PASS.
