import http from 'k6/http'
import { check, fail, sleep } from 'k6'
import { Trend, Counter, Rate } from 'k6/metrics'
import exec from 'k6/execution'

const base = __ENV.LAB_BASE_URL
if (!/^https:\/\/localhost:\d+$/.test(base || '') || !/^[a-f0-9]{12}$/.test(__ENV.LAB_RUN_ID || '')) {
  throw new Error('Only an owned local laboratory can receive load.')
}
const profile = __ENV.LAB_PROFILE || 'smoke'
const password = 'LocalTest123!'
const elapsed = Object.fromEntries(['register', 'login', 'current', 'refresh', 'logout'].map(name => [name, new Trend(`operation_${name}_ms`, true)]))
const samples = Object.fromEntries(Object.keys(elapsed).map(name => [name, new Counter(`samples_${name}`)]))
const errors = new Rate('unexpected_errors')
const thresholds = { checks: ['rate==1'], unexpected_errors: ['rate<0.001'], http_req_failed: ['rate<0.001'] }
for (const operation of Object.keys(elapsed)) {
  if (['smoke', 'baseline'].includes(profile) || operation === 'current') thresholds[`operation_${operation}_ms`] = ['p(95)<150']
  if (profile === 'baseline') thresholds[`samples_${operation}`] = ['count>=1000']
}
if (profile === 'load') thresholds.dropped_iterations = ['count==0']
const scenarios = {
  smoke: { executor: 'constant-vus', vus: 1, duration: '2m', exec: 'cycle' },
  baseline: { executor: 'shared-iterations', vus: 1, iterations: 1000, maxDuration: '30m', exec: 'cycle' },
  load: { executor: 'ramping-arrival-rate', startRate: 1, timeUnit: '1s', preAllocatedVUs: 25, maxVUs: 50,
    stages: [{ target: 1, duration: '5m' }, { target: 5, duration: '5m' }, { target: 10, duration: '5m' }], exec: 'query' },
  journey: { executor: 'ramping-vus', startVUs: 1, stages: [{ target: 10, duration: '2m' }, { target: 25, duration: '3m' }, { target: 50, duration: '5m' }], exec: 'journey' },
  spike: { executor: 'ramping-vus', startVUs: 10, stages: [{ target: 10, duration: '2m' }, { target: 100, duration: '30s' }, { target: 100, duration: '1m' }, { target: 10, duration: '2m' }], exec: 'journey' },
  soak: { executor: 'constant-vus', vus: 10, duration: '1h', exec: 'journey' },
}
export const options = { scenarios: { measured: scenarios[profile] }, thresholds, noCookiesReset: true,
  setupTimeout: '10m', hosts: __ENV.LAB_PROXY_IP ? { localhost: __ENV.LAB_PROXY_IP } : {}, summaryTrendStats: ['count', 'min', 'med', 'p(95)', 'p(99)', 'max'],
  systemTags: ['status', 'method', 'name', 'scenario', 'expected_response'], insecureSkipTLSVerify: false }
let token
let authenticated = false
function request(method, path, data, operation, measured = true) {
  const started = Date.now()
  const response = http.request(method, base + path, data ? JSON.stringify(data) : null, {
    headers: { 'Content-Type': 'application/json', ...(token ? { 'X-CSRF-Token': token } : {}) },
    tags: { name: operation, phase: measured ? 'measured' : 'setup' }, timeout: '6s', redirects: 0,
  })
  if (measured) { elapsed[operation].add(Date.now() - started); samples[operation].add(1) }
  const expected = operation === 'register' ? 201 : operation === 'logout' ? 204 : 200
  const okay = response.status === expected
  if (measured) { errors.add(!okay); check(response, { [`${operation} status`]: () => okay }) }
  if (!okay) fail(`${operation} unexpected status ${response.status}; response bodies suppressed`)
  return response
}
function getCsrf() { token = request('GET', '/api/v1/identity/csrf', null, 'csrf', false).json('token') }
export function setup() {
  const users = []
  for (let i = 0; i < 1000; i++) {
    const email = `perf-${__ENV.LAB_RUN_ID}-${i}@example.test`
    request('POST', '/api/v1/identity/users', { email, password }, 'register', false)
    users.push(email)
  }
  // Prime each operation outside the measured window. Cold startup remains separately identifiable.
  getCsrf()
  request('POST', '/api/v1/identity/sessions', { email: users[999], password }, 'login', false)
  request('GET', '/api/v1/identity/session/current', null, 'current', false)
  http.cookieJar().delete(base, 'cc_at')
  request('GET', '/api/v1/identity/session/current', null, 'refresh', false)
  request('DELETE', '/api/v1/identity/sessions/current', null, 'logout', false)
  return { users }
}
export function cycle(data) {
  getCsrf()
  const email = `measured-${__ENV.LAB_RUN_ID}-${exec.scenario.iterationInTest}@example.test`
  request('POST', '/api/v1/identity/users', { email, password }, 'register')
  request('POST', '/api/v1/identity/sessions', { email: data.users[__VU - 1], password }, 'login')
  request('GET', '/api/v1/identity/session/current', null, 'current')
  http.cookieJar().delete(base, 'cc_at')
  const refreshed = request('GET', '/api/v1/identity/session/current', null, 'refresh')
  check(refreshed, { 'refresh rotates access': response => response.json('session.refreshed') === true })
  request('DELETE', '/api/v1/identity/sessions/current', null, 'logout')
  if (profile === 'smoke') sleep(1)
}
export function query(data) {
  if (!authenticated) {
    getCsrf()
    request('POST', '/api/v1/identity/sessions', { email: data.users[__VU - 1], password }, 'login', false)
    authenticated = true
  }
  // This includes automatic real-time access expiry/refresh during the 1-hour profile.
  request('GET', '/api/v1/identity/session/current', null, 'current')
}
export function journey(data) { query(data); sleep(1 + Math.random() * 2) }
