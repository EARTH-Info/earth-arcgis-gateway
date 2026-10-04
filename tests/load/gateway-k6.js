import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const BASE = __ENV.GATEWAY_BASE_URL || 'https://gateway.example.gov';
const VALID_TOKEN = __ENV.EARTHID_TOKEN || '';
const INVALID_TOKEN = __ENV.INVALID_EARTHID_TOKEN || 'invalid.jwt.token';
const RESOURCE = __ENV.ARCGIS_TEST_PATH ||
  '/arcgis/rest/services/Land/Parcels/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json';

const rejected = new Counter('gateway_rejected');
const upstreamLatency = new Trend('gateway_request_duration', true);

export const options = {
  discardResponseBodies: true,
  scenarios: {
    unauthenticated_flood: {
      executor: 'constant-arrival-rate',
      rate: Number(__ENV.UNAUTH_RPS || 20),
      timeUnit: '1s',
      duration: __ENV.TEST_DURATION || '30s',
      preAllocatedVUs: 10,
      maxVUs: 50,
      exec: 'unauthenticatedFlood',
    },
    invalid_jwt_flood: {
      executor: 'constant-arrival-rate',
      rate: Number(__ENV.INVALID_RPS || 20),
      timeUnit: '1s',
      duration: __ENV.TEST_DURATION || '30s',
      preAllocatedVUs: 10,
      maxVUs: 50,
      exec: 'invalidJwtFlood',
    },
    valid_user_burst: {
      executor: 'constant-vus',
      vus: Number(__ENV.VALID_VUS || 10),
      duration: __ENV.TEST_DURATION || '30s',
      exec: 'validUserBurst',
      startTime: '2s',
    },
    random_paths: {
      executor: 'constant-arrival-rate',
      rate: Number(__ENV.RANDOM_PATH_RPS || 10),
      timeUnit: '1s',
      duration: __ENV.TEST_DURATION || '30s',
      preAllocatedVUs: 5,
      maxVUs: 20,
      exec: 'randomPathFlood',
      startTime: '4s',
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.50'],
    http_req_duration: ['p(95)<2000'],
    dropped_iterations: ['count<100'],
  },
};

function authHeaders(token) {
  return {
    Authorization: `Bearer ${token}`,
    'X-Correlation-ID': `k6-${__VU}-${__ITER}`,
  };
}

function observe(response, expected) {
  upstreamLatency.add(response.timings.duration);
  if (response.status >= 400) rejected.add(1);
  check(response, {
    'status is expected class': (r) => expected.includes(r.status),
  });
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
  if (!VALID_TOKEN) {
    sleep(1);
    return;
  }

  const r = http.get(BASE + RESOURCE, {
    headers: authHeaders(VALID_TOKEN),
    redirects: 0,
  });
  observe(r, [200, 403, 429, 502, 503, 504]);
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

export function oversizedPost() {
  if (!VALID_TOKEN) return;

  const body = 'x'.repeat(2 * 1024 * 1024 + 1);
  const r = http.post(BASE + RESOURCE.split('?')[0], body, {
    headers: {
      ...authHeaders(VALID_TOKEN),
      'Content-Type': 'application/octet-stream',
    },
    redirects: 0,
  });
  observe(r, [413, 429]);
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
  observe(r, VALID_TOKEN ? [200, 403, 429] : [401, 429]);
}

export function adminIsolation() {
  const token = VALID_TOKEN || INVALID_TOKEN;
  const r = http.get(BASE + '/admin/api/overview', {
    headers: authHeaders(token),
    redirects: 0,
  });
  observe(r, [200, 401, 403, 429]);
}
