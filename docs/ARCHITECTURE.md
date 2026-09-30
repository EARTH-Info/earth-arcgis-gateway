# EARTH ArcGIS Gateway

## Security boundary

EarthID authenticates application users. ArcGIS Enterprise authenticates only the gateway service identity. ArcGIS credentials and tokens must never be returned to the browser.

Request path:

EarthID user -> web application -> EARTH ArcGIS Gateway -> Portal/federated ArcGIS Server.

## V1 controls

1. Validate EarthID JWT issuer, audience, signature and expiry.
2. Partition rate limiting by immutable EarthID `sub`.
3. Allow only configured ArcGIS REST path prefixes.
4. Reject known write/admin-style operations at the gateway.
5. Acquire and cache short-lived ArcGIS tokens server-side.
6. Emit structured audit events linked to EarthID `sub` and correlation ID.
7. Never log JWTs, ArcGIS tokens, passwords or secrets.

## Important

The allowlist is the primary gateway authorization boundary. ArcGIS permissions on the service account are a second boundary. Both must be restrictive.

V1 intentionally keeps AI out of enforcement. Deterministic controls perform allow/deny/throttle decisions. Telemetry can later feed anomaly detection and AI-assisted profiling.

## Next hardening

- Redis/distributed rate limits for multi-instance deployment.
- Per-app/service/layer policy and weighted request costs.
- Response byte and feature-count telemetry.
- Query fingerprinting without retaining sensitive query values.
- Temporary blocklist with expiry and administrative reason.
- OAuth upstream provider alongside legacy service-account token provider.
- Integration tests against ArcGIS Enterprise 11.3.
- OpenTelemetry/SIEM export.
