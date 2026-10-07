# EARTH ArcGIS Gateway

## Security boundary

EarthID authenticates application users. ArcGIS Enterprise authenticates only the gateway service identity. ArcGIS credentials and tokens must never be returned to the browser.

Request path:

EarthID user -> web application -> EARTH ArcGIS Gateway -> Portal/federated ArcGIS Server.

## V1 controls

1. Validate EarthID JWT issuer, audience, signature and expiry.
2. Resolve the trusted application from validated token audience/client context.
3. Canonicalize the ArcGIS service/layer/operation and enforce configured path prefixes as an upper security bound.
4. Enforce an explicit read-operation allowlist; unknown/write/admin operations fail closed.
5. Apply distributed weighted rate protection by immutable EarthID `sub` + tenant + app + service + layer + operation before calling the application Access API.
6. Ask the application Access API for the effective per-user resource decision and fail closed on timeout/error/malformed responses.
7. Acquire and cache short-lived ArcGIS credentials server-side, retrying an ArcGIS authentication failure at most once.
8. Stream normal ArcGIS responses and emit bounded asynchronous structured telemetry linked to EarthID `sub` and correlation ID.
9. Never log JWTs, ArcGIS tokens, passwords or secrets.

## Important

The allowlist is the primary gateway authorization boundary. ArcGIS permissions on the service account are a second boundary. Both must be restrictive.

V1 intentionally keeps AI out of enforcement. Deterministic controls perform allow/deny/throttle decisions. Telemetry can later feed anomaly detection and AI-assisted profiling.

## Implemented product-side hardening

- Redis-backed multi-window distributed rate limits and temporary user blocks.
- Per-user/app/service/layer/operation authorization and weighted GIS request costs.
- Bounded request buffering, streamed upstream responses and safe header forwarding.
- ClickHouse telemetry with bounded asynchronous queue and durable local spool fallback.
- EarthID-protected Admin Console with security events, live traffic, health and block/unblock controls.
- Replaceable Enterprise 11.3 federated and Enterprise 11.5 OAuth credential providers.
- Containerized non-root Production deployment with fail-fast Redis/ClickHouse requirements.

## External release validation still required

- JTUWMA ArcGIS Enterprise 11.3 end-to-end browser/access matrix.
- ArcGIS Enterprise 11.5 OAuth compatibility run.
- Measured performance and attack/load evidence from the target staging deployment.
- Optional future SIEM/OpenTelemetry export and AI-assisted anomaly explanation do not replace deterministic enforcement.
