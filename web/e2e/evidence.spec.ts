import { randomBytes } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { accounts, expect, signIn, test } from './fixtures';

// The evidence page against the real backend: a file goes up, is hashed, recorded and parsed, and the page
// shows each step from what the server says. One case for the file, used by the tests in order.

const caseName = 'evidence run';
const sample = join(import.meta.dirname, '..', '..', 'fixtures', 'nginx', 'sample.log');
const scratch = join(import.meta.dirname, '..', 'test-results', 'evidence-files');
const CHUNK = 16 * 1024 * 1024;

/** A log of its own for every run, a little over two chunks long, so an upload of it can be cut between them. */
function bigLog(name: string): string {
  mkdirSync(scratch, { recursive: true });
  const salt = randomBytes(6).toString('hex');
  const line = (i: number) => `198.51.100.${i % 250} - - [07/Nov/2026:06:20:05 +0000] "GET /run/${salt}/${i} HTTP/1.1" 200 ${1000 + (i % 9000)} "-" "curl/8.5.0"\n`;
  const parts: string[] = [];
  let size = 0;
  for (let i = 0; size < 2 * CHUNK + 4 * 1024 * 1024; i++) {
    const text = line(i);
    parts.push(text);
    size += text.length;
  }
  const path = join(scratch, name);
  writeFileSync(path, parts.join(''));
  return path;
}

async function openCase(page: Page, account = accounts.owner()): Promise<void> {
  await page.goto('/login');
  await signIn(page, account);
  await page.getByRole('link', { name: caseName }).first().click();
  await expect(page.getByRole('heading', { name: caseName })).toBeVisible();
}

const fileRow = (page: Page, name: string) => page.getByRole('row').filter({ hasText: name });

test.describe.configure({ mode: 'serial' });

test('a file is uploaded, verified, recorded and parsed, and the page follows it', async ({ page }) => {
  await page.goto('/login');
  await signIn(page, accounts.owner());
  await page.getByRole('button', { name: 'New case' }).first().click();
  await page.getByRole('dialog').getByLabel('Name').fill(caseName);
  await page.getByRole('dialog').getByRole('button', { name: 'Create case' }).click();
  await page.getByRole('link', { name: caseName }).click();

  await expect(page.getByText('Add the first evidence files')).toBeVisible();
  await page.getByLabel('Files to add').setInputFiles(sample);

  // The upload row leaves once the file is recorded; the file is then a row of the list, with its hash.
  const row = fileRow(page, 'sample.log');
  await expect(row).toBeVisible({ timeout: 30_000 });
  await expect(row).toContainText('Parsed', { timeout: 60_000 });
  await expect(row).toContainText('nginx');
  await expect(page.getByRole('region', { name: 'Uploads' })).toHaveCount(0);

  const overview = page.getByRole('region', { name: 'Case overview' });
  await expect(overview).toContainText('1 file');
  await expect(overview).toContainText('Nothing waits');
  await expect(overview).toContainText('Field statistics');
});

test('the same bytes again are answered "already in this case", with a way to the file', async ({ page }) => {
  await openCase(page);
  await page.getByLabel('Files to add').setInputFiles(sample);

  const uploadsList = page.getByRole('region', { name: 'Uploads' });
  await expect(uploadsList).toContainText('Already in this case', { timeout: 30_000 });
  await uploadsList.getByRole('button', { name: 'Show the file' }).click();
  await expect(fileRow(page, 'sample.log')).toHaveCount(1);
  await expect(page.getByRole('button', { name: 'Clear the filter' })).toBeVisible();
});

test('a packet capture is refused with the reason of the server, and nothing is listed', async ({ page }) => {
  await openCase(page);
  // The magic number of a capture file, then filler: the server knows it by its first bytes.
  const capture = Buffer.concat([Buffer.from('d4c3b2a1020004000000000000000000ffff000001000000', 'hex'), randomBytes(256)]);
  await page.getByLabel('Files to add').setInputFiles({ name: 'trace.pcap', mimeType: 'application/octet-stream', buffer: capture });

  const uploadsList = page.getByRole('region', { name: 'Uploads' });
  await expect(uploadsList).toContainText('Not taken. Nothing was stored', { timeout: 30_000 });
  await expect(fileRow(page, 'trace.pcap')).toHaveCount(0);
});

test('an upload whose connection is cut goes on from the offset the server holds', async ({ page }) => {
  const path = bigLog('cut.log');
  const seen: string[] = [];
  let cut = false;
  await page.route('**/api/v1/cases/*/uploads/*', async (route) => {
    const request = route.request();
    if (request.method() !== 'GET') {
      seen.push(`${request.method()} ${request.headers()['upload-offset'] ?? ''}`.trim());
    }
    // The second chunk never reaches the server: the connection goes away under it, once.
    if (request.method() === 'PATCH' && request.headers()['upload-offset'] === String(CHUNK) && !cut) {
      cut = true;
      await route.abort('connectionreset');
      return;
    }
    await route.continue();
  });

  await openCase(page);
  await page.getByLabel('Files to add').setInputFiles(path);
  await expect(page.getByRole('region', { name: 'Uploads' })).toContainText('Connection lost', { timeout: 60_000 });

  const row = fileRow(page, 'cut.log');
  await expect(row).toBeVisible({ timeout: 120_000 });
  await expect(row).toContainText('Parsed', { timeout: 120_000 });

  // First chunk, the cut chunk, a question to the server, the same chunk from the offset it answered, the rest.
  expect(cut).toBe(true);
  expect(seen).toEqual(['PATCH 0', `PATCH ${CHUNK}`, 'HEAD', `PATCH ${CHUNK}`, `PATCH ${2 * CHUNK}`]);
});

test('an upload survives a reload: the page knows how far it got and goes on once the file is chosen again', async ({ page }) => {
  const path = bigLog('reload.log');
  let hold = true;
  await page.route('**/api/v1/cases/*/uploads/*', async (route) => {
    // The second chunk waits, so the reload happens with the upload in flight.
    if (hold && route.request().method() === 'PATCH' && route.request().headers()['upload-offset'] === String(CHUNK)) {
      await new Promise((resolve) => setTimeout(resolve, 60_000));
      await route.abort().catch(() => undefined);
      return;
    }
    await route.continue();
  });

  await openCase(page);
  await page.getByLabel('Files to add').setInputFiles(path);
  const uploadsList = page.getByRole('region', { name: 'Uploads' });
  await expect(uploadsList.getByRole('progressbar')).toHaveAttribute('value', /^(4[0-9]|[5-9][0-9])$/, { timeout: 60_000 });

  hold = false;
  page.once('dialog', (dialog) => void dialog.accept());
  await page.reload();

  await expect(uploadsList).toContainText('Interrupted by a reload of the page', { timeout: 30_000 });
  await expect(uploadsList).toContainText('reload.log');
  await expect(uploadsList.getByRole('progressbar')).toHaveAttribute('value', /^4[0-9]$/);

  await page.getByLabel('The file reload.log again').setInputFiles(path);
  const row = fileRow(page, 'reload.log');
  await expect(row).toBeVisible({ timeout: 120_000 });
  await expect(row).toContainText('Parsed', { timeout: 120_000 });
});

test('closing the tab with an upload in flight asks first', async ({ page }) => {
  const path = bigLog('leave.log');
  await page.route('**/api/v1/cases/*/uploads/*', async (route) => {
    if (route.request().method() === 'PATCH') {
      await new Promise((resolve) => setTimeout(resolve, 30_000));
      await route.abort().catch(() => undefined);
      return;
    }
    await route.continue();
  });
  await openCase(page);
  await page.getByLabel('Files to add').setInputFiles(path);
  await expect(page.getByRole('region', { name: 'Uploads' })).toContainText('leave.log');

  const asked = new Promise<string>((resolve) => {
    page.once('dialog', (dialog) => {
      resolve(dialog.type());
      // Stay: the upload is cancelled by the test below, not by leaving.
      void dialog.dismiss();
    });
  });
  await page.close({ runBeforeUnload: true });
  expect(await asked).toBe('beforeunload');
});

test('a read-only member reads the files and has no way to upload', async ({ page }) => {
  await openCase(page);
  const address = new URL(page.url()).pathname;
  const caseId = address.split('/').pop() ?? '';

  // The owner shares the case, read-only, through the API the interface will use for it.
  const me = (await (await page.request.get('/api/v1/auth/me')).json()) as { csrf_token: string };
  const shared = await page.request.post(`/api/v1/cases/${caseId}/members`, { headers: { 'X-CSRF-Token': me.csrf_token }, data: { user: accounts.other().name, role: 'ro' } });
  expect(shared.status()).toBe(201);
  await page.getByRole('button', { name: /Signed in as/ }).click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();

  await signIn(page, accounts.other());
  await page.getByRole('link', { name: caseName }).click();
  await expect(page.getByText('Your role in this case is read-only')).toBeVisible();
  await expect(page.getByRole('region', { name: 'Add evidence' })).toHaveCount(0);
  await expect(fileRow(page, 'sample.log')).toBeVisible();
});
