import { expect, test } from '@playwright/test'

test('BROWSER-01 token CSRF chega por HTTPS mas seu cookie não fica acessível ao JavaScript', async ({ page, context }) => {
  await page.goto('/')
  const result = await page.evaluate(async () => {
    const response = await fetch('/api/v1/identity/csrf')
    return { status: response.status, token: typeof (await response.json()).token, cookie: document.cookie }
  })
  expect(result.status).toBe(200)
  expect(result.token).toBe('string')
  expect(result.cookie.includes('cc_csrf')).toBe(false)
  const cookie = (await context.cookies()).find(item => item.name === 'cc_csrf')
  expect(Boolean(cookie?.httpOnly && cookie?.secure && cookie?.sameSite === 'Strict')).toBe(true)
})

test('BROWSER-02 SameSite Strict impede envio da sessão em uma requisição entre sites', async ({ page, context, baseURL }) => {
  const { csrf, login, register } = await import('../support/http.js')
  const user = await register(context.request)
  const token = await csrf(context.request)
  expect((await login(context.request, user.email, token)).status()).toBe(200)
  expect((await context.request.get('/api/test/session-protected')).status()).toBe(200)
  // 127.0.0.1 and localhost are different sites even though this run's certificate covers both.
  await page.goto(baseURL!.replace('localhost', '127.0.0.1'))
  const response = page.waitForResponse(value => value.url().startsWith(baseURL + '/api/test/session-protected'))
  await page.evaluate(url => {
    const frame = document.createElement('iframe')
    frame.name = 'cross-site-target'
    document.body.append(frame)
    const form = document.createElement('form')
    form.method = 'GET'
    form.action = url
    form.target = frame.name
    document.body.append(form)
    form.submit()
  }, baseURL + '/api/test/session-protected')
  const denied = await response
  expect(denied.status()).toBe(401)
  expect((await denied.request().allHeaders()).cookie ?? '').not.toContain('cc_at=')
})
