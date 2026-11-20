import { expect, test as base, type Page } from '@playwright/test';

function need(name: string): string {
  const value = process.env[name];
  if (value === undefined || value === '') {
    throw new Error(`${name} is not set: run the browser tests through tools/web-e2e.ps1`);
  }
  return value;
}

export const accounts = {
  owner: () => ({ name: need('URLOG_E2E_OWNER'), password: need('URLOG_E2E_OWNER_PASSWORD') }),
  other: () => ({ name: need('URLOG_E2E_OTHER'), password: need('URLOG_E2E_OTHER_PASSWORD') }),
};

export async function signIn(page: Page, account: { name: string; password: string }): Promise<void> {
  await page.getByLabel('Name').fill(account.name);
  await page.getByLabel('Password').fill(account.password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

/**
 * Every test fails on a request to any host but the instance under test, and on an error in the console:
 * the interface loads nothing from elsewhere, fonts included.
 */
export const test = base.extend<{ strict: undefined }>({
  strict: [
    async ({ page, baseURL }, use) => {
      const origin = new URL(baseURL ?? '').origin;
      const foreign: string[] = [];
      const errors: string[] = [];
      page.on('request', (request) => {
        const url = request.url();
        if (!url.startsWith(origin) && !url.startsWith('data:') && !url.startsWith('blob:')) {
          foreign.push(url);
        }
      });
      page.on('pageerror', (error) => errors.push(error.message));
      page.on('console', (message) => {
        // A refused request is logged by the browser itself; the tests that expect one assert the page instead.
        if (message.type() === 'error' && !message.text().startsWith('Failed to load resource')) {
          errors.push(message.text());
        }
      });
      await use(undefined);
      expect(foreign, 'requests to another host').toEqual([]);
      expect(errors, 'errors in the console').toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };
