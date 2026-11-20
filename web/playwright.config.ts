import { defineConfig, devices } from '@playwright/test';

// The browser tests run against a backend that tools/web-e2e.ps1 has started on a scratch store. They are
// never pointed at the development instance: the script supplies the address and the accounts of the run.
export default defineConfig({
  testDir: 'e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  outputDir: 'test-results',
  use: {
    baseURL: process.env['URLOG_E2E_URL'] ?? 'http://127.0.0.1:5081',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
