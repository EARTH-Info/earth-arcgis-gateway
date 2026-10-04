# EI ArcGIS Gateway Completion Matrix

This file is the release truth for Epic #2. An item is COMPLETE only when its GitHub acceptance criteria have implementation and evidence.

| Issue | Workstream | Status | Remaining evidence |
|---|---|---|---|
| #3 | GATE-001 CI/build/test baseline | COMPLETE | CI run #44/#59/#67 green |
| #4 | EarthID identity/app resolution | IN PROGRESS | Full JWT negative integration suite |
| #5 | ArcGIS 11.3 federation/retry | IN PROGRESS | Automated token lifecycle/replay tests + staging proof |
| #6 | Canonical resource/security boundary | IN PROGRESS | Expanded double-encoding/redirect/host security corpus |
| #7 | Access API fail-closed | IN PROGRESS | End-to-end proof that DENY never calls ArcGIS |
| #8 | Structured audit | IN PROGRESS | Complete early-deny/block coverage + persistence/admin query |
| #9 | Rate controls | IN PROGRESS | Redis integration tests + operation dimension/sustained controls |
| #10 | JTUWMA reference integration | BLOCKED EXTERNAL | JTU staging/EarthID/Access API/ArcGIS environment |
| #11 | Enterprise 11.5 OAuth provider | IN PROGRESS / EXTERNAL VALIDATION | Provider implementation + Enterprise 11.5 staging |
| #12 | Admin Console | NOT STARTED | Product UI/API/RBAC |
| #13 | Async telemetry/analytics | IN PROGRESS | ClickHouse sink, durable fallback, outage/load evidence |
| #14 | DDoS/overload resilience | IN PROGRESS | Request limits, concurrency/bulkheads, attack/load evidence |

## Release rule

Do not close Epic #2 until all product-side implementation is complete and #10/#11 external validation evidence is attached. Never convert an unavailable staging environment into a false PASS.
