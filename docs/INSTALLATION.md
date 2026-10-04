# EI ArcGIS Gateway Installation Guide

## 1. Deployment topology

Production traffic should be:

```
Internet / agency LAN
        |
Reverse proxy / WAF / load balancer (TLS)
        |
EI ArcGIS Gateway node(s)
        |---- Redis (required for multi-node enforcement)
        |---- ClickHouse (required by the supplied Production configuration for durable audit analytics)
        |
EarthID / application Access API / ArcGIS Enterprise
```

Do not expose the gateway container port directly to the Internet. The supplied Compose file binds port 8080 to loopback so an agency-approved reverse proxy can terminate TLS and apply connection/request controls.

## 2. Prerequisites

- Docker Engine with Compose v2, or an equivalent container platform.
- DNS/TLS certificates for the public gateway hostname.
- Reachability from the gateway to EarthID discovery/JWKS, each application Access API, ArcGIS Portal/Server, Redis and ClickHouse where configured.
- A least-privilege ArcGIS service identity for Enterprise 11.3, or approved OAuth application credentials for validated 11.5 deployments.
- ClickHouse table created from `deploy/clickhouse-schema.sql` when telemetry persistence is enabled.

## 3. Configure secrets

From `deploy/`:

```bash
cp .env.example .env
chmod 600 .env
```

Populate real values in `.env`. Never commit this file.

For Enterprise 11.3 use:

- `ARCGIS_CREDENTIAL_PROVIDER=federated-11.3`
- Portal generateToken endpoint
- exact registered federated `serverUrl`
- restricted gateway username/password

For validated Enterprise 11.5 OAuth use:

- `ARCGIS_CREDENTIAL_PROVIDER=oauth-11.5`
- OAuth token endpoint
- client ID and client secret

## 4. Create telemetry table

Create the required ClickHouse telemetry table before starting Production:

```bash
clickhouse-client --multiquery < clickhouse-schema.sql
```

Use an agency-approved database/user and grant only INSERT to the gateway telemetry account. The Admin Console currently reads its live window from the gateway's bounded recent-event buffer; analytics persistence is independent of the request path.

## 5. Build and start

```bash
docker compose --env-file .env build --pull
docker compose --env-file .env up -d
```

The application performs startup validation. Invalid HTTPS endpoints, missing application registrations, empty resource allowlists, missing Redis/ClickHouse in Production, invalid protection limits/trusted proxies, or missing credentials for the selected ArcGIS provider cause startup failure instead of silently weakening security.

## 6. Verify health

From the gateway host:

```bash
curl -fsS http://127.0.0.1:8080/health/live
curl -fsS http://127.0.0.1:8080/health/ready
```

`/health/live` only proves the process is alive. `/health/ready` also reports Redis and telemetry persistence state. A configured but disconnected Redis returns HTTP 503.

## 7. Reverse proxy requirements

The reverse proxy/WAF must:

- terminate TLS using approved protocols/ciphers;
- enforce connection and request-rate ceilings;
- impose header/body/slow-client limits;
- preserve the original request path without decoding/re-encoding it unexpectedly;
- restrict `/admin` by network policy in addition to EarthID RBAC where possible;
- never expose ArcGIS service credentials;
- pass health probes only from trusted monitoring networks.

The gateway does not currently trust arbitrary `X-Forwarded-For` headers, avoiding client-IP spoofing unless a deployment explicitly adds trusted-proxy handling.

## 8. Validate before pilot

Do not call a deployment production-ready until these gates are recorded:

1. EarthID valid/expired/wrong issuer/wrong audience/wrong signature/missing-sub tests.
2. Application identity cannot be overridden by browser parameters.
3. Access API ALLOW/DENY/timeout/error/malformed tests.
4. ArcGIS 498/499/401 retry exactly once with POST replay.
5. Resource/path security corpus.
6. Redis rate and temporary-block integration tests.
7. ClickHouse outage test proving ArcGIS response path remains independent.
8. JTUWMA user × service/layer authorization matrix.
9. Browser trace proving no ArcGIS token and no direct protected-service bypass.
10. Defined load/security test profile with P50/P95/P99 and upstream request counts.

## 9. Upgrade

Build a new immutable image, run the automated suite, then deploy through the reverse proxy/load balancer using rolling replacement. Do not mutate binaries inside a running container. Keep the prior image digest available for rollback.
