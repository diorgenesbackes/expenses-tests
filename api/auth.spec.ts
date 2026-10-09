import { expect, test } from '@playwright/test'
import { csrf, login, newEmail, password, register } from '../support/http.js'

test('API-01 cadastro, replay e duplicidade preservam o resultado', async ({ request }) => {
  const user = await register(request)
  const replay = await request.post('/api/v1/identity/users', { data: { email: user.email.toUpperCase(), password: 'Changed123!' }, headers: { 'Idempotency-Key': user.key } })
  expect(replay.status()).toBe(201)
  expect(await replay.json()).toEqual(user.body)
  const duplicate = await request.post('/api/v1/identity/users', { data: { email: user.email, password } })
  expect(duplicate.status()).toBe(409)
  expect((await login(request, user.email, await csrf(request))).status()).toBe(200)
})

test('API-02 cookies, renovação e revogação imediata entre duas instâncias', async ({ request, playwright, baseURL }) => {
  const { email } = await register(request)
  const token = await csrf(request)
  const signedIn = await login(request, email, token)
  expect(signedIn.status()).toBe(200)
  const initialInstance = signedIn.headers()['x-lab-instance']
  const state = await request.storageState()
  const authCookies = state.cookies.filter(cookie => cookie.name.startsWith('cc_'))
  expect(authCookies.length).toBeGreaterThanOrEqual(3)
  expect(authCookies.every(cookie => cookie.httpOnly && cookie.secure && cookie.sameSite === 'Strict')).toBe(true)
  const other = await playwright.request.newContext({ baseURL, storageState: state })
  try {
    const current = await other.get('/api/v1/identity/session/current', { headers: { 'X-CSRF-Token': token, 'X-Lab-Target': '2' } })
    expect(current.status()).toBe(200)
    expect(current.headers()['x-lab-instance']).not.toBe(initialInstance)
    const refreshState = { ...state, cookies: state.cookies.filter(cookie => cookie.name !== 'cc_at') }
    const renew = await playwright.request.newContext({ baseURL, storageState: refreshState })
    try {
      const refreshed = await renew.get('/api/v1/identity/session/current', { headers: { 'X-CSRF-Token': token, 'X-Lab-Target': '2' } })
      expect(refreshed.status()).toBe(200)
      expect((await refreshed.json()).session.refreshed).toBe(true)
      const logout = await renew.delete('/api/v1/identity/sessions/current', { headers: { 'X-CSRF-Token': token, 'X-Lab-Target': '1' } })
      expect(logout.status()).toBe(204)
      const copied = await other.get('/api/test/session-protected', { headers: { 'X-Lab-Target': '2' } })
      expect(copied.status()).toBe(401)
    } finally { await renew.dispose() }
  } finally { await other.dispose() }
})

test('API-03 falhas por e-mail normalizado bloqueiam em ambas as instâncias', async ({ request }) => {
  const { email } = await register(request)
  const token = await csrf(request)
  for (let attempt = 1; attempt <= 10; attempt++) {
    const response = await request.post('/api/v1/identity/sessions', { data: { email: attempt % 2 ? email : email.toUpperCase(), password: 'Incorrect123!' },
      headers: { 'X-CSRF-Token': token, 'X-Lab-Target': attempt % 2 ? '1' : '2' } })
    expect(response.status()).toBe(attempt < 10 ? 401 : 423)
    if (attempt === 10) expect(Number(response.headers()['retry-after'])).toBeGreaterThan(0)
  }
  expect((await login(request, email, token)).status()).toBe(423)
  const different = await register(request)
  expect((await login(request, different.email, token, '2')).status()).toBe(200)
})

test('API-04 CSRF, origem, corpo e diagnósticos são protegidos no host publicado', async ({ request }) => {
  expect((await request.post('/api/v1/identity/sessions', { data: { email: newEmail(), password } })).status()).toBe(403)
  const token = await csrf(request)
  expect((await request.post('/api/v1/identity/sessions', { data: { email: newEmail(), password }, headers: { 'X-CSRF-Token': token, Origin: 'https://attacker.invalid' } })).status()).toBe(403)
  expect((await request.post('/api/v1/identity/users', { data: { email: newEmail(), password: 'x'.repeat(9000) } })).status()).toBe(413)
  expect((await request.get('/api/v1/identity/users')).status()).toBe(404)
  expect((await request.get('/api/test/session-protected')).status()).toBe(401)
})

test('API-05 provedor indisponível não autentica nem consome tentativas', async ({ request, playwright }) => {
  const { email } = await register(request)
  const token = await csrf(request)
  const control = await playwright.request.newContext({ baseURL: `http://127.0.0.1:${process.env.LAB_IDENTITY_PORT}`, extraHTTPHeaders: { 'X-Lab-Key': process.env.LAB_CONTROL_KEY! } })
  try {
    expect((await control.post('/control', { data: { email, value: 'unavailable' } })).status()).toBe(204)
    for (let i = 0; i < 11; i++) expect((await login(request, email, token)).status()).toBe(503)
    expect((await request.get('/api/test/session-protected')).status()).toBe(401)
    await control.post('/control', { data: { email, value: 'clear' } })
    expect((await login(request, email, token)).status()).toBe(200)
  } finally { await control.dispose() }
})

test('API-06 cadastros concorrentes com a mesma chave convergem para um resultado', async ({ request }) => {
  const { randomUUID } = await import('node:crypto')
  const email = newEmail()
  const key = randomUUID()
  const results = await Promise.all(Array.from({ length: 8 }, () => request.post('/api/v1/identity/users', {
    data: { email, password }, headers: { 'Idempotency-Key': key },
  })))
  expect(results.every(response => [201, 409].includes(response.status()))).toBe(true)
  expect(results.some(response => response.status() === 201)).toBe(true)
  const created = await Promise.all(results.filter(response => response.status() === 201).map(response => response.json()))
  expect(new Set(created.map(body => JSON.stringify(body))).size).toBe(1)
  const replay = await register(request, email, key)
  expect(replay.body).toEqual(created[0])
})

test('API-07 access token adulterado não autoriza acesso', async ({ request, playwright, baseURL }) => {
  const { email } = await register(request)
  expect((await login(request, email, await csrf(request))).status()).toBe(200)
  const state = await request.storageState()
  const access = state.cookies.find(cookie => cookie.name === 'cc_at')!
  const parts = access.value.split('.')
  parts[1] = Buffer.from(JSON.stringify({ sub: 'attacker', token_use: 'access', client_id: 'expenses-local-tests' })).toString('base64url')
  access.value = parts.join('.')
  const tampered = await playwright.request.newContext({ baseURL, storageState: state })
  try { expect((await tampered.get('/api/test/session-protected')).status()).toBe(401) }
  finally { await tampered.dispose() }
})

test('API-08 banco indisponível falha fechado e recupera após retorno', async ({ request }) => {
  const { execFileSync } = await import('node:child_process')
  const container = process.env.LAB_DATABASE_CONTAINER!
  const owner = execFileSync('docker', ['inspect', '--format', '{{index .Config.Labels "expenses.tests.run"}}', container], { encoding: 'utf8' }).trim()
  expect(owner).toBe(process.env.LAB_RUN_ID)
  const { email } = await register(request)
  const token = await csrf(request)
  execFileSync('docker', ['pause', container])
  try {
    const failed = await login(request, email, token)
    expect(failed.status()).toBe(503)
  } finally { execFileSync('docker', ['unpause', container]) }
  await expect.poll(async () => (await request.get('/api/health/db')).status(), { timeout: 10_000 }).toBe(200)
  expect((await login(request, email, token)).status()).toBe(200)
})

test('API-09 entradas inválidas não interrompem o proxy do laboratório', async ({ request }) => {
  expect((await request.get('/assets/')).status()).toBe(404)
  expect((await request.get('/%ZZ')).status()).toBe(400)
  expect((await request.get('/api/health/db')).status()).toBe(200)
})
