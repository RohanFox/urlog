import { accounts, expect, signIn, test } from './fixtures';

// The store of a run is new, so one fixed name is this run's case.
const caseName = 'browser run';

test('the first paint is the loading screen, with both fonts served by the instance', async ({ page }) => {
  const fonts: string[] = [];
  page.on('response', (response) => {
    if (response.url().endsWith('.woff2')) {
      fonts.push(new URL(response.url()).pathname);
    }
  });
  await page.goto('/login');
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await expect(page.getByRole('img', { name: 'urlog, log forensics' })).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  expect(fonts.some((f) => f.includes('IBMPlexSans'))).toBe(true);
  const family = await page.evaluate(() => getComputedStyle(document.body).fontFamily);
  expect(family).toContain('IBM Plex Sans');
});

test('a wrong password gets one generic line that names neither the name nor the password', async ({ page }) => {
  await page.goto('/login');
  const wrong = { name: accounts.other().name, password: 'not the password' };
  await signIn(page, wrong);
  await expect(page.getByText('That name and password were not accepted')).toBeVisible();
  await expect(page.getByText(/attempt|locked/i)).toHaveCount(0);
  await expect(page).toHaveURL(/\/login$/);
});

test('sign in, create a case, find it after a reload, sign out', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/login$/);
  await signIn(page, accounts.owner());

  await expect(page.getByRole('heading', { name: 'Cases' })).toBeVisible();
  await page.getByRole('button', { name: 'New case' }).first().click();
  const dialog = page.getByRole('dialog', { name: 'New case' });
  await dialog.getByLabel('Name').fill(caseName);
  await dialog.getByRole('button', { name: 'Create case' }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole('link', { name: caseName })).toBeVisible();

  // The session is the cookie's: a reload asks the server who this is and stays signed in.
  await page.reload();
  await expect(page.getByRole('link', { name: caseName })).toBeVisible();

  await page.getByRole('link', { name: caseName }).click();
  await expect(page.getByRole('heading', { name: caseName })).toBeVisible();
  await expect(page.getByText('Owner', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('No events with a usable time yet')).toBeVisible();

  await page.getByRole('button', { name: /Signed in as/ }).click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();

  // Signed out on the server too: the address of the case leads back to sign-in.
  await page.goBack();
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
});

test('a user without a share sees no such case and no case in their list', async ({ page }) => {
  await page.goto('/login');
  await signIn(page, accounts.owner());
  await page.getByRole('link', { name: caseName }).first().click();
  const address = new URL(page.url()).pathname;
  await page.getByRole('button', { name: /Signed in as/ }).click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();

  // Signing out on a case's page leaves no trace of its address for the next person.
  await expect(page).toHaveURL(/\/login$/);
  await signIn(page, accounts.other());
  await expect(page.getByRole('heading', { name: 'Start your first case' })).toBeVisible();
  await expect(page.getByText(caseName)).toHaveCount(0);

  await page.goto(address);
  await expect(page.getByRole('heading', { name: 'No such case' })).toBeVisible();
  await expect(page.getByText(caseName)).toHaveCount(0);
});

test('a session the server has ended sends the page to sign-in and back to the same address', async ({ page, context }) => {
  await page.goto('/login');
  await signIn(page, accounts.owner());
  await page.getByRole('link', { name: caseName }).first().click();
  const address = new URL(page.url()).pathname;

  // The cookie goes, as when the server ends a session; the next read is answered 401.
  await context.clearCookies();
  await page.goto(address);
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  expect(new URL(page.url()).searchParams.get('next')).toBe(address);
  await signIn(page, accounts.owner());
  await expect(page.getByRole('heading', { name: caseName })).toBeVisible();
});

test('the theme follows the system until the user chooses, and the choice survives a reload', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.goto('/login');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  const dark = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);

  await signIn(page, accounts.owner());
  await page.getByRole('button', { name: /Signed in as/ }).click();
  await page.getByRole('menuitemradio', { name: 'Light' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  const light = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
  expect(light).not.toBe(dark);

  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
});

test('with reduced motion the loader stands still', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  // Hold the session read, so the loading screen stays up to be looked at.
  await page.route('**/api/v1/auth/me', async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 1500));
    await route.continue();
  });
  await page.goto('/login', { waitUntil: 'commit' });
  const solid = page.locator('.boot .brand-loader-solid').first();
  await expect(solid).toBeVisible();
  expect(await solid.evaluate((el) => getComputedStyle(el).animationName)).toBe('none');
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
});

test('an unknown address under the API is not answered with the page', async ({ request }) => {
  for (const path of ['/api/v1/no-such-route', '/api/no-such-route', '/api']) {
    const response = await request.get(path);
    expect(response.status(), path).toBeGreaterThanOrEqual(400);
    expect(await response.text(), path).not.toContain('<div id="root">');
  }
  // Any other address is the page's own, which shows what it has for it.
  expect((await request.get('/cases/1')).status()).toBe(200);
});
