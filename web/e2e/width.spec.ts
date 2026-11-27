import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { accounts, expect, signIn, test } from './fixtures';

// How the pages use the window's width: the case list and the evidence page between their margins, the search
// page's side panes as shares of the window within a least and a greatest width, and the same pages with the
// browser's font size raised. One case of two synthetic files, used by the tests in order.

const caseName = 'width run';
const scratch = join(import.meta.dirname, '..', 'test-results', 'width-files');
const syslogSample = join(import.meta.dirname, '..', '..', 'fixtures', 'syslog', 'sample.log');
const LONG_NAME = 'access-log-of-the-reverse-proxy-in-front-of-the-shop-frontend-cluster-node-07-2026-11-07.log';

/** 120 requests a second apart, every tenth a 404, under a file name longer than any column gives it at 1366. */
function nginxLog(): string {
  const lines: string[] = [];
  for (let i = 0; i < 120; i++) {
    const t = new Date(Date.UTC(2026, 10, 7, 6, 0, i)).toISOString().slice(11, 19);
    lines.push(`198.51.100.${i % 250} - - [07/Nov/2026:${t} +0000] "GET /shop/item/${i} HTTP/1.1" ${i % 10 === 0 ? 404 : 200} ${1000 + i} "-" "curl/8.5.0"`);
  }
  mkdirSync(scratch, { recursive: true });
  const path = join(scratch, LONG_NAME);
  writeFileSync(path, `${lines.join('\n')}\n`);
  return path;
}

/** The least, the share of the window and the greatest width of the search page's side panes, from the tokens. */
const RAIL = { min: 224, share: 0.145, max: 360 };
const DETAIL = { min: 360, share: 0.34, max: 600 };
const clamp = (pane: { min: number; share: number; max: number }, width: number) => Math.min(Math.max(pane.min, pane.share * width), pane.max);

let caseId = '';

async function signedIn(page: Page): Promise<void> {
  await page.goto('/login');
  await signIn(page, accounts.owner());
  await expect(page.getByRole('heading', { name: 'Cases' })).toBeVisible();
}

/** The document's overflow and the boxes of the parts named, in the window's coordinates. */
async function boxes(page: Page, selectors: Record<string, string>) {
  return page.evaluate((named) => {
    const doc = document.documentElement;
    const parts: Record<string, { left: number; right: number; top: number; bottom: number; width: number } | null> = {};
    for (const [name, selector] of Object.entries(named)) {
      const el = document.querySelector(selector);
      const r = el?.getBoundingClientRect();
      parts[name] = r === undefined || r.width === 0 ? null : { left: r.left, right: r.right, top: r.top, bottom: r.bottom, width: r.width };
    }
    return { tall: doc.scrollHeight > doc.clientHeight, wide: doc.scrollWidth > doc.clientWidth, parts };
  }, selectors);
}

const casesTable = (page: Page) => page.getByRole('region', { name: 'Cases' }).locator('table');
const filesTable = (page: Page) => page.getByRole('region', { name: 'Evidence files' }).locator('table');
const fileRow = (page: Page, name: string) => page.getByRole('row').filter({ hasText: name });

test.describe.configure({ mode: 'serial' });

test('a case of two files for the width tests', async ({ page }) => {
  test.setTimeout(180_000);
  await signedIn(page);
  await page.getByRole('button', { name: 'New case' }).first().click();
  await page.getByRole('dialog').getByLabel('Name').fill(caseName);
  await page.getByRole('dialog').getByRole('button', { name: 'Create case' }).click();
  await page.getByRole('link', { name: caseName }).click();
  await page.getByLabel('Files to add').setInputFiles([nginxLog(), syslogSample]);
  for (const name of [LONG_NAME, 'sample.log']) {
    await expect(fileRow(page, name)).toContainText('Parsed', { timeout: 120_000 });
  }
  caseId = new URL(page.url()).pathname.split('/').pop() ?? '';
});

test('the case list and the evidence page reach the right margin, 24 px under 1600 and 40 from it', async ({ page }) => {
  await signedIn(page);
  for (const [width, height, margin] of [[1920, 1080, 40], [1600, 900, 40], [1366, 768, 24], [1280, 720, 24]] as const) {
    const where = `${String(width)}x${String(height)}`;
    await page.setViewportSize({ width, height });
    await page.goto('/cases');
    await expect(casesTable(page)).toBeVisible();
    const list = await page.getByRole('region', { name: 'Cases' }).boundingBox();
    expect(list?.x, where).toBeCloseTo(margin, 0);
    expect((list?.x ?? 0) + (list?.width ?? 0), where).toBeCloseTo(width - margin, 0);

    await page.goto(`/cases/${caseId}`);
    await expect(fileRow(page, 'sample.log')).toContainText('Parsed');
    const files = await page.getByRole('region', { name: 'Evidence files' }).boundingBox();
    expect(files?.x, where).toBeCloseTo(margin, 0);
    expect((files?.x ?? 0) + (files?.width ?? 0), where).toBeCloseTo(width - margin, 0);
    expect((await boxes(page, {})).wide, where).toBe(false);
  }
});

test('the SHA-256 is whole from 1600 and ten and four characters under it, and the file name takes the room', async ({ page }) => {
  await signedIn(page);
  const row = fileRow(page, LONG_NAME);
  const name = row.locator('td.file-name .ellipsis');

  await page.setViewportSize({ width: 1920, height: 1080 });
  await page.goto(`/cases/${caseId}`);
  await expect(row).toContainText('Parsed');
  const whole = row.locator('.hash-full');
  await expect(whole).toBeVisible();
  await expect(whole).toHaveText(/^[0-9a-f]{64}$/);
  await expect(row.locator('.hash-short')).toBeHidden();
  // The hash is not cut, and the name has more room than the 320 px it had under a cap.
  expect(await whole.evaluate((el) => el.scrollWidth <= el.clientWidth + 0.5)).toBe(true);
  const wide = await name.boundingBox();
  expect(wide?.width).toBeGreaterThan(320);

  await page.setViewportSize({ width: 1366, height: 768 });
  await expect(row.locator('.hash-short')).toBeVisible();
  await expect(row.locator('.hash-short')).toHaveText(/^[0-9a-f]{10}…[0-9a-f]{4}$/);
  await expect(whole).toBeHidden();
  // The name is cut to its column, with the whole name in its title.
  expect(await name.evaluate((el) => el.scrollWidth > el.clientWidth)).toBe(true);
  await expect(name).toHaveAttribute('title', LONG_NAME);
});

test('under 1280 the flags stand under the file name, and under 1024 a table scrolls inside its section, never the page', async ({ page }) => {
  await signedIn(page);
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto(`/cases/${caseId}`);
  const row = fileRow(page, 'sample.log');
  await expect(row).toContainText('Parsed');
  const flagsColumn = filesTable(page).getByRole('columnheader', { name: 'Flags' });
  await expect(flagsColumn).toBeVisible();
  const flags = await row.locator('td.c-flags .file-flags').innerText();
  expect(flags.trim(), 'the syslog sample carries a flag').not.toBe('');
  await expect(row.locator('td.file-name .file-flags')).toBeHidden();

  await page.setViewportSize({ width: 1100, height: 800 });
  await expect(flagsColumn).toBeHidden();
  await expect(row.locator('td.file-name .file-flags')).toBeVisible();
  expect(await row.locator('td.file-name .file-flags').innerText()).toBe(flags);
  expect((await boxes(page, {})).wide).toBe(false);

  for (const width of [1000, 800]) {
    await page.setViewportSize({ width, height: 800 });
    await page.goto(`/cases/${caseId}`);
    await expect(row).toContainText('Parsed');
    expect((await boxes(page, {})).wide, `evidence at ${String(width)}`).toBe(false);
    if (width === 800) {
      const wrap = page.getByRole('region', { name: 'Evidence files' }).locator('.table-wrap');
      expect(await wrap.evaluate((el) => el.scrollWidth > el.clientWidth), 'the files table scrolls inside its section at 800').toBe(true);
    }
    await page.goto('/cases');
    await expect(casesTable(page)).toBeVisible();
    expect((await boxes(page, {})).wide, `cases at ${String(width)}`).toBe(false);
  }
});

const SEARCH = '?q=status%3D404&from=2026-11-07T06%3A00%3A00Z&to=2026-11-07T06%3A11%3A00Z';
const PANES = { panes: '.panes', rail: '.panes > .rail', events: '.events-scroll', detail: '.panes > .detail' };

async function openSearch(page: Page): Promise<void> {
  await page.goto(`/cases/${caseId}/search${SEARCH}`);
  await expect(page.locator('.events-head')).toContainText('12 of 12 events loaded');
  await expect(page.locator('.timeline-plot canvas')).toBeVisible();
}

test('the field list and the detail are shares of the window within their least and greatest widths', async ({ page }) => {
  await signedIn(page);
  await page.setViewportSize({ width: 1920, height: 1080 });
  await openSearch(page);
  for (const [width, height] of [[1024, 700], [1366, 768], [1600, 900], [1920, 1080], [2560, 1440], [3200, 1600]] as const) {
    const where = `${String(width)}x${String(height)}`;
    await page.setViewportSize({ width, height });
    await page.getByRole('button', { name: /Open the event of/ }).first().click();
    await expect(page.getByRole('complementary', { name: 'Event', exact: true }).getByLabel('Raw line', { exact: true })).toBeVisible();
    const at = await boxes(page, PANES);
    expect(at.parts.detail?.width, where).toBeCloseTo(clamp(DETAIL, width), 0);
    if (width >= 1600) {
      expect(at.parts.rail?.width, where).toBeCloseTo(clamp(RAIL, width), 0);
    }
    await page.keyboard.press('Escape');
    if (width < 1600) {
      await expect(page.locator(PANES.rail), where).toBeVisible();
      expect((await boxes(page, PANES)).parts.rail?.width, where).toBeCloseTo(clamp(RAIL, width), 0);
    }
  }
});

test('at 2560x1440 no page shows an empty band and no pane is wider than its greatest width', async ({ page }) => {
  await signedIn(page);
  await page.setViewportSize({ width: 2560, height: 1440 });

  await page.goto('/cases');
  await expect(casesTable(page)).toBeVisible();
  const list = await boxes(page, { cases: '[aria-label="Cases"]', bar: '.app > header' });
  expect(list.parts.cases?.left).toBeCloseTo(40, 0);
  expect(list.parts.cases?.right).toBeCloseTo(2520, 0);
  expect(list.parts.bar?.right).toBeCloseTo(2560, 0);

  await page.goto(`/cases/${caseId}`);
  await expect(fileRow(page, 'sample.log')).toContainText('Parsed');
  const evidence = await boxes(page, { overview: '[aria-label="Case overview"]', files: '[aria-label="Evidence files"]', facts: '[aria-label="Case overview"] dl' });
  for (const part of ['overview', 'files']) {
    expect(evidence.parts[part]?.left, part).toBeCloseTo(40, 0);
    expect(evidence.parts[part]?.right, part).toBeCloseTo(2520, 0);
  }
  // The overview's values have the row's width, so a value wraps only when the row is full.
  expect(evidence.parts['facts']?.right).toBeGreaterThan(2520 - 20);

  await openSearch(page);
  for (const open of [false, true]) {
    if (open) {
      await page.getByRole('button', { name: /Open the event of/ }).first().click();
      await expect(page.getByRole('complementary', { name: 'Event', exact: true })).toBeVisible();
    }
    const at = await boxes(page, PANES);
    const where = open ? 'event open' : 'no event open';
    expect({ tall: at.tall, wide: at.wide }, where).toEqual({ tall: false, wide: false });
    // The panes run from the left edge to the right edge and down to the bottom of the window.
    expect(at.parts.panes?.left, where).toBe(0);
    expect(at.parts.panes?.right, where).toBeCloseTo(2560, 0);
    expect(at.parts.rail?.left, where).toBe(0);
    expect(at.parts.detail?.right, where).toBeCloseTo(2560, 0);
    expect(at.parts.events?.bottom, where).toBeCloseTo(1440, 0);
    expect(at.parts.rail?.width, where).toBeLessThanOrEqual(RAIL.max);
    expect(at.parts.detail?.width, where).toBeLessThanOrEqual(DETAIL.max);
    // The events touch both side panes: nothing stands empty between them.
    expect(at.parts.events?.left, where).toBeCloseTo(at.parts.rail?.right ?? 0, 0);
    expect(at.parts.events?.right, where).toBeLessThanOrEqual((at.parts.detail?.left ?? 0) + 0.5);
    expect(at.parts.events?.right, where).toBeGreaterThan((at.parts.detail?.left ?? 0) - 20);
  }
});

test('with the browser font size at 20 px the text grows and the search page still fills the window without scrolling', async ({ page }) => {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Page.setFontSizes', { fontSizes: { standard: 20, fixed: 20 } });
  await signedIn(page);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).fontSize)).toBe('20px');
  // The page's text is 13 px at the default of 16, so 16.25 px here.
  expect(await page.evaluate(() => getComputedStyle(document.body).fontSize)).toBe('16.25px');

  for (const [width, height] of [[1920, 911], [1366, 768], [1024, 600]] as const) {
    const where = `${String(width)}x${String(height)}`;
    await page.setViewportSize({ width, height });
    await openSearch(page);
    await page.getByRole('button', { name: /Open the event of/ }).first().click();
    await expect(page.getByRole('complementary', { name: 'Event', exact: true }).getByLabel('Raw line', { exact: true })).toBeVisible();
    const at = await boxes(page, PANES);
    expect({ tall: at.tall, wide: at.wide }, where).toEqual({ tall: false, wide: false });
    expect(at.parts.events?.bottom, where).toBeGreaterThan(height - 2);
    expect(at.parts.events?.bottom, where).toBeLessThanOrEqual(height);
    await page.keyboard.press('Escape');
  }

  await page.setViewportSize({ width: 1366, height: 768 });
  for (const address of ['/cases', `/cases/${caseId}`]) {
    await page.goto(address);
    await expect(page.locator('table').first()).toBeVisible();
    expect((await boxes(page, {})).wide, address).toBe(false);
  }
});
