# EarthID effective ArcGIS access contract

## Stable application contract

The browser authenticates with EarthID. It never receives an ArcGIS credential.

Flow:

EarthID user -> JTUWMA / GeoForest web app -> EARTH ArcGIS Gateway -> application Access API -> ArcGIS Enterprise.

The gateway extracts the validated immutable EarthID subject (sub), identifies the calling application and requested ArcGIS resource/operation, and asks the application's Access API for the effective decision.

## Authorization request

The gateway sends a server-to-server request containing at minimum:

- earthIdSub
- applicationId
- ArcGIS service identifier
- layerId when applicable
- ArcGIS operation
- HTTP method
- correlationId

The gateway does not trust a browser-supplied EarthID subject.

## Authorization response

The Access API returns:

- allowed: true/false
- reasonCode
- policyVersion
- optional rateLimitProfile
- optional cacheTtlSeconds

Default behavior is fail closed. Timeout, malformed response, unavailable Access API, unknown application, unknown service/layer or unknown operation means deny unless an explicitly approved deployment policy says otherwise.

## Gateway sequence

1. Validate EarthID JWT.
2. Resolve EarthID sub from the validated token.
3. Canonicalize and validate the ArcGIS path.
4. Resolve application + service + layer + operation from gateway-owned configuration.
5. Ask the application's Access API for effective access.
6. If denied: do not call ArcGIS. Log DENY with reason and policy version.
7. Apply EarthID/user/app/resource rate policy.
8. Obtain the current upstream ArcGIS credential.
9. Forward the request.
10. Log ALLOW/THROTTLE/upstream failure with EarthID sub and correlation ID.

## Audit

Every decision records the immutable EarthID sub, application, service, layer, operation, decision, reason, policy version, rate-limit state, timestamp, latency, response status and correlation ID.

Never log EarthID JWTs, ArcGIS access tokens, client secrets or passwords.

## ArcGIS version isolation

EarthID authorization is independent from ArcGIS Enterprise authentication.

- Enterprise 11.3: restricted Portal service identity + Portal-to-federated-server token exchange.
- Enterprise 11.4/11.5+: OAuth app credentials can replace the upstream provider where supported.
- JTUWMA and GeoForest clients keep the same gateway URL and EarthID session model across the upgrade.

The ArcGIS credential authorizes only the gateway's maximum possible ArcGIS scope. The application Access API further reduces that scope per EarthID user. The gateway must never allow an Access API decision to grant a resource that the gateway's own configured allowlist does not contain.
