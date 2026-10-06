# Security and Load Test Procedure

This procedure supplies the product-side harness for the Tier-1 SEC-004 / issue #22 scenarios. A runnable harness is not production evidence: pilot/production PASS still requires measured results from the target deployment.

## Run a scenario

Run against an isolated staging gateway:

```bash
k6 run \
  -e SCENARIO=valid_user_burst \
  -e GATEWAY_BASE_URL=https://gateway-staging.example.gov \
  -e EARTHID_TOKEN='<short-lived-test-token>' \
  -e ARCGIS_TEST_PATH='/arcgis/rest/services/Approved/Test/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json' \
  tests/load/gateway-k6.js
```

`SCENARIO=core` runs the four baseline profiles concurrently. Focused Tier-1 scenarios should be run individually so their dependency conditions and evidence are unambiguous.

The k6 script makes expected-status checks release-significant: `checks` must remain 100% and `gateway_unexpected_status` must remain zero. Expected security responses such as 401/403 are therefore evaluated by scenario semantics rather than a generic HTTP-failure threshold.

## Required scenarios

The harness exposes these scenario names:

1. `unauthenticated_flood`
2. `invalid_jwt_flood`
3. `valid_user_burst`
4. `many_valid_users`
5. `expensive_spatial_burst`
6. `oversized_post`
7. `slow_client_body`
8. `random_paths`
9. `retry_498`
10. `access_api_degradation`
11. `redis_degradation`
12. `clickhouse_outage`
13. `arcgis_degradation`
14. `admin_isolation`
15. `spoofed_forwarded_headers`

`many_valid_users` requires multiple comma-separated EarthID test tokens through `EARTHID_TOKENS` so one identity is not incorrectly used to simulate many users.

## Slow-body probe

k6 does not provide the throttled streaming-upload control required for the actual slow-client-body test. Run the companion probe:

```bash
GATEWAY_BASE_URL=https://gateway-staging.example.gov \
EARTHID_TOKEN='<short-lived-test-token>' \
bash tests/load/slow-body.sh
```

After that probe is captured as evidence, the k6 `slow_client_body` marker may be run with `SLOW_BODY_EXTERNAL_VERIFIED=1`. Do not mark the scenario PASS from the marker alone.

## Dependency/fault injection

The following scenarios require the staging operator to deliberately place the named dependency into the intended state before/during the run:

- `access_api_degradation`: make the application Access API unavailable or slow. Requests must fail closed and ArcGIS must not be called.
- `redis_degradation`: stop/disconnect Redis. Distributed enforcement must fail closed and readiness must become unready/degraded as documented.
- `clickhouse_outage`: make ClickHouse unavailable. Authorized ArcGIS traffic must continue while telemetry spools locally; queue/spool drops are not acceptable.
- `arcgis_degradation`: make ArcGIS slow/unavailable. Concurrency must remain bounded and responses must converge to controlled 502/503/504 behavior.
- `retry_498`: configure the staging ArcGIS/fake upstream to force one 498/499 auth failure. Exactly one credential refresh/retry is permitted for the client request.

Also perform the token-endpoint-unavailable test even though it uses the same representative client traffic rather than its own k6 request shape. It must prove there is no retry storm.

## Evidence to capture

For every run record:

- exact gateway commit and image digest;
- environment and ArcGIS Enterprise version;
- scenario name, parameters and duration;
- P50/P95/P99 latency;
- throughput and rejection counts by status/reason;
- gateway CPU and memory;
- open connections;
- telemetry accepted/dropped/spooled counts;
- Redis health/state;
- ArcGIS upstream request count;
- Access API request count;
- gateway upstream-concurrency peak;
- per-user concurrency peak for applicable scenarios;
- recovery behavior after the fault/traffic stops.

For 498/499 and dependency-failure runs, capture upstream/access-service request counts so retry amplification can be independently verified.

## Release rule

A script existing in the repository is not performance evidence. Attach measured staging results to the release/PR before pilot/production. Do not claim negligible gateway overhead until a direct-ArcGIS baseline and equivalent gateway request profile have been measured.
