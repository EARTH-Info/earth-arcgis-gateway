# EARTH ArcGIS Gateway modernization

This branch modernizes the archived Esri resource-proxy pattern for EARTH Info's application architecture.

The original Esri project remains available in repository history under the Apache-2.0 license. New implementation work lives under `src/Earth.ArcGIS.Gateway`.

## Purpose

EarthID users are application users and do not require ArcGIS Portal accounts. The gateway uses a restricted ArcGIS service identity upstream while retaining the EarthID identity for authorization, rate limiting and audit.

## Run

Configure EarthID authority/audience and ArcGIS endpoints. Supply the ArcGIS service account only through environment variables or a secret store:

- `Gateway__Username`
- `Gateway__Password`

Do not commit credentials.

Then run:

`dotnet run --project src/Earth.ArcGIS.Gateway/Earth.ArcGIS.Gateway.csproj`

## Status

Initial secure V1 foundation. Do not deploy to production until integration/security tests and the deployment-specific allowlist are complete.
