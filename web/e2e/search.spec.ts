import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page, Request } from '@playwright/test';
import { accounts, expect, signIn, test } from './fixtures';

// The search page against the real backend and a real store: a case of an nginx log long enough to page, the
// syslog sample, and two files of hostile lines. One case, used by the tests in order.

const caseName = 'search run';
const scratch = join(import.meta.dirname, '..', 'test-results', 'search-files');
const syslogSample = join(import.meta.dirname, '..', '..', 'fixtures', 'syslog', 'sample.log');
const NGINX_LINES = 650;
const MEGABYTE = 1024 * 1024;

/** 650 requests, one a second from 06:00 on 2026-11-07, every tenth a 404: more than three pages of 200. */
function nginxLog(): string {
  const lines: string[] = [];
  for (let i = 0; i < NGINX_LINES; i++) {
    const at = new Date(Date.UTC(2026, 10, 7, 6, 0, i));
    const hh = String(at.getUTCHours()).padStart(2, '0');
    const mm = String(at.getUTCMinutes()).padStart(2, '0');
    const ss = String(at.getUTCSeconds()).padStart(2, '0');
    const status = i % 10 === 0 ? 404 : 200;
    lines.push(`198.51.100.${i % 250} - - [07/Nov/2026:${hh}:${mm}:${ss} +0000] "GET /shop/item/${i} HTTP/1.1" ${status} ${1000 + i} "-" "curl/8.5.0"`);
  }
  return write('paged.log', `${lines.join('\n')}\n`);
}

/** What an attacker can put into a log line, each on its own line with a time of its own on 2026-11-08. */
const HOSTILE = {
  script: '<script>window.pwned=1</script>',
  img: '<img src=x onerror="window.pwned=1">',
  handler: '<div onmouseover="window.pwned=1" onclick="window.pwned=1">hover me</div>',
  address: '<a href="javascript:window.pwned=1">click me</a> javascript:window.pwned=1',
  bidi: 'invoice\u202Efdp.exe \u2066isolated\u2069 zero\u200Bwidth',
  nul: 'before the NUL \u0000 after the NUL',
  ansi: '\u001b[31mred\u001b[0m and a title \u001b]0;pwned\u0007',
} as const;

function hostileLog(): string {
  const lines = Object.values(HOSTILE).map((text, i) => `2026-11-08T10:00:0${i + 1}+00:00 ${text}`);
  return write('hostile.log', `${lines.join('\n')}\n`);
}

/** One line of a megabyte of one character, between two ordinary lines. */
function longLineLog(): string {
  return write('long-line.log', `2026-11-08T10:00:08+00:00 before the long line\n2026-11-08T10:00:09+00:00 ${'A'.repeat(MEGABYTE)}\n2026-11-08T10:00:10+00:00 after the long line\n`);
}

/**
 * Waits until a file is parsed. Detection may keep a file with control bytes or one enormous line as "not a
 * log"; a person would then name its source by hand, and so does this, through the route the interface will use.
 */
async function parsed(page: Page, name: string): Promise<'detected' | 'overridden'> {
  const row = page.getByRole('row').filter({ hasText: name });
  await expect(row).toContainText(/Parsed|Kept, not a log/, { timeout: 120_000 });
  if ((await row.textContent())?.includes('Parsed') === true) {
    return 'detected';
  }
  const caseId = new URL(page.url()).pathname.split('/').pop() ?? '';
  const me = (await (await page.request.get('/api/v1/auth/me')).json()) as { csrf_token: string };
  const list = (await (await page.request.get(`/api/v1/cases/${caseId}/files?q=${encodeURIComponent(name)}`)).json()) as { files: Array<{ id: number; version: number }> };
  const file = list.files[0];
  if (file === undefined) {
    throw new Error(`${name} is not in the file list`);
  }
  const answer = await page.request.put(`/api/v1/cases/${caseId}/files/${file.id}/override`, { headers: { 'X-CSRF-Token': me.csrf_token, 'If-Match': String(file.version) }, data: { source: 'generic' } });
  expect(answer.status(), `naming the source of ${name}: ${await answer.text()}`.slice(0, 300)).toBe(202);
  await expect(row).toContainText('Parsed', { timeout: 120_000 });
  return 'overridden';
}

/** Two lines with bytes that are not UTF-8: Latin-1 letters, a stray continuation byte, an overlong form, 0xFF. */
const NOT_UTF8 = Buffer.concat([Buffer.from('2026-11-08T12:00:01+00:00 caf'), Buffer.from([0xe9]), Buffer.from(' na'), Buffer.from([0xef]), Buffer.from('ve')]);

function notUtf8Log(): string {
  mkdirSync(scratch, { recursive: true });
  const path = join(scratch, 'not-utf8.log');
  writeFileSync(path, Buffer.concat([NOT_UTF8, Buffer.from('\n2026-11-08T12:00:02+00:00 stray '), Buffer.from([0x80, 0xc0, 0xaf, 0xff]), Buffer.from(' end\n')]));
  return path;
}

/** One syslog record with three thousand parameters of structured data, each a field of its event. */
function manyFieldsLog(): string {
  const params = Array.from({ length: 3000 }, (_, i) => `p${String(i).padStart(4, '0')}="<b>v${i}</b>"`).join(' ');
  return write('many-fields.log', `<134>1 2026-11-08T11:00:00+00:00 fields-host fieldsapp 4242 ID9 [wide@32473 ${params}] a record with three thousand fields\n`);
}

function write(name: string, text: string): string {
  mkdirSync(scratch, { recursive: true });
  const path = join(scratch, name);
  writeFileSync(path, text);
  return path;
}

async function openSearch(page: Page, query = '', account = accounts.owner()): Promise<void> {
  await page.goto('/login');
  await signIn(page, account);
  await page.getByRole('link', { name: caseName }).first().click();
  await expect(page.getByRole('heading', { name: caseName })).toBeVisible();
  const caseId = new URL(page.url()).pathname.split('/').pop() ?? '';
  await page.goto(`/cases/${caseId}/search${query}`);
  await expect(page.getByRole('textbox', { name: 'Query' })).toBeVisible();
}

const queryBox = (page: Page) => page.getByRole('textbox', { name: 'Query' });
const eventsRegion = (page: Page) => page.getByRole('region', { name: 'Events' });
const eventsStatus = (page: Page) => eventsRegion(page).locator('.events-head');

async function run(page: Page, query: string): Promise<void> {
  await queryBox(page).fill(query);
  await queryBox(page).press('Enter');
}

/** What the page last wrote to the clipboard, once the write has been accepted (the button then says "Copied"). */
async function copied(page: Page): Promise<string> {
  await expect(page.getByRole('complementary', { name: 'Event' }).getByText('Copied', { exact: true }).first()).toBeAttached();
  return page.evaluate(() => (window as unknown as { copied: string }).copied);
}

/** Every dialog a page raised during the test that runs now. */
const dialogs: string[] = [];

/** Fails the test if anything a log line carried ever ran or was built into the page. */
async function nothingRan(page: Page): Promise<void> {
  expect(dialogs, 'dialogs raised by the page').toEqual([]);
  expect(await page.evaluate(() => (window as unknown as { pwned?: number }).pwned)).toBeUndefined();
  expect(await page.evaluate(() => (window as unknown as { violations: string[] }).violations)).toEqual([]);
  expect(await page.title()).toBe('urlog');
  // No element came out of a line: no script but the page's own files, no image, no handler attribute, no link to code.
  expect(await page.locator('script:not([src^="/"])').count()).toBe(0);
  const built = (selector: string) => page.locator(selector).evaluateAll((els) => els.map((el) => `${el.tagName.toLowerCase()}.${el.getAttribute('class') ?? ''}`));
  // Where log text is drawn: the parts of the answer and the open event. (The editor of the query draws an
  // image of its own as a spacer beside its placeholder; no log text reaches the editor.)
  expect(await built(':is(.region, .detail) :is(img, iframe, object, embed, script, style, link, form, input[type="hidden"])')).toEqual([]);
  expect(await built('[onerror], [onclick], [onmouseover], [onload], a[href^="javascript"]')).toEqual([]);
}

test.describe.configure({ mode: 'serial' });

test.beforeEach(async ({ page }) => {
  // A dialog is what an injected alert would raise; a policy violation is what an injected script or style would raise.
  dialogs.length = 0;
  page.on('dialog', (dialog) => {
    dialogs.push(dialog.message());
    void dialog.dismiss();
  });
  await page.addInitScript(() => {
    const w = window as unknown as { violations: string[] };
    w.violations = [];
    // The page may write to the clipboard and may not read it, so a test cannot read it either: what the page
    // hands to the clipboard is kept here, and handed on.
    const clipboard = navigator.clipboard;
    const write = clipboard.writeText.bind(clipboard);
    clipboard.writeText = (text: string) => {
      (window as unknown as { copied: string }).copied = text;
      return write(text);
    };
    document.addEventListener('securitypolicyviolation', (event) => {
      w.violations.push(`${event.violatedDirective} ${event.blockedURI}`);
    });
  });
});

test('a case is filled and its search page shows timeline, events and count', async ({ page }) => {
  test.setTimeout(180_000);
  await page.goto('/login');
  await signIn(page, accounts.owner());
  await page.getByRole('button', { name: 'New case' }).first().click();
  await page.getByRole('dialog').getByLabel('Name').fill(caseName);
  await page.getByRole('dialog').getByRole('button', { name: 'Create case' }).click();
  await page.getByRole('link', { name: caseName }).click();

  await page.getByLabel('Files to add').setInputFiles([nginxLog(), syslogSample, hostileLog(), longLineLog(), manyFieldsLog(), notUtf8Log()]);
  const how: Record<string, string> = {};
  for (const name of ['paged.log', 'sample.log', 'hostile.log', 'long-line.log', 'many-fields.log', 'not-utf8.log']) {
    how[name] = await parsed(page, name);
  }
  // Kept with the run: which of the hostile files detection took for a log by itself.
  test.info().annotations.push({ type: 'detection', description: JSON.stringify(how) });
  writeFileSync(join(scratch, 'detection.json'), JSON.stringify(how));
  // A log with a NUL, a bell and escapes in it is a log: nobody has to name its source by hand.
  expect(how).toEqual({ 'paged.log': 'detected', 'sample.log': 'detected', 'hostile.log': 'detected', 'long-line.log': 'detected', 'many-fields.log': 'detected', 'not-utf8.log': 'detected' });

  await page.getByRole('link', { name: 'Search', exact: true }).click();
  await expect(page).toHaveURL(/\/cases\/\d+\/search$/);
  await expect(eventsStatus(page)).toContainText(/200 of [\d,]+ events loaded/, { timeout: 30_000 });
  await expect(page.locator('.timeline-plot canvas')).toBeVisible();
  await expect(page.getByText(/Finished in [\d,]+ ms/)).toBeVisible();
  await expect(page.getByRole('list', { name: 'Severity of the bars' })).toBeVisible();
});

test('every answer carries the content policy, and the page raises no violation of it', async ({ page }) => {
  const policies = new Map<string, string | undefined>();
  page.on('response', (response) => {
    const path = new URL(response.url()).pathname;
    policies.set(path.startsWith('/assets/') ? '/assets/*' : path.replace(/\d+/g, 'N'), response.headers()['content-security-policy']);
  });
  await openSearch(page, '?q=status%3D404');
  await expect(eventsStatus(page)).toContainText(/65 of 65 events loaded/);

  const expected = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
  for (const path of ['/login', '/assets/*', '/api/vN/auth/me', '/api/vN/cases/N/search', '/api/vN/cases/N/dashboard']) {
    expect(policies.get(path), `the policy on ${path}`).toBe(expected);
  }
  // The other headers of every answer, read from the page itself and from an API answer.
  const answer = await page.request.get('/api/v1/auth/me');
  const page0 = await page.request.get('/login');
  for (const headers of [answer.headers(), page0.headers()]) {
    expect(headers['x-content-type-options']).toBe('nosniff');
    expect(headers['referrer-policy']).toBe('no-referrer');
    expect(headers['cross-origin-opener-policy']).toBe('same-origin');
    expect(headers['cross-origin-resource-policy']).toBe('same-origin');
    expect(headers['permissions-policy']).toContain('camera=()');
    expect(headers['strict-transport-security']).toBeUndefined();
  }
  expect(answer.headers()['cache-control']).toBe('no-store');
  // The editor's styles, the chart and the menu all work under it.
  await expect(queryBox(page)).toHaveCSS('font-family', /JetBrains Mono/);
  await page.getByRole('button', { name: /Signed in as/ }).click();
  await expect(page.getByRole('menuitem', { name: 'Sign out' })).toBeVisible();
  await page.keyboard.press('Escape');
  await nothingRan(page);
});

test('a query narrows timeline, events and count, and the address is the search', async ({ page }) => {
  await openSearch(page);
  await run(page, 'status=404');
  await expect(eventsStatus(page)).toContainText('65 of 65 events loaded');
  await expect(page).toHaveURL(/\?q=status%3D404$/);
  await expect(page.locator('#timeline-text')).toContainText('65 events');

  // A reload runs the same search; back returns to the search before.
  await page.reload();
  await expect(eventsStatus(page)).toContainText('65 of 65 events loaded');
  await expect(queryBox(page)).toHaveText('status=404');
  await run(page, 'status=200');
  await expect(eventsStatus(page)).toContainText(/200 of 585 events loaded/);
  await page.goBack();
  await expect(eventsStatus(page)).toContainText('65 of 65 events loaded');
  await expect(queryBox(page)).toHaveText('status=404');
  await page.goForward();
  await expect(eventsStatus(page)).toContainText(/of 585 events loaded/);
});

test('the list pages by the cursor to its end and no event comes twice', async ({ page }) => {
  const sent: Array<{ cursor: boolean }> = [];
  const keys: string[] = [];
  page.on('request', (request: Request) => {
    if (request.method() === 'POST' && request.url().endsWith('/search')) {
      sent.push({ cursor: 'cursor' in (request.postDataJSON() as Record<string, unknown>) });
    }
  });
  page.on('response', (response) => {
    if (response.request().method() !== 'POST' || !response.url().endsWith('/search') || response.status() !== 200) {
      return;
    }
    void response.text().then((body) => {
      for (const frame of body.split('\n\n')) {
        if (!frame.startsWith('event: page')) {
          continue;
        }
        const data = JSON.parse(frame.slice(frame.indexOf('data: ') + 6)) as { columns: string[]; rows: unknown[][] };
        const file = data.columns.indexOf('file.id');
        const offset = data.columns.indexOf('file.offset');
        keys.push(...data.rows.map((row) => `${String(row[file])}:${String(row[offset])}`));
      }
    });
  });

  await openSearch(page, '?q=url.path%3D%2Fshop%2F*');
  await expect(eventsStatus(page)).toContainText('200 of 650 events loaded');
  const scroller = page.locator('.events-scroll');
  for (const loaded of [400, 600, 650]) {
    await scroller.evaluate((el) => {
      el.scrollTop = el.scrollHeight;
    });
    await expect(eventsStatus(page)).toContainText(`${loaded} of 650 events loaded`);
  }
  await scroller.evaluate((el) => {
    el.scrollTop = el.scrollHeight;
  });
  await expect(eventsRegion(page)).toContainText('End of the results, 650 events');

  // One first request and three pages by cursor; 650 keys, each once.
  expect(sent.map((s) => s.cursor)).toEqual([false, true, true, true]);
  await expect.poll(() => keys.length).toBe(650);
  expect(new Set(keys).size).toBe(650);
  // Every row has the one height: the list is as tall as its rows, and only those in view are in the document.
  const heights = await page.locator('.events-table tbody tr.event').evaluateAll((rows) => [...new Set(rows.map((row) => row.getBoundingClientRect().height))]);
  expect(heights).toHaveLength(1);
  expect(await page.locator('.events-table tbody tr.event').count()).toBeLessThan(80);
});

test('a drag on the timeline, and the arrow keys on it, narrow the range in the address', async ({ page }) => {
  // The axis is the range that was searched: the eleven minutes of the generated log.
  await openSearch(page, '?q=url.path%3D%2Fshop%2F*&from=2026-11-07T06%3A00%3A00Z&to=2026-11-07T06%3A11%3A00Z');
  await expect(eventsStatus(page)).toContainText('200 of 650 events loaded');

  const over = page.locator('.timeline-plot .u-over');
  const box = await over.boundingBox();
  if (box === null) {
    throw new Error('the timeline has no plotting area');
  }
  await page.mouse.move(box.x + box.width * 0.25, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.5, box.y + box.height / 2, { steps: 8 });
  await page.mouse.up();
  await expect(page).toHaveURL(/from=2026-11-07T06%3A02%3A\d\d\.000Z&to=2026-11-07T06%3A05%3A\d\d\.000Z/);
  await expect(eventsStatus(page)).toContainText(/of (1\d\d|2\d\d) events loaded/);
  await expect(page.getByLabel('From')).toHaveValue(/^2026-11-07T06:0/);

  // The same by keyboard, from the whole range again.
  await page.goBack();
  await expect(eventsStatus(page)).toContainText('200 of 650 events loaded');
  const plot = page.locator('.timeline-plot');
  await plot.focus();
  await page.keyboard.press('ArrowLeft');
  await page.keyboard.press('Shift+ArrowLeft');
  await expect(page.locator('#timeline-text')).toContainText(/^Selected 2026-11-07 06:1\d:\d\d to 2026-11-07 06:1\d:\d\d, \d+ events\. Enter searches it\.$/);
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/from=2026-11-07T06%3A1/);
  await expect(eventsStatus(page)).not.toContainText('of 650');
});

test('a syntax error is marked where the server found it, and the results before stay', async ({ page }) => {
  await openSearch(page, '?q=status%3D404');
  await expect(eventsStatus(page)).toContainText('65 of 65 events loaded');
  await run(page, 'status=404 | eval x=1');
  await expect(page.getByRole('alert')).toContainText(/At character 14: .*eval/);
  await expect(page.locator('.cm-mark-error')).toHaveText('eval');
  await expect(page.getByText(/the results of the search before/)).toBeVisible();
  await expect(eventsStatus(page)).toContainText('65 of 65 events loaded');
});

test('a search started while another is in flight leaves nothing of the first on the page', async ({ page }) => {
  let release: (() => void) | undefined;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let heldBack = 0;
  await page.route('**/api/v1/cases/*/search', async (route) => {
    const body = route.request().postDataJSON() as { query?: string };
    if (body.query === 'status=200') {
      heldBack += 1;
      await gate;
      await route.continue().catch(() => undefined);
      return;
    }
    await route.continue();
  });

  await openSearch(page, '?q=status%3D200');
  await expect(page.getByRole('button', { name: 'Cancel' })).toBeVisible();
  await run(page, 'status=404');
  await expect(eventsStatus(page)).toContainText('65 of 65 events loaded');

  release?.();
  await page.waitForTimeout(1500);
  expect(heldBack).toBe(1);
  await expect(eventsStatus(page)).toContainText('65 of 65 events loaded');
  await expect(eventsRegion(page)).not.toContainText('" 200 ');
  await expect(queryBox(page)).toHaveText('status=404');
});

test('an aggregation shows its table in place of timeline and events', async ({ page }) => {
  await openSearch(page);
  await run(page, 'url.path=/shop/* | stats count by http.response.status_code');
  const result = page.getByRole('region', { name: 'Result' });
  await expect(result.getByRole('row')).toHaveCount(3);
  await expect(result).toContainText('2 rows');
  await expect(result.getByRole('row').filter({ hasText: '404' })).toContainText('65');
  await expect(page.getByRole('region', { name: 'Timeline' })).toHaveCount(0);
});

test('an event opens from its row, and "only" writes its value into the query', async ({ page }) => {
  await openSearch(page, '?q=status%3D404');
  await eventsRegion(page).getByRole('button', { name: /Open the event of/ }).first().click();
  const detail = page.getByRole('complementary', { name: 'Event' });
  await expect(detail.getByLabel('Raw line', { exact: true })).toContainText('GET /shop/item/640 HTTP/1.1" 404');
  await expect(page).toHaveURL(/sel=\d+%3A\d+/);
  await expect(detail.getByRole('link', { name: 'paged.log' })).toBeVisible();
  await expect(detail).toContainText(/SHA-256 [0-9a-f]{10}…[0-9a-f]{4}/);

  // "/" takes the focus to the query from anywhere on the page.
  await page.keyboard.press('/');
  await expect(queryBox(page)).toBeFocused();
  await detail.getByRole('heading', { name: 'Event' }).click();

  // j and k walk the list; Escape closes.
  await page.keyboard.press('j');
  await expect(detail.getByLabel('Raw line', { exact: true })).toContainText('/shop/item/630 ');
  await page.keyboard.press('k');
  await expect(detail.getByLabel('Raw line', { exact: true })).toContainText('/shop/item/640 ');

  await detail.getByRole('button', { name: 'Only url.path' }).click();
  await expect(queryBox(page)).toHaveText('status=404 url.path="/shop/item/640"');
  await expect(eventsStatus(page)).toContainText('1 of 1 events loaded');
});

test('hostile lines are shown as they were written, and nothing in them runs', async ({ page, context }) => {
  test.setTimeout(120_000);
  await context.grantPermissions(['clipboard-write']);
  await openSearch(page, '?from=2026-11-08T10%3A00%3A00Z&to=2026-11-08T10%3A00%3A08Z');
  await expect(eventsStatus(page)).toContainText('7 of 7 events loaded');
  const list = eventsRegion(page);

  // Markup, a handler attribute and an address of code: text, character for character.
  for (const text of [HOSTILE.script, HOSTILE.img, HOSTILE.handler, HOSTILE.address]) {
    await expect(list.getByText(text, { exact: false }).first()).toBeVisible();
  }
  await list.getByText(HOSTILE.handler, { exact: false }).first().hover();
  await nothingRan(page);

  // An override of the direction, a NUL and an escape sequence are marks with their names, not effects.
  await expect(list.getByTitle('U+202E RIGHT-TO-LEFT OVERRIDE').first()).toHaveText('U+202E');
  await expect(list.getByTitle('U+2066 LEFT-TO-RIGHT ISOLATE').first()).toBeVisible();
  await expect(list.getByTitle('U+200B ZERO WIDTH SPACE').first()).toBeVisible();
  await expect(list.getByTitle('U+001B ESCAPE').first()).toHaveText('\u241b');
  await expect(list.getByTitle('U+0007 BEL').first()).toBeVisible();
  const bidiRow = list.getByRole('row').filter({ hasText: 'invoice' });
  await expect(bidiRow).toContainText('invoiceU+202Efdp.exe');

  // The detail of the script line: the raw line is the line.
  await list.getByRole('row').filter({ hasText: HOSTILE.script }).getByRole('button', { name: /Open the event/ }).click();
  const detail = page.getByRole('complementary', { name: 'Event' });
  await expect(detail.getByLabel('Raw line', { exact: true })).toHaveText(`2026-11-08T10:00:01+00:00 ${HOSTILE.script}`);
  await detail.getByRole('button', { name: 'Copy the whole raw line' }).click();
  expect(await copied(page)).toBe(`2026-11-08T10:00:01+00:00 ${HOSTILE.script}`);

  // Copy gives the original characters of the line with the override, not the marks.
  await bidiRow.getByRole('button', { name: /Open the event/ }).click();
  await expect(detail.getByLabel('Raw line', { exact: true })).toContainText('invoiceU+202Efdp.exe');
  await detail.getByRole('button', { name: 'Copy the whole raw line' }).click();
  expect(await copied(page)).toBe(`2026-11-08T10:00:05+00:00 ${HOSTILE.bidi}`);
  await nothingRan(page);
});

test('a line of a megabyte and an event of three thousand fields open at once and say what is cut', async ({ page, context }) => {
  test.setTimeout(120_000);
  await context.grantPermissions(['clipboard-write']);
  await openSearch(page, '?from=2026-11-08T10%3A00%3A09Z&to=2026-11-08T10%3A00%3A10Z');
  // The generic source stores a line of more than 64 KiB as two events: its first 65,536 bytes, and the rest
  // under the same time. The rest, 983,066 characters, is the longest value the page is given.
  await expect(eventsStatus(page)).toContainText('2 of 2 events loaded');
  const rest = MEGABYTE + 26 - 65_536;

  let started = Date.now();
  await eventsRegion(page).getByRole('button', { name: /Open the event/ }).first().click();
  const detail = page.getByRole('complementary', { name: 'Event' });
  await expect(detail.getByText('Showing the first 20,000 of 983,066 characters')).toBeVisible();
  expect(Date.now() - started, 'opening a line of a megabyte').toBeLessThan(3000);
  expect(await detail.getByLabel('Raw line', { exact: true }).evaluate((el) => el.textContent.length)).toBe(20_000);
  // The cell of the list holds four hundred characters of it.
  expect(await eventsRegion(page).locator('td.col-message').first().evaluate((el) => el.textContent.length)).toBeLessThan(410);

  await detail.getByRole('button', { name: 'Copy the whole raw line' }).click();
  expect((await copied(page)).length).toBe(rest);
  started = Date.now();
  await detail.getByRole('button', { name: 'Show all' }).click();
  expect(await detail.getByLabel('Raw line', { exact: true }).evaluate((el) => el.textContent.length)).toBe(rest);
  expect(Date.now() - started, 'drawing the whole line').toBeLessThan(5000);
  // The page still answers.
  await page.keyboard.press('Escape');
  await expect(detail).toHaveCount(0);

  // Three thousand fields: two hundred drawn, the rest by a step or by a part of a name.
  await page.goto(`${new URL(page.url()).pathname}?from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T11%3A00%3A01Z`);
  await expect(eventsStatus(page)).toContainText('1 of 1 events loaded');
  started = Date.now();
  await eventsRegion(page).getByRole('button', { name: /Open the event/ }).click();
  await expect(detail.getByText(/Showing 200 of 3,0\d\d fields/)).toBeVisible();
  expect(Date.now() - started, 'opening an event of three thousand fields').toBeLessThan(3000);
  await expect(detail.getByRole('rowheader')).toHaveCount(200);
  await detail.getByLabel('Part of a field name').fill('p2999');
  await expect(detail.getByRole('rowheader')).toHaveCount(1);
  await expect(detail.getByRole('row').filter({ hasText: 'p2999' })).toContainText('<b>v2999</b>');
  await nothingRan(page);
});

test('the fields beside the events show the values of the search, and a value narrows it', async ({ page }) => {
  await openSearch(page, '?q=status%3D404');
  await expect(eventsStatus(page)).toContainText('65 of 65 events loaded');
  const rail = page.getByRole('complementary', { name: 'Fields' });

  // A field the statistics keep values for: counted by the search itself.
  await rail.getByRole('button', { name: /^http\.request\.method/ }).click();
  await expect(rail.getByRole('button', { name: 'Only http.request.method get' })).toBeVisible();
  await expect(rail.getByRole('listitem').filter({ hasText: /^get/ })).toContainText('65');

  // A text field has none: its values are asked for when it is opened, with the search of the page.
  const asked = page.waitForRequest((request) => request.method() === 'POST' && request.url().endsWith('/search') && String(request.postData()).includes('stats count by url.path'));
  await rail.getByRole('button', { name: /^url\.path/ }).click();
  expect((await asked).postDataJSON()).toEqual({ query: 'status=404 | stats count by url.path | sort -count | head 10' });
  await expect(rail.getByRole('button', { name: /^Only url\.path \/shop\/item\// })).toHaveCount(10);

  await rail.getByRole('button', { name: /^Only url\.path \/shop\/item\// }).first().click();
  await expect(queryBox(page)).toHaveText(/^status=404 url\.path="\/shop\/item\/\d+"$/);
  await expect(eventsStatus(page)).toContainText('1 of 1 events loaded');
  await nothingRan(page);
});

test('the query box completes field names and, after a pipe, verbs', async ({ page }) => {
  await openSearch(page);
  await expect(eventsStatus(page)).toContainText(/200 of [\d,]+ events loaded/);
  await queryBox(page).click();
  await page.keyboard.type('http.resp');
  const options = page.getByRole('option');
  await expect(options.filter({ hasText: 'http.response.status_code' })).toBeVisible();
  await options.filter({ hasText: 'http.response.status_code' }).click();
  await expect(queryBox(page)).toHaveText('http.response.status_code');

  await page.keyboard.type('=404 | st');
  await expect(options.filter({ hasText: 'stats' })).toBeVisible();
  await expect(options.filter({ hasText: 'http.response.status_code' })).toHaveCount(0);
  await options.filter({ hasText: 'stats' }).click();
  await page.keyboard.type(' count by progr');
  await options.filter({ hasText: 'program' }).first().click();
  await expect(queryBox(page)).toHaveText('http.response.status_code=404 | stats count by program');

  // With the list closed, Enter runs the search.
  await page.keyboard.press('Enter');
  await expect(page.getByRole('region', { name: 'Result' })).toContainText('1 row');
  await nothingRan(page);
});

/** The bytes of the export written to a chosen file, kept for the test that downloads the same search. */
let exported = '';

test('an export is written to the file the reader chose as it arrives, and two exports are the same bytes', async ({ page }) => {
  // The browser of the tests has no dialog to answer: the file is one in the browser's own private file system,
  // written through the same stream a chosen file would be.
  await page.addInitScript(() => {
    (window as unknown as { showSaveFilePicker: unknown }).showSaveFilePicker = async (options: { suggestedName: string }) => {
      const root = await navigator.storage.getDirectory();
      return root.getFileHandle(options.suggestedName, { create: true });
    };
  });
  const stored = () =>
    page.evaluate(async () => {
      const root = await navigator.storage.getDirectory();
      const names: string[] = [];
      for await (const name of (root as unknown as { keys: () => AsyncIterable<string> }).keys()) {
        names.push(name);
      }
      const first = names[0];
      return first === undefined ? '' : (await (await root.getFileHandle(first)).getFile()).text();
    });

  await openSearch(page, '?q=status%3D404');
  await expect(eventsStatus(page)).toContainText('65 of 65 events loaded');
  await page.getByRole('button', { name: 'Export' }).click();
  await page.getByRole('menuitem', { name: 'CSV', exact: true }).click();
  await expect(page.getByText(/^Exported case-\d+-export\.csv, [\d.]+ KiB, 65 rows$/)).toBeVisible();

  exported = await stored();
  const lines = exported.trimEnd().split('\r\n');
  // A note of the sensitive columns, the header, and one line for each of the 65 events.
  expect(lines[0]).toMatch(/^# sensitive fields: /);
  // Every row with the file and offset it came from and its raw line.
  expect(lines[1]).toBe('ts,host,program,severity,source_ip,message,file_id,file_offset,raw');
  expect(lines).toHaveLength(67);
  expect(lines[2]).toMatch(/,\d+,\d+,"198\.51\.100\.\d+ - - \[07\/Nov\/2026:06:10:40 \+0000\] ""GET \/shop\/item\/640 HTTP\/1\.1"" 404 /);
  expect(lines[2]).toContain('404');

  await page.getByRole('button', { name: 'Export' }).click();
  await page.getByRole('menuitem', { name: 'CSV', exact: true }).click();
  await expect(page.getByText(/^Exported case-\d+-export\.csv/)).toBeVisible();
  expect(await stored()).toBe(exported);
  await nothingRan(page);
});

test('the brief form, and the files a result drew from with their hashes, are exports of their own', async ({ page }) => {
  await page.addInitScript(() => {
    (window as unknown as { showSaveFilePicker: unknown }).showSaveFilePicker = async (options: { suggestedName: string }) => {
      const root = await navigator.storage.getDirectory();
      return root.getFileHandle(options.suggestedName, { create: true });
    };
  });
  const stored = (name: string) =>
    page.evaluate(async (wanted) => {
      const root = await navigator.storage.getDirectory();
      return (await (await root.getFileHandle(wanted)).getFile()).text();
    }, name);

  await openSearch(page, '?q=status%3D404');
  await expect(eventsStatus(page)).toContainText('65 of 65 events loaded');
  const caseId = new URL(page.url()).pathname.split('/')[2] ?? '';

  await page.getByRole('button', { name: 'Export' }).click();
  await page.getByRole('menuitemcheckbox', { name: 'Brief columns' }).click();
  await page.getByRole('menuitem', { name: 'CSV, brief' }).click();
  await expect(page.getByText(/^Exported case-\d+-export\.csv, [\d.]+ KiB, 65 rows$/)).toBeVisible();
  expect((await stored(`case-${caseId}-export.csv`)).split('\r\n')[1]).toBe('ts,host,program,severity,source_ip,message');

  await page.getByRole('button', { name: 'Export' }).click();
  await page.getByRole('menuitem', { name: 'The files of this result, with their hashes (CSV)' }).click();
  await expect(page.getByText(/^Exported case-\d+-export-files\.csv/)).toBeVisible();
  const files = (await stored(`case-${caseId}-export-files.csv`)).trimEnd().split('\r\n');
  expect(files[0]).toBe('file_id,name,host,size,sha256,rows');
  // One file holds every 404 of the case: named, with its size, its SHA-256 at intake, and all 65 rows.
  expect(files).toHaveLength(2);
  expect(files[1]).toMatch(/^\d+,paged\.log,,\d+,[0-9a-f]{64},65$/);
  await nothingRan(page);
});

test('the bytes of a line are shown as hex and saved exactly, and the page says when the text is not the bytes', async ({ page }) => {
  await openSearch(page, '?from=2026-11-08T12%3A00%3A00Z&to=2026-11-08T12%3A00%3A02Z');
  await expect(eventsStatus(page)).toContainText('1 of 1 events loaded');
  await eventsRegion(page).getByRole('button', { name: /Open the event/ }).click();
  const detail = page.getByRole('complementary', { name: 'Event' });

  // The server, not a guess, says that this line's text is not its bytes.
  await expect(detail.getByRole('note')).toContainText('This line holds bytes that are not valid UTF-8');
  await expect(detail.getByLabel('Raw line', { exact: true })).toContainText('caf\uFFFD na\uFFFDve');

  await detail.getByRole('button', { name: 'Show the bytes' }).click();
  const hex = detail.getByLabel('Bytes of the line, as hex');
  // Sixteen bytes to a row: the letter that is not UTF-8 stands as its byte, and as a dot beside it.
  await expect(hex).toContainText('63 61 66 e9 20 6e');
  await expect(hex).toContainText('61 ef 76 65');
  await expect(hex).toContainText('|a.ve|');
  await expect(detail).toContainText(`${NOT_UTF8.length} bytes, as stored`);
  await expect(detail).toContainText(/SHA-256 [0-9a-f]{64}/);

  const download = page.waitForEvent('download');
  await detail.getByRole('button', { name: 'Save the bytes' }).click();
  const file = await download;
  expect(file.suggestedFilename()).toMatch(/^case-\d+-file-\d+-offset-0\.bin$/);
  expect(readFileSync(await file.path()).equals(NOT_UTF8)).toBe(true);

  // A line of plain text carries no such note.
  await page.goto(`${new URL(page.url()).pathname}?q=status%3D404`);
  await eventsRegion(page).getByRole('button', { name: /Open the event/ }).first().click();
  await expect(detail.getByLabel('Raw line', { exact: true })).toContainText('404');
  await expect(detail.getByRole('note')).toHaveCount(0);
  await nothingRan(page);
});

test('the pieces of a long line say which line they are, and the whole line is saved as one file', async ({ page }) => {
  test.setTimeout(120_000);
  await openSearch(page, '?from=2026-11-08T10%3A00%3A09Z&to=2026-11-08T10%3A00%3A10Z');
  await expect(eventsStatus(page)).toContainText('2 of 2 events loaded');
  await eventsRegion(page).getByRole('button', { name: /Open the event/ }).first().click();
  const detail = page.getByRole('complementary', { name: 'Event' });
  // The line begins after the 47 bytes of the line before it.
  await expect(detail.getByRole('note')).toContainText('Part 2 of 2 of one line that begins at offset 47 of this file');

  const download = page.waitForEvent('download');
  await detail.getByRole('button', { name: 'Save the whole line' }).click();
  const file = await download;
  await expect(detail.getByRole('note')).toContainText('Saved 1,048,602 bytes from 2 parts.');
  const whole = readFileSync(await file.path());
  expect(whole.length).toBe(MEGABYTE + 26);
  expect(whole.equals(Buffer.concat([Buffer.from('2026-11-08T10:00:09+00:00 '), Buffer.alloc(MEGABYTE, 'A')]))).toBe(true);

  await detail.getByRole('button', { name: 'Show the parts of this line' }).click();
  await expect(queryBox(page)).toHaveText(/^file\.id=\d+ line\.first_offset="47" \| sort file\.offset$/);
  await expect(eventsStatus(page)).toContainText('2 of 2 events loaded');
  await nothingRan(page);
});

test('a browser that cannot write a chosen file gets the same export as a download from the page', async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(window, 'showSaveFilePicker', { value: undefined });
  });
  await openSearch(page, '?q=status%3D404');
  await expect(eventsStatus(page)).toContainText('65 of 65 events loaded');
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export' }).click();
  await page.getByRole('menuitem', { name: 'JSON lines' }).click();
  const file = await download;
  expect(file.suggestedFilename()).toMatch(/^case-\d+-export\.jsonl$/);
  await expect(page.getByText(/^Exported case-\d+-export\.jsonl, [\d.]+ KiB, 65 rows$/)).toBeVisible();
  const text = readFileSync(await file.path(), 'utf8');
  expect(text.trimEnd().split('\n')).toHaveLength(66);

  // The same search as CSV is, byte for byte, what the other way wrote.
  const second = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export' }).click();
  await page.getByRole('menuitem', { name: 'CSV', exact: true }).click();
  expect(readFileSync(await (await second).path(), 'utf8')).toBe(exported);
  await nothingRan(page);
});

test('someone the case is not shared with gets "no such case" on its search address, and no event', async ({ page }) => {
  await openSearch(page);
  const address = new URL(page.url()).pathname;
  await page.getByRole('button', { name: /Signed in as/ }).click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();

  const searches: number[] = [];
  page.on('response', (response) => {
    if (response.url().endsWith('/search') && response.request().method() === 'POST') {
      searches.push(response.status());
    }
  });
  await signIn(page, accounts.other());
  await expect(page.getByRole('heading', { name: 'Cases' })).toBeVisible();
  await page.goto(`${address}?q=status%3D404`);
  await expect(page.getByRole('heading', { name: 'No such case' })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Events' })).toHaveCount(0);
  expect(searches.filter((status) => status === 200)).toEqual([]);
});
