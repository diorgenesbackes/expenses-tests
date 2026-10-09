import { randomUUID } from 'node:crypto'
import { expect, type APIRequestContext } from '@playwright/test'

export const password = 'LocalTest123!'
export const newEmail = () => `lab-${randomUUID()}@example.test`
export async function register(client: APIRequestContext, email = newEmail(), key = randomUUID()) {
  const response = await client.post('/api/v1/identity/users', { data: { email, password }, headers: { 'Idempotency-Key': key } })
  expect(response.status()).toBe(201)
  return { email, key, body: await response.json() }
}
export async function csrf(client: APIRequestContext) {
  const response = await client.get('/api/v1/identity/csrf')
  expect(response.status()).toBe(200)
  return (await response.json()).token as string
}
export async function login(client: APIRequestContext, email: string, token: string, instance = '1') {
  return client.post('/api/v1/identity/sessions', { data: { email, password }, headers: { 'X-CSRF-Token': token, 'X-Lab-Target': instance } })
}
