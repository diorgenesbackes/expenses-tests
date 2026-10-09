import { defineConfig } from '@playwright/test'

const baseURL = process.env.LAB_BASE_URL
if (!baseURL || new URL(baseURL).hostname !== 'localhost' || !process.env.LAB_RUN_ID) {
  throw new Error('Execute through scripts/lab.py; an owned local laboratory is required.')
}

export default defineConfig({
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 30_000,
  reporter: [['list'], ['json', { outputFile: `reports/${process.env.LAB_RUN_ID}/playwright.json` }]],
  use: { baseURL, ignoreHTTPSErrors: false, trace: 'off', screenshot: 'off', video: 'off' },
  projects: [
    { name: 'api', testDir: './api' },
    // Trust only this run's certificate public key in the disposable Chromium process.
    // Node HTTP uses the generated CA; no OS/browser-wide trust is modified.
    { name: 'chromium', testDir: './e2e', use: { browserName: 'chromium', launchOptions: {
      args: [`--ignore-certificate-errors-spki-list=${process.env.LAB_TLS_SPKI}`],
    } } },
  ],
})
