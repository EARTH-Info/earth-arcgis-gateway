# ArcGIS Enterprise compatibility

## Design rule

EarthID is the application identity. ArcGIS authentication is an upstream implementation detail behind the gateway.

## Enterprise 11.3

Use a restricted Portal service account. The gateway obtains a Portal token, exchanges it for a federated-server token using the Portal generateToken endpoint and the exact registered serverUrl, and uses the returned expiry as the source of truth.

On an ArcGIS authentication failure (498/499, including an ArcGIS JSON error returned with HTTP 200, or HTTP 401), invalidate the cached federated token, exchange a fresh token and retry exactly once.

## Enterprise 11.4 and 11.5+

Prefer OAuth 2.0 app authentication when deployment testing confirms the required secured items/services are supported. Enterprise 11.4+ supports app authentication with privileges and specific item access. Enterprise 11.5 adds portal-service privileges for developer credentials.

The gateway must keep its EarthID authorization, rate limiting, audit, service/layer allowlists and abuse controls regardless of ArcGIS credential type.

## Migration requirement

Do not couple request forwarding to username/password or generateToken. Upstream ArcGIS authentication must remain replaceable so a deployment can move from the 11.3 federated service-account provider to OAuth app credentials without changing gateway routes or clients.

## Acceptance tests

- Portal token expiry refreshes before expiry.
- Federated server token expiry refreshes before expiry.
- First 498/499 causes one refresh/retry and succeeds when credentials remain valid.
- Repeated auth failure is returned after one retry; no infinite loop.
- POST query body survives the retry unchanged.
- ArcGIS token never appears in client response, URL, audit log or exception log.
- Exact registered federated serverUrl is used for token exchange.
- EarthID sub remains the rate-limit/audit identity through token refresh.
- OAuth provider migration must preserve the same gateway contract for 11.5+.
