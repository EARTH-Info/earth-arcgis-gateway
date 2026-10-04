# Security and Load Test Procedure

This procedure supplies the product-side harness for issues #13 and #14. Production PASS still requires evidence from the target deployment; the repository must not invent latency/capacity numbers.

## Automated traffic profiles

Run against an isolated staging gateway:

```bash
k6 run \
  -e GATEWAY_BASE_URL=https://gateway-staging.example.gov \
  -e EARTHID_TOKEN='<short-lived-test-token>' \
  -e ARCGIS_TEST_PATH='/arcgis/rest/services/Approved/Test/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json' \
  tests/load/gateway-k6.js
```

The default script exercises unauthenticated flood, invalid-JWT flood, valid-user burst and random/unapproved resource traffic concurrently.

Run the exported functions `oversizedPost`, `spoofedForwardedHeaders` and `adminIsolation` as focused scenarios when validating edge/WAF behavior.

## Required dependency-failure runs

Repeat the same representative valid-user workload while deliberately applying one failure at a time:

1. Access API unavailable/slow: requests must fail closed and ArcGIS must not be called.
2. Redis unavailable: distributed enforcement must fail closed; readiness must report degraded.
3. ClickHouse unavailable: authorized ArcGIS traffic must continue; telemetry must spool locally.
4. ArcGIS slow/unavailable: concurrency must remain bounded and responses converge to controlled 502/503/504 behavior.
5. Token endpoint unavailable: no retry storm; one request must not fan out indefinitely.
6. Forced ArcGIS 498/499: exactly one credential refresh/retry per client request.

## Evidence to capture

For every run record:

- exact gateway commit/image digest;
- environment and ArcGIS Enterprise version;
- request profile and duration;
- P50/P95/P99 latency;
- throughput and rejection counts by status;
- gateway CPU and memory;
- open connections;
- telemetry accepted/dropped/spooled counts;
- Redis health;
- ArcGIS upstream request count;
- Access API request count;
- gateway upstream-concurrency peak;
- recovery behavior after traffic stops.

## Release rule

A script existing in the repository is not performance evidence. Attach the measured staging result to the release/PR before production. Do not claim the gateway adds negligible overhead until the direct-ArcGIS baseline and equivalent gateway request profile have been measured.
