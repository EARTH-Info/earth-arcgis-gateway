#!/usr/bin/env bash
set -euo pipefail

: "${GATEWAY_BASE_URL:?GATEWAY_BASE_URL is required}"
: "${EARTHID_TOKEN:?EARTHID_TOKEN is required}"

ARCGIS_POST_PATH="${ARCGIS_POST_PATH:-/arcgis/rest/services/Land/Parcels/FeatureServer/0/query}"
BODY_BYTES="${SLOW_BODY_BYTES:-262144}"
RATE="${SLOW_BODY_RATE:-1k}"
TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

head -c "$BODY_BYTES" /dev/zero | tr '\0' 'x' > "$TMP"

status="$(curl \
  --silent \
  --show-error \
  --output /dev/null \
  --write-out '%{http_code}' \
  --limit-rate "$RATE" \
  --max-time "${SLOW_BODY_MAX_SECONDS:-60}" \
  -X POST \
  -H "Authorization: Bearer ${EARTHID_TOKEN}" \
  -H 'Content-Type: application/octet-stream' \
  --data-binary "@$TMP" \
  "${GATEWAY_BASE_URL}${ARCGIS_POST_PATH}" || true)"

case "$status" in
  408|413|429|504) exit 0 ;;
  *)
    echo "Unexpected slow-body status: $status" >&2
    exit 1
    ;;
esac
