import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const BASE = __ENV.GATEWAY_BASE_URL || 'https://gateway.example.gov';
const VALID_TOKEN = __ENV.EARTHID_TOKEN || '';
const VALID_TOKENS = (__ENV.EARTHID_TOKENS || VALID_TOKEN)
  .split(',')
  .map(x => x.trim())
  .filter(Boolean);
const INVALID_TOKEN = __ENV.INVALID_EARTHID_TOKEN || 'invalid.jwt.token';
const RESOURCE = __ENV.ARCGIS_TEST_PATH ||
  '/arcgis/rest/services/Land/Parcels/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json';
const SPATIAL_RESOURCE = __ENV.ARCGIS_SPATIAL_TEST_PATH ||
  '/arcgis/rest/services/Land/Parcels/FeatureServer/0/query?geometry=116%2C5&outFields=*&resultOffset=0&resultRecordCount=2000&f=json';
const SCENARIO = __ENV.SCENARIO || 'core';
const DURATION = __ENV.TEST_DURATION || '30s';

const rejected = new Counter('gateway_rejected');
const unexpected = new Counter('gateway_unexpected_status');
const gatewayLatency = new Trend('gateway_request_duration', true);

function arrival(exec, rate, startTime = '0s') {
  return {
    executor: 'constant-arrival-rate',
    rate,
    timeUnit: '1s',
    duration: DURATION,
    preAllocatedVUs: 10,
    maxVUs: 100,
    exec,
    startTime,
  };
}

function vus(exec, count, startTime = '0s') {
  return {
    executor: 'constant-vus',
    vus: count,
    duration: DURATION,
    exec,
    startTime,
  };
}

const scenarioCatalog = {
  unauthenticated_flood: arrival('unauthenticatedFlood', Number(__ENV.UNAUTH_RPS || 20)),
  invalid_jwt_flood: arrival('invalidJwtFlood', Number(__ENV.INVALID_RPS || 20)),
  valid_user_burst: vus('validUserBurst', Number(__ENV.VALID_VUS || 10)),
  many_valid_users: vus('manyValidUsers', Number(__ENV.MANY_USER_VUS || 20)),
  expensive_spatial_burst: vus('expensiveSpatialBurst', Number(__ENV.SPATIAL_VUS || 10)),
  oversized_post: vus('oversizedPost', Number(__ENV.OVERSIZED_VUS || 2)),
  slow_client_body: vus('slowClientBodyMarker', 1),
  random_paths: arrival('randomPathFlood', Number(__ENV.RANDOM_PATH_RPS || 10)),
  retry_498: vus('forcedAuthRetryProbe', Number(__ENV.RETRY_VUS || 2)),
  access_api_degradation: vus('accessApiDegradation', Number(__ENV.FAILURE_VUS || 5)),
  redis_degradation: vus('redisDegradation', Number(__ENV.FAILURE_VUS || 5)),
  clickhouse_outage: vus('clickHouseOutage', Number(__ENV.FAILURE_VUS || 5)),
  arcgis_degradation: vus('arcGisDegradation', Number(__ENV.FAILURE_VUS || 5)),
  admin_isolation: vus('adminIsolation', Number(__ENV.ADMIN_VUS || 5)),
  spoofed_forwarded_headers: vus('spoofedForwardedHeaders', Number(__ENV.SPOOF_VUS || 5)),
};

const coreScenarios = {
  unauthenticated_flood: scenarioCatalog.unauthenticated_flood,
  invalid_jwt_flood: { ...scenarioCatalog.invalid_jwt_flood, startTime: '2s' },
  valid_user_burst: { ...scenarioCatalog.valid_user_burst, startTime: '4s' },
  random_paths: { ...scenarioCatalog.random_paths, startTime: '6s' },
};

if (SCENARIO !== 'core' && !scenarioCatalog[SCENARIO]) {
  throw new Error(`Unknown SCENARIO '${SCENARIO}'. Supported: core, ${Object.keys(scenarioCatalog).join(', ')}`);
}

export const options = {
  discardResponseBodies: true,
  scenarios: SCENARIO === 'core'
    ? coreScenarios
    : { [SCENARIO]: scenarioCatalog[SCENARIO] },
  thresholds: {
    checks: ['rate==1'],
    gateway_unexpected_status: ['count==0'],
    dropped_iterations: ['count<100'],
    'gateway_request_duration{scenario:valid_user_burst}': ['p(95)<2000'],
  },
};

function authHeaders(token) {
  return {
    Authorization: `Bearer ${token}`,
    'X-Correlation-ID': `k6-${SCENARIO}-${__VU}-${__ITER}`,
  };
}

function observe(response, expected) {
  gatewayLatency.add(response.timings.duration);
  if (response.status >= 400) rejected.add(1);
  const ok = expected.includes(response.status);
  if (!ok) unexpected.add(1);
  check(response, {
    'status is expected class': () => ok,
  });
}

function requireValidToken() {
  const ok = VALID_TOKENS.length > 0;
  check(null, { 'valid EarthID token supplied': () => ok });
  return ok;
}

export function unauthenticatedFlood() {
  const r = http.get(BASE + RESOURCE, { redirects: 0 });
  observe(r, [401, 429]);
}

export function invalidJwtFlood() {
  const r = http.get(BASE + RESOURCE, {
    headers: authHeaders(INVALID_TOKEN),
    redirects: 0,
  });
  observe(r, [401, 429]);
}

export function validUserBurst() {
  if (!requireValidToken()) return;
  const r = http.get(BASE + RESOURCE, {
    headers: authHeaders(VALID_TOKENS[0]),
    redirects: 0,
  });
  observe(r, [200, 429]);
}

export function manyValidUsers() {
  const enoughUsers = VALID_TOKENS.length >= 2;
  check(null, { 'multiple EarthID user tokens supplied': () => enoughUsers });
  if (!enoughUsers) return;

  const token = VALID_TOKENS[(__VU - 1) % VALID_TOKENS.length];
  const r = http.get(BASE + RESOURCE, {
    headers: authHeaders(token),
    redirects: 0,
  });
  observe(r, [200, 429]);
}

export function expensiveSpatialBurst() {
  if (!requireValidToken()) return;
  const r = http.get(BASE + SPATIAL_RESOURCE, {
    headers: authHeaders(VALID_TOKENS[0]),
    redirects: 0,
  });
  observe(r, [200, 429]);
}

export function oversizedPost() {
  if (!requireValidToken()) return;
  const body = 'x'.repeat(Number(__ENV.OVERSIZED_BYTES || (2 * 1024 * 1024 + 1)));
  const r = http.post(BASE + RESOURCE.split('?')[0], body, {
    headers: {
      ...authHeaders(VALID_TOKENS[0]),
      'Content-Type': 'application/octet-stream',
    },
    redirects: 0,
  });
  observe(r, [413, 429]);
}

export function slowClientBodyMarker() {
  // k6 does not expose a throttled streaming upload API. The companion
  // tests/load/slow-body.sh performs the real slow-body transfer. This marker
  // prevents this scenario from being mistaken for an executed slow-body test.
  const configured = __ENV.SLOW_BODY_EXTERNAL_VERIFIED === '1';
  check(null, { 'external slow-body probe verified': () => configured });
  sleep(1);
}

export function randomPathFlood() {
  const token = VALID_TOKEN || INVALID_TOKEN;
  const randomPath =
    `/arcgis/rest/services/NotApproved/${__VU}/FeatureServer/0/query?f=json`;
  const r = http.get(BASE + randomPath, {
    headers: authHeaders(token),
    redirects: 0,
  });
  observe(r, VALID_TOKEN ? [403, 429] : [401, 429]);
}

export function forcedAuthRetryProbe() {
  if (!requireValidToken()) return;
  const r = http.get(BASE + RESOURCE, {
    headers: authHeaders(VALID_TOKENS[0]),
    redirects: 0,
  });
  observe(r, [200, 502]);
}

export function accessApiDegradation() {
  if (!requireValidToken()) return;
  const r = http.get(BASE + RESOURCE, {
    headers: authHeaders(VALID_TOKENS[0]),
    redirects: 0,
  });
  observe(r, [403, 503, 504]);
}

export function redisDegradation() {
  if (!requireValidToken()) return;
  const r = http.get(BASE + RESOURCE, {
    headers: authHeaders(VALID_TOKENS[0]),
    redirects: 0,
  });
  observe(r, [503]);
}

export function clickHouseOutage() {
  if (!requireValidToken()) return;
  const r = http.get(BASE + RESOURCE, {
    headers: authHeaders(VALID_TOKENS[0]),
    redirects: 0,
  });
  observe(r, [200, 429]);
}

export function arcGisDegradation() {
  if (!requireValidToken()) return;
  const r = http.get(BASE + RESOURCE, {
    headers: authHeaders(VALID_TOKENS[0]),
    redirects: 0,
  });
  observe(r, [502, 503, 504]);
}

export function spoofedForwardedHeaders() {
  const token = VALID_TOKEN || INVALID_TOKEN;
  const r = http.get(BASE + RESOURCE, {
    headers: {
      ...authHeaders(token),
      'X-Forwarded-For': '127.0.0.1',
      'X-Real-IP': '127.0.0.1',
    },
    redirects: 0,
  });
  observe(r, VALID_TOKEN ? [200, 429] : [401, 429]);
}

export function adminIsolation() {
  const token = VALID_TOKEN || INVALID_TOKEN;
  const r = http.get(BASE + '/admin/api/overview', {
    headers: authHeaders(token),
    redirects: 0,
  });
  observe(r, [401, 403]);
}
