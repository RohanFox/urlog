import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { setCsrfToken } from '../api/http';
import samples from '../api/samples/search-frames.json';
import { App } from '../app/App';
import type * as time from '../search/time';
import { dashboard, events, fakeServer, fileRecord, problem, session, sse } from '../test-server';

// The editor and the chart draw through browser APIs the test document does not have. Each is replaced by a
// stand-in with the same contract; what they draw is covered by the browser tests.
vi.mock('../search/Editor', () => ({
  default: ({ value, marks, label, onChange, onRun }: { value: string; marks: ReadonlyArray<{ kind: string; offset: number; length: number }>; label: string; onChange: (v: string) => void; onRun: (text: string) => void }) => (
    <div>
      <textarea
        aria-label={label}
        value={value}
        onChange={(event) => {
          onChange(event.target.value);
        }}
        onKeyDown={(event) => {
          if (event.key === 'Enter') {
            event.preventDefault();
            onRun(event.currentTarget.value);
          }
        }}
      />
      <ul aria-label="Marks">
        {marks.map((mark, i) => (
          <li key={i}>
            {mark.kind} {mark.offset}+{mark.length}
          </li>
        ))}
      </ul>
    </div>
  ),
}));

vi.mock('../search/Timeline', () => ({
  default: ({ histogram, from, to, collapsed, range, toggle, onSelect }: { histogram: { total: number; span: string }; from: string; to: string; collapsed: boolean; range: ReactNode; toggle: ReactNode; onSelect: (from: string, to: string) => void }) => (
    <div>
      {collapsed ? null : (
        <>
          <span>
            timeline of {histogram.total} in {histogram.span} over {from} to {to}
          </span>
          <button
            type="button"
            onClick={() => {
              onSelect('2026-03-01T00:00:01.000Z', '2026-03-01T00:00:03.000Z');
            }}
          >
            select a stretch
          </button>
        </>
      )}
      {range}
      {toggle}
    </div>
  ),
}));

// The zone of the machine that runs the tests is not part of them.
vi.mock('../search/time', async (original) => ({ ...(await original<typeof time>()), browserZone: () => 'Europe/Bucharest' }));

const SEARCH = 'POST /api/v1/cases/7/search';
const COLUMNS = (samples.listing[3]?.data as { columns: string[] }).columns;

interface Event {
  time?: string;
  host?: string;
  program?: string;
  level?: string;
  message?: string;
  raw?: string;
  user?: string;
  status?: number;
  file?: number;
  offset?: number;
  extras?: Record<string, string>;
  /** What the server says of the line's bytes: 0 when its text holds replacements. */
  valid?: number;
}

function row(e: Event) {
  const values: Record<string, unknown> = {
    _time: e.time ?? '2026-03-01T00:00:05.000Z',
    'host.name': e.host ?? 'web-01',
    program: e.program ?? 'nginx',
    'log.level': e.level ?? 'info',
    'user.name': e.user ?? '',
    'source.ip': '10.0.0.5',
    'destination.ip': '::',
    'http.response.status_code': e.status ?? 0,
    message: e.message ?? 'GET /index.html 200',
    raw: e.raw ?? e.message ?? '10.0.0.5 - - "GET /index.html HTTP/1.1" 200 512',
    'file.id': e.file ?? 11,
    'file.offset': e.offset ?? 100,
    'host.id': 3,
    extras: e.extras ?? {},
    'raw.valid_utf8': e.valid ?? 1,
  };
  return COLUMNS.map((name) => values[name] ?? (name.endsWith('.pid') || name.endsWith('.code') || name.endsWith('.bytes') ? 0 : ''));
}

interface Listing {
  rows?: Event[];
  total?: number;
  next?: string | null;
  capped?: boolean;
  warnings?: unknown[];
  span?: Record<string, unknown>;
  buckets?: unknown[];
}

const META = { kind: 'meta', from: '2026-03-01T00:00:00.000Z', to: '2026-03-01T00:00:05.001Z', tz: 'UTC', result: 'events', columns: [], warnings: [], fields: [], span: { from: '2026-03-01T00:00:00.000Z', to: '2026-03-01T00:00:05.000Z', undated_events: 0, future_events: 0 } };

function frames(l: Listing = {}) {
  const rows = (l.rows ?? [{ offset: 100 }, { offset: 200, message: 'GET /admin 404', level: 'error' }]).map(row);
  return {
    meta: { event: 'meta', data: { ...META, warnings: l.warnings ?? [], span: l.span ?? META.span } },
    histogram: { event: 'histogram', data: { kind: 'histogram', span: '1s', source: 'query', total: l.total ?? rows.length, buckets: l.buckets ?? [{ start: '2026-03-01T00:00:05.000Z', counts: { info: 1, error: 1 } }] } },
    page: { event: 'page', data: { kind: 'page', first_row: 0, columns: COLUMNS, rows, next: l.next ?? null, capped: l.capped ?? false, ...(l.capped === true ? { message: 'narrow the search or the time range: a result is paged to 10,000 events' } : {}) } },
    counts: { event: 'counts', data: { kind: 'counts', total: l.total ?? rows.length, source: 'query', fields: [{ name: 'program', top: [{ value: 'nginx', count: rows.length }] }] } },
    done: { event: 'done', data: { kind: 'done', duration_ms: 42 } },
  };
}

function listing(l: Listing = {}) {
  const f = frames(l);
  return events([f.meta, f.histogram, f.page, f.counts, f.done]);
}

const BASE = {
  'GET /api/v1/auth/me': { status: 200, body: session },
  'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard() },
  'GET /api/v1/cases/7/fields': { status: 200, body: { case_id: 7, span: META.span, statistics: { state: 'ok', pending: false, computed_at: null, events: 2, reason: null }, fields: [{ name: 'user.name', type: 'keyword', description: '', aliases: [], sensitive: true, zero_is_absent: false, events: 1, distinct_estimate: 1, top: null }, { name: 'program', type: 'keyword', description: '', aliases: [], sensitive: false, zero_is_absent: false, events: 2, distinct_estimate: 1, top: [{ value: 'nginx', count: 2 }] }, { name: 'http.response.status_code', type: 'int', description: '', aliases: ['status'], sensitive: false, zero_is_absent: true, events: 2, distinct_estimate: 2, top: [] }, { name: 'process.pid', type: 'int', description: '', aliases: [], sensitive: false, zero_is_absent: true, events: 0, distinct_estimate: 0, top: [] }, { name: 'http.response.body.bytes', type: 'int', description: '', aliases: [], sensitive: false, zero_is_absent: false, events: 2, distinct_estimate: 2, top: [] }], extras_keys: [], verbs: ['stats', 'sort', 'head'] } },
  'GET /api/v1/cases/7/files/11': { status: 200, body: { file: fileRecord(), detection_record: null, seen: null, unreadable_reason: null, stated_size: null, truncated_decoded_offset: null, truncated_stored_offset: null, trailing_bytes: 0 } },
};

function open(routes: Parameters<typeof fakeServer>[0], address = '/cases/7/search') {
  const server = fakeServer({ ...BASE, ...routes });
  window.history.replaceState(null, '', address);
  render(<App />);
  return server;
}

/** A body the test feeds and ends by hand. */
function held() {
  const encoder = new TextEncoder();
  let controller: ReadableStreamDefaultController<Uint8Array> | undefined;
  const stream = new ReadableStream<Uint8Array>({
    start(c) {
      controller = c;
    },
  });
  return {
    stream,
    send(...list: Array<{ event: string; data: unknown }>) {
      controller?.enqueue(encoder.encode(sse(list)));
    },
    end() {
      controller?.close();
    },
  };
}

beforeAll(() => {
  // The list draws the rows in view; the test document has no layout, so it is given a view of 600 pixels.
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', { configurable: true, get: () => 600 });
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, get: () => 1200 });
});

afterEach(() => {
  setCsrfToken(undefined);
  vi.unstubAllGlobals();
  window.localStorage.clear();
  delete (window as unknown as { pwned?: number }).pwned;
});

const region = (name: string) => screen.getByRole('region', { name });
const rangeButton = () => screen.getByRole('button', { name: /^Time range: / });

describe('the search page', () => {
  it('runs the address: the whole case with no query, and draws timeline, events, count and duration', async () => {
    const server = open({ [SEARCH]: listing({ total: 6 }) });

    expect(await screen.findByText(/timeline of 6 in 1s/)).toBeInTheDocument();
    const list = region('Events');
    expect(await within(list).findByText('GET /admin 404')).toBeInTheDocument();
    expect(within(list).getByRole('status')).toHaveTextContent('2 of 6 events loaded');
    expect(await screen.findByText(/Finished in 42 ms/)).toBeInTheDocument();
    expect(screen.getByText(/Searched/)).toHaveTextContent('Searched 2026-03-01 00:00:00 to 2026-03-01 00:00:05 UTC');
    expect(within(list).getByText('End of the results, 2 events')).toBeInTheDocument();

    // No query, no range, no zone: the server's defaults, with the CSRF token of the session.
    const sent = server.calls(SEARCH);
    expect(sent).toHaveLength(1);
    expect(sent[0]?.body).toEqual({ query: '' });
    expect(sent[0]?.headers['X-CSRF-Token']).toBe('token-1');

    // The header leads to the two pages of the case.
    expect(screen.getByRole('link', { name: 'Evidence' })).toHaveAttribute('href', '/cases/7');
    expect(screen.getByRole('link', { name: 'Search' })).toHaveAttribute('aria-current', 'page');
  });

  it('runs what the address holds: query, range and zone', async () => {
    const server = open({ [SEARCH]: listing() }, '/cases/7/search?q=status%3D404&from=-7d%40d&to=now&tz=Europe%2FBucharest');
    await screen.findByText('GET /admin 404');
    expect(server.calls(SEARCH)[0]?.body).toEqual({ query: 'status=404', from: '-7d@d', to: 'now', tz: 'Europe/Bucharest' });
    expect(screen.getByLabelText('Query')).toHaveValue('status=404');
    // Times are written in the zone of the address.
    expect(within(region('Events')).getAllByRole('button', { name: /Open the event of 2026-03-01 02:00:05.000/ })).toHaveLength(2);
  });

  it('shows the warnings of the server at their place in the text', async () => {
    open({ [SEARCH]: listing({ warnings: (samples.listing[0]?.data as { warnings: unknown[] }).warnings }) }, '/cases/7/search?q=nosuchfield%3D1');
    expect(await screen.findByText(/At character 1: 'nosuchfield' is not in the catalog/)).toBeInTheDocument();
    expect(within(screen.getByRole('list', { name: 'Marks' })).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['warning 0+11', 'warning 0+13']);
  });

  it('marks a syntax error where the server found it and keeps the results of the search before', async () => {
    const server = open({ [SEARCH]: listing() });
    const user = userEvent.setup();
    await screen.findByText('GET /admin 404');

    server.set(SEARCH, { status: 400, body: samples.refusals.query_syntax.body });
    await user.type(screen.getByLabelText('Query'), 'host=web-01 | eval x=1{Enter}');

    expect(await screen.findByRole('alert')).toHaveTextContent('At character 15: not supported in v1: eval');
    expect(within(screen.getByRole('list', { name: 'Marks' })).getByRole('listitem')).toHaveTextContent('error 14+4');
    // Nothing is thrown away for a typing mistake.
    expect(screen.getByText(/the results of the search before/)).toHaveTextContent('every event · the whole case');
    expect(screen.getByText('GET /admin 404')).toBeInTheDocument();
    // With their count: they are an answer that was complete.
    expect(within(region('Events')).getByText('2 of 2 events loaded')).toBeInTheDocument();
    expect(window.location.search).toBe('?q=host%3Dweb-01+%7C+eval+x%3D1');
  });

  it('marks the end of the range the server could not read, in its words, and runs nothing', async () => {
    open({ [SEARCH]: { status: 400, body: samples.refusals.bad_request.body } }, '/cases/7/search?from=yesterday');
    const from = await screen.findByLabelText('From');
    await waitFor(() => {
      expect(from).toBeInvalid();
    });
    expect(from).toHaveAccessibleDescription(/'yesterday' is not a time: use -24h/);
    expect(screen.getByLabelText('To')).toBeValid();
    expect(screen.queryByRole('region', { name: 'Events' })?.textContent ?? '').not.toContain('GET');
  });

  it('says "busy, retrying" for a 429, neutrally, and goes on by itself', async () => {
    let asked = 0;
    open({
      [SEARCH]: () => {
        asked += 1;
        return asked === 1 ? { status: 429, body: samples.refusals.busy.body, headers: { 'Retry-After': '1' } } : listing();
      },
    });
    const notice = await screen.findByText('The server is busy, retrying in 1 s');
    expect(notice.closest('[role="status"]')).not.toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.getByText(/try 1 of 5/)).toBeInTheDocument();

    expect(await screen.findByText('GET /admin 404', undefined, { timeout: 4000 })).toBeInTheDocument();
    expect(screen.queryByText(/The server is busy/)).toBeNull();
    expect(asked).toBe(2);
  });

  it('says where a listing was capped, with the number loaded and the words of the server', async () => {
    open({ [SEARCH]: listing({ capped: true, total: 5_000_000 }) });
    const foot = await screen.findByText(/Showing the first 2 events. Narrow the search or export./);
    expect(foot).toHaveTextContent('a result is paged to 10,000 events');
    // A cut the server made is said as a fact of the answer, not as an error.
    expect(foot).toHaveAttribute('role', 'status');
    expect(screen.queryByText(/End of the results/)).toBeNull();
  });

  it('asks for the next page as the list nears its end, and says so when that page is refused', async () => {
    let asked = 0;
    const server = open({
      [SEARCH]: () => {
        asked += 1;
        if (asked === 1) {
          return listing({ next: 'CURSOR-1', total: 4 });
        }
        return asked === 2 ? { status: 504, body: { type: 'https://urlog/problems/query-timeout', title: 'the query passed its time limit', status: 504, code: 'query_timeout' } } : events(samples.page_at_the_limit);
      },
    });
    const user = userEvent.setup();

    expect(await screen.findByText(/The next events could not be read: the query passed its time limit/)).toBeInTheDocument();
    expect(server.calls(SEARCH)[1]?.body).toEqual({ query: '', cursor: 'CURSOR-1' });
    // The rows already loaded stay.
    expect(screen.getByText('GET /admin 404')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Try this page again' }));
    expect(await screen.findByText('4 of 4 events loaded')).toBeInTheDocument();
  });

  it('shows an aggregation as its table, and says what the server cut', async () => {
    open({ [SEARCH]: events(samples.aggregation) }, '/cases/7/search?q=%7C+stats+count+by+host');
    const result = await screen.findByRole('region', { name: 'Result' });
    expect(await within(result).findByText('web-02')).toBeInTheDocument();
    expect(within(result).getByText('2 of 3 rows shown. Export for all of them.')).toBeInTheDocument();
    expect(within(result).getByText(/1 row has a list of values capped at 100/)).toBeInTheDocument();
    expect(within(result).getAllByText('values capped at 100')).toHaveLength(1);
    expect(within(result).getAllByRole('columnheader').map((th) => th.textContent)).toEqual(['host.name', 'count', 'Cut']);
    expect(screen.queryByRole('region', { name: 'Timeline' })).toBeNull();
  });

  it('keeps the parts that arrived when the stream fails, marks them, and says which part is missing and why', async () => {
    const f = frames();
    open({ [SEARCH]: events([f.meta, f.histogram, { event: 'error', data: { kind: 'error', code: 'query_timeout', message: 'Timeout exceeded: elapsed 60.0 seconds' } }]) });

    const list = await screen.findByRole('region', { name: 'Events' });
    expect(await within(list).findByText('The events could not be listed')).toBeInTheDocument();
    expect(within(list).getByRole('alert')).toHaveTextContent('The search passed its time limit. Timeout exceeded: elapsed 60.0 seconds. Narrow the time range or the query. (query_timeout)');
    expect(within(list).getByRole('button', { name: 'Run again' })).toBeInTheDocument();
    const timeline = region('Timeline');
    expect(within(timeline).getByText(/timeline of 2/)).toBeInTheDocument();
    expect(within(timeline).getByText('from an incomplete run')).toBeInTheDocument();
  });

  it('says that the connection was lost when the stream ends without its closing event', async () => {
    const f = frames();
    const server = open({ [SEARCH]: events([f.meta, f.page]) });
    const user = userEvent.setup();

    const timeline = await screen.findByRole('region', { name: 'Timeline' });
    expect(await within(timeline).findByRole('alert')).toHaveTextContent('The connection was lost before the search finished');
    expect(within(region('Events')).getByText('from an incomplete run')).toBeInTheDocument();
    expect(screen.getByText('GET /admin 404')).toBeInTheDocument();

    server.set(SEARCH, listing());
    await user.click(within(timeline).getByRole('button', { name: 'Run again' }));
    expect(await screen.findByText(/timeline of 2/)).toBeInTheDocument();
    expect(screen.queryByText('from an incomplete run')).toBeNull();
  });

  it('says that nothing matches, with what was searched, and invents no row', async () => {
    open({ [SEARCH]: listing({ rows: [], buckets: [] }) }, '/cases/7/search?q=nothing');
    expect(await screen.findByRole('heading', { name: 'No events match' })).toBeInTheDocument();
    expect(screen.getByText(/Searched nothing · the whole case. The case holds 1 file./)).toBeInTheDocument();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('draws nothing of a search that was replaced while it was in flight', async () => {
    const first = held();
    let asked = 0;
    open({
      [SEARCH]: () => {
        asked += 1;
        return asked === 1 ? { status: 200, stream: first.stream } : listing({ rows: [{ message: 'the second search', offset: 900 }] });
      },
    });
    const user = userEvent.setup();
    const f = frames({ rows: [{ message: 'the first search', offset: 800 }] });
    first.send(f.meta);
    await screen.findByText(/Searched/);

    await user.type(screen.getByLabelText('Query'), 'second{Enter}');
    expect(await screen.findByText('the second search')).toBeInTheDocument();

    // The frames of the first search arrive after it was replaced.
    first.send(f.histogram, f.page, f.counts, f.done);
    first.end();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByText('the first search')).toBeNull();
    expect(screen.getByText('the second search')).toBeInTheDocument();
    expect(asked).toBe(2);
  });

  it('keeps each part of the search before, dimmed and named, until the new search has sent that part', async () => {
    const second = held();
    let asked = 0;
    open({
      [SEARCH]: () => {
        asked += 1;
        return asked === 1 ? listing({ rows: [{ message: 'from the first search', offset: 800 }], total: 1 }) : { status: 200, stream: second.stream };
      },
    });
    const user = userEvent.setup();
    await screen.findByText('from the first search');

    await user.type(screen.getByLabelText('Query'), 'second{Enter}');
    const f = frames({ rows: [{ message: 'from the second search', offset: 900 }], total: 9 });
    second.send(f.meta, f.histogram);

    // The timeline is the new search's; the events are still the old ones, and say so.
    expect(await screen.findByText(/timeline of 9 in 1s/)).toBeInTheDocument();
    expect(region('Timeline')).toHaveAttribute('aria-busy', 'false');
    expect(region('Events')).toHaveAttribute('aria-busy', 'true');
    expect(region('Events')).toHaveClass('region-stale');
    expect(screen.getByText('from the first search')).toBeInTheDocument();
    expect(screen.getByText(/Searching. Below, dimmed: the results of/)).toHaveTextContent('every event · the whole case');

    second.send(f.page, f.counts, f.done);
    second.end();
    expect(await screen.findByText('from the second search')).toBeInTheDocument();
    expect(region('Events')).toHaveAttribute('aria-busy', 'false');
    expect(screen.queryByText('from the first search')).toBeNull();
    expect(screen.queryByText(/the results of/)).toBeNull();
  });

  it('narrows the range from the timeline and from the counts of the events a default range leaves out', async () => {
    const server = open({ [SEARCH]: listing({ span: { ...META.span, undated_events: 12, future_events: 1 } }) });
    const user = userEvent.setup();
    await screen.findByText('GET /admin 404');

    await user.click(screen.getByRole('button', { name: '12 events without a usable time' }));
    await waitFor(() => {
      expect(server.calls(SEARCH).at(-1)?.body).toEqual({ query: '', from: '1970-01-01T00:00:00Z', to: '1980-01-01T00:00:00Z' });
    });
    await user.click(rangeButton());
    expect(screen.getByLabelText('From')).toHaveValue('1970-01-01T00:00:00Z');
    await user.keyboard('{Escape}');

    await user.click(await screen.findByRole('button', { name: '1 event dated after their file arrived' }));
    await waitFor(() => {
      expect(server.calls(SEARCH).at(-1)?.body).toEqual({ query: '', from: '2026-03-01T00:00:05.001Z', to: '9999-12-31T23:59:59Z' });
    });

    await user.click(await screen.findByRole('button', { name: 'select a stretch' }));
    await waitFor(() => {
      expect(server.calls(SEARCH).at(-1)?.body).toEqual({ query: '', from: '2026-03-01T00:00:01.000Z', to: '2026-03-01T00:00:03.000Z' });
    });
    expect(window.location.search).toContain('from=2026-03-01T00%3A00%3A01.000Z');
  });

  it('opens an event from the row it has: raw line, fields with a value, the file it came from', async () => {
    const server = open({ [SEARCH]: listing({ rows: [{ offset: 100, user: 'root', status: 404, raw: '10.0.0.5 - root "GET /admin" 404', extras: { 'log.syslog.msgid': 'ID47' } }] }) });
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: /Open the event of 2026-03-01 00:00:05.000/ }));
    const detail = await screen.findByRole('complementary', { name: 'Event' });
    expect(within(detail).getByLabelText('Raw line')).toHaveTextContent('10.0.0.5 - root "GET /admin" 404');
    expect(window.location.search).toBe('?sel=11%3A100');

    // A field without a value is not listed; the unspecified address and the empty text are "no value".
    const names = within(detail).getAllByRole('rowheader').map((th) => th.textContent);
    expect(names).toContain('user.name sensitive');
    expect(names).toContain('http.response.status_code');
    expect(names).toContain('log.syslog.msgid');
    expect(names).not.toContain('destination.ip');
    expect(names).not.toContain('url.path');

    // The name and the hash of the file come from its record.
    expect(await within(detail).findByRole('link', { name: 'access.log' })).toHaveAttribute('href', '/cases/7');
    expect(detail).toHaveTextContent('offset 100');
    expect(detail).toHaveTextContent('SHA-256 a3f1c29b7d…e07b');
    // Opening an event asked the server for the file record and for nothing else.
    expect(server.calls(SEARCH)).toHaveLength(1);

    await user.click(within(detail).getByRole('button', { name: 'Only user.name' }));
    await waitFor(() => {
      expect(server.calls(SEARCH).at(-1)?.body).toEqual({ query: 'user.name="root"' });
    });
    expect(screen.getByLabelText('Query')).toHaveValue('user.name="root"');
  });

  it('shows a field as a column, three at most, and takes it away again', async () => {
    open({ [SEARCH]: listing({ rows: [{ offset: 100, user: 'root' }] }) }, '/cases/7/search?sel=11:100');
    const user = userEvent.setup();
    const detail = await screen.findByRole('complementary', { name: 'Event' });

    await user.click(within(detail).getByRole('button', { name: 'Show user.name as a column' }));
    expect(await within(region('Events')).findByRole('columnheader', { name: /user.name/ })).toBeInTheDocument();
    expect(window.location.search).toContain('cols=user.name');
    expect(within(detail).getByRole('button', { name: 'Show user.name as a column' })).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Remove the column user.name' }));
    expect(window.location.search).not.toContain('cols=');
  });

  it('says so when the address names an event the list does not hold', async () => {
    open({ [SEARCH]: listing() }, '/cases/7/search?sel=99:1');
    expect(await screen.findByText('The selected event is not on the loaded pages')).toBeInTheDocument();
    expect(screen.getByText('GET /admin 404')).toBeInTheDocument();
  });

  it('shows the event with its file id when the file record cannot be read', async () => {
    open({ [SEARCH]: listing(), 'GET /api/v1/cases/7/files/11': problem(404, 'not-found', 'no such file') }, '/cases/7/search?sel=11:100');
    const detail = await screen.findByRole('complementary', { name: 'Event' });
    expect(await within(detail).findByText(/the file record could not be read/)).toBeInTheDocument();
    expect(detail).toHaveTextContent('file 11, offset 100');
    expect(within(detail).getByLabelText('Raw line')).toBeInTheDocument();
  });

  it('offers the presets of the time grammar and the zone of the browser, each a new search', async () => {
    const server = open({ [SEARCH]: listing() });
    const user = userEvent.setup();
    await screen.findByText('GET /admin 404');

    // One button says the range and the zone; its panel holds the presets, From, To and the zone.
    expect(rangeButton()).toHaveTextContent('Whole case · UTC');
    expect(rangeButton()).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByLabelText('Time range')).toBeNull();
    await user.click(rangeButton());
    expect(rangeButton()).toHaveAttribute('aria-expanded', 'true');
    await user.selectOptions(screen.getByLabelText('Time range'), 'Last 24 hours');
    await waitFor(() => {
      expect(server.calls(SEARCH).at(-1)?.body).toEqual({ query: '', from: '-24h' });
    });
    // A preset is a search: the panel closes and the button says what is searched now.
    expect(screen.queryByLabelText('Time range')).toBeNull();
    expect(rangeButton()).toHaveTextContent('Last 24 hours · UTC');

    await user.click(rangeButton());
    expect(screen.getByLabelText('Time range')).toHaveValue('-24h');
    await user.click(screen.getByRole('button', { name: 'Europe/Bucharest' }));
    await waitFor(() => {
      expect(server.calls(SEARCH).at(-1)?.body).toEqual({ query: '', from: '-24h', tz: 'Europe/Bucharest' });
    });
    expect(screen.getByRole('button', { name: 'Europe/Bucharest' })).toHaveAttribute('aria-pressed', 'true');
    expect(rangeButton()).toHaveTextContent('Last 24 hours · Europe/Bucharest');
    expect(await screen.findAllByRole('button', { name: /Open the event of 2026-03-01 02:00:05.000/ })).toHaveLength(2);

    await user.clear(screen.getByLabelText('From'));
    await user.type(screen.getByLabelText('From'), '2026-03-01T00:00:00Z');
    await user.type(screen.getByLabelText('To'), '2026-03-01T00:00:03Z');
    await user.click(screen.getByRole('button', { name: 'Apply' }));
    await waitFor(() => {
      expect(server.calls(SEARCH).at(-1)?.body).toEqual({ query: '', from: '2026-03-01T00:00:00Z', to: '2026-03-01T00:00:03Z', tz: 'Europe/Bucharest' });
    });
    expect(rangeButton()).toHaveTextContent('2026-03-01T00:00:00Z to 2026-03-01T00:00:03Z · Europe/Bucharest');
    await user.click(rangeButton());
    expect(screen.getByLabelText('Time range')).toHaveValue('custom');
  });

  it('closes the range panel with Escape and leaves the open event open', async () => {
    open({ [SEARCH]: listing({ rows: [{ offset: 100 }] }) }, '/cases/7/search?sel=11:100');
    const user = userEvent.setup();
    await screen.findByRole('complementary', { name: 'Event' });

    await user.click(rangeButton());
    screen.getByLabelText('From').focus();
    await user.keyboard('{Escape}');
    expect(screen.queryByLabelText('From')).toBeNull();
    expect(rangeButton()).toHaveFocus();
    expect(screen.getByRole('complementary', { name: 'Event' })).toBeInTheDocument();
    expect(window.location.search).toBe('?sel=11:100');

    // A press outside closes it too.
    await user.click(rangeButton());
    await user.click(within(region('Events')).getByText(/events loaded/));
    expect(screen.queryByLabelText('From')).toBeNull();
  });

  it('puts the case in the top bar, the export in the head of the events, and the case facts at the foot of the fields', async () => {
    open({ [SEARCH]: listing() });
    await screen.findByText('GET /admin 404');
    const header = screen.getByRole('banner');
    expect(within(header).getByText('payroll portal')).toBeInTheDocument();
    expect(within(header).getByText('Active')).toBeInTheDocument();
    // The page has no head of its own: the top bar names the case.
    expect(screen.queryByRole('heading', { level: 1, name: 'payroll portal' })).toBeNull();
    expect(within(region('Events')).getByRole('button', { name: 'Export' })).toBeInTheDocument();
    const rail = screen.getByRole('complementary', { name: 'Fields' });
    expect(await within(rail).findByText(/Finished in 42 ms · 1 file · 1 host · your role: Owner/)).toBeInTheDocument();
  });

  it('folds the timeline to its caption and keeps the fold in this browser', async () => {
    open({ [SEARCH]: listing({ total: 6 }) });
    const user = userEvent.setup();
    expect(await screen.findByText(/timeline of 6 in 1s/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Hide the timeline' }));
    expect(screen.queryByText(/timeline of 6/)).toBeNull();
    // The caption stays: what was searched, and the way back.
    expect(within(region('Timeline')).getByText(/Searched/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show the timeline' })).toHaveAttribute('aria-expanded', 'false');
    expect(window.localStorage.getItem('urlog.search.timeline')).toBe('folded');
    // Not in the address: a shared address does not fold anyone's timeline.
    expect(window.location.search).toBe('');

    await user.click(screen.getByRole('button', { name: 'Show the timeline' }));
    expect(await screen.findByText(/timeline of 6 in 1s/)).toBeInTheDocument();
    expect(window.localStorage.getItem('urlog.search.timeline')).toBeNull();
  });

  it('folds the fields to a strip with one button, keeps the fold, and lets an open event fold them only while it is open', async () => {
    open({ [SEARCH]: listing() });
    const user = userEvent.setup();
    await screen.findByText('GET /admin 404');
    const panes = () => region('Events').parentElement as HTMLElement;

    await user.click(screen.getByRole('button', { name: 'Hide the fields' }));
    expect(panes()).toHaveClass('panes-folded');
    expect(window.localStorage.getItem('urlog.search.fields')).toBe('folded');
    await user.click(screen.getByRole('button', { name: 'Show the fields' }));
    expect(panes()).not.toHaveClass('panes-folded');
    expect(window.localStorage.getItem('urlog.search.fields')).toBeNull();

    // An open event asks for the room of the fields (the stylesheet gives it from 1024 to 1599 pixels).
    await user.click(screen.getAllByRole('button', { name: /Open the event of/ })[0] as HTMLElement);
    expect(panes()).toHaveClass('panes-for-event');
    // The reader may take the fields back while the event is open; that is not kept.
    await user.click(screen.getByRole('button', { name: 'Show the fields' }));
    expect(panes()).not.toHaveClass('panes-for-event');
    expect(window.localStorage.getItem('urlog.search.fields')).toBeNull();
    // Closing the event ends it, and the next event asks again.
    await user.keyboard('{Escape}');
    expect(panes()).not.toHaveClass('panes-for-event');
    await user.keyboard('j');
    expect(panes()).toHaveClass('panes-for-event');
  });

  it('opens with the fields folded when this browser folded them before', async () => {
    window.localStorage.setItem('urlog.search.fields', 'folded');
    open({ [SEARCH]: listing() });
    await screen.findByText('GET /admin 404');
    expect(region('Events').parentElement).toHaveClass('panes-folded');
  });

  it('opens with the timeline folded when this browser folded it before', async () => {
    window.localStorage.setItem('urlog.search.timeline', 'folded');
    open({ [SEARCH]: listing({ total: 6 }) });
    expect(await screen.findByText('GET /admin 404')).toBeInTheDocument();
    expect(screen.queryByText(/timeline of 6/)).toBeNull();
    expect(screen.getByRole('button', { name: 'Show the timeline' })).toBeInTheDocument();
  });

  it('fills the column of the detail, while no event is open, with the keys and the search', async () => {
    open({ [SEARCH]: listing({ total: 6 }) }, '/cases/7/search?q=status%3D404');
    const user = userEvent.setup();
    await screen.findByText('GET /admin 404');
    const empty = screen.getByRole('complementary', { name: 'No event open' });
    expect(within(empty).getByText('j')).toBeInTheDocument();
    expect(within(empty).getByText('k')).toBeInTheDocument();
    expect(within(empty).getByText('Enter')).toBeInTheDocument();
    expect(within(empty).getByText('Esc')).toBeInTheDocument();
    expect(empty).toHaveTextContent('Querystatus=404');
    expect(empty).toHaveTextContent('Range2026-03-01 00:00:00 to 2026-03-01 00:00:05 UTC');
    expect(await within(empty).findByText('6 match, 2 loaded')).toBeInTheDocument();
    expect(empty).toHaveTextContent('complete, 42 ms');

    await user.click(screen.getAllByRole('button', { name: /Open the event of/ })[0] as HTMLElement);
    expect(await screen.findByRole('complementary', { name: 'Event' })).toBeInTheDocument();
    expect(screen.queryByRole('complementary', { name: 'No event open' })).toBeNull();
    await user.keyboard('{Escape}');
    expect(await screen.findByRole('complementary', { name: 'No event open' })).toBeInTheDocument();
  });

  it('counts a query that is longer than the server reads, before it is sent', async () => {
    open({ [SEARCH]: listing() });
    const user = userEvent.setup();
    await screen.findByText('GET /admin 404');
    await user.click(screen.getByLabelText('Query'));
    await user.paste('x'.repeat(8001));
    expect(await screen.findByRole('alert')).toHaveTextContent('The query is 8,001 characters long. The server reads at most 8,000.');
    expect(screen.getByRole('button', { name: 'Search' })).toBeDisabled();
  });

  it('says in the words of the server why a case cannot be searched at this moment', async () => {
    open({ [SEARCH]: problem(409, 'conflict', 'the case is being archived: it cannot be searched until that has ended') });
    expect(await screen.findByText('This case cannot be searched now')).toBeInTheDocument();
    expect(screen.getByText('the case is being archived: it cannot be searched until that has ended')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('gives a case that is neither active nor closed a notice and no query', async () => {
    const server = open({ 'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard({ case: { id: 7, name: 'payroll portal', state: 'archived', my_role: 'owner', owner: 'alice', task: null } }) } });
    expect(await screen.findByText('This case is archived')).toBeInTheDocument();
    expect(screen.queryByLabelText('Query')).toBeNull();
    expect(server.calls(SEARCH)).toHaveLength(0);
  });

  it('says "no such case" to someone the case is not shared with, and asks for no event', async () => {
    const server = open({ 'GET /api/v1/cases/7/dashboard': problem(404, 'not-found', 'no such case') });
    expect(await screen.findByRole('heading', { name: 'No such case' })).toBeInTheDocument();
    expect(server.calls(SEARCH)).toHaveLength(0);
  });

  it('lets a read-only member search like anyone and says the role', async () => {
    open({ [SEARCH]: listing(), 'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard({ case: { id: 7, name: 'payroll portal', state: 'closed', my_role: 'ro', owner: 'alice', task: null } }) } });
    expect(await screen.findByText('GET /admin 404')).toBeInTheDocument();
    expect(screen.getByText(/your role: Read-only/)).toBeInTheDocument();
  });
});

describe('the fields, the zeros and the export on the search page', () => {
  it('shows the fields of the case beside the events and narrows the search from a value', async () => {
    const server = open({ [SEARCH]: listing() }, '/cases/7/search?q=status%3D404+%7C+head+50');
    const user = userEvent.setup();
    const rail = await screen.findByRole('complementary', { name: 'Fields' });
    await user.click(await within(rail).findByRole('button', { name: /^program/ }));
    await user.click(within(rail).getByRole('button', { name: 'Only program nginx' }));

    // Into the search part, before the command.
    await waitFor(() => {
      expect(server.calls(SEARCH).at(-1)?.body).toEqual({ query: 'status=404 program="nginx" | head 50' });
    });
    expect(screen.getByLabelText('Query')).toHaveValue('status=404 program="nginx" | head 50');
  });

  it('says so when the fields cannot be listed, and searches all the same', async () => {
    open({ [SEARCH]: listing(), 'GET /api/v1/cases/7/fields': problem(400, 'bad-request', 'the fields of this case cannot be read') });
    expect(await screen.findByText('GET /admin 404')).toBeInTheDocument();
    const rail = screen.getByRole('complementary', { name: 'Fields' });
    expect(await within(rail).findByText('The fields could not be listed')).toBeInTheDocument();
    expect(within(rail).getByText('the fields of this case cannot be read')).toBeInTheDocument();
    // What the search counted is still there under its name.
    expect(within(rail).getByRole('button', { name: /^program/ })).toBeInTheDocument();
  });

  it('leaves out a 0 where the catalog says it stands for "no value", and shows a 0 that is a value', async () => {
    open({ [SEARCH]: listing({ rows: [{ offset: 100, status: 0 }] }) }, '/cases/7/search?sel=11:100');
    const detail = await screen.findByRole('complementary', { name: 'Event' });
    await waitFor(() => {
      expect(within(detail).queryByText(/Fields in grey hold 0/)).toBeNull();
      expect(within(detail).getAllByRole('rowheader').map((th) => th.textContent)).toContain('http.response.body.bytes');
    });
    const names = within(detail).getAllByRole('rowheader').map((th) => th.textContent);
    // The status and the pid are 0 in the row and marked by the catalog: no value. The bytes are 0 and a value.
    expect(names).not.toContain('http.response.status_code');
    expect(names).not.toContain('process.pid');
  });

  it('says, on the word of the server, when the text of a line is not its bytes, and shows the bytes as hex', async () => {
    const stored = new Uint8Array([0x63, 0x61, 0x66, 0xe9, 0x20, 0x3c, 0x62, 0x3e, 0x00, 0xff]);
    const server = open(
      {
        [SEARCH]: listing({ rows: [{ offset: 100, raw: 'caf� <b>', message: 'caf�', valid: 0 }, { offset: 200, raw: 'a line that says � itself', valid: 1 }] }),
        'GET /api/v1/cases/7/files/11/events/100/raw': { status: 200, bytes: stored, headers: { 'X-Urlog-Raw-Sha256': 'ab'.repeat(32) } },
      },
      '/cases/7/search?sel=11:100',
    );
    const user = userEvent.setup();
    const detail = await screen.findByRole('complementary', { name: 'Event' });
    expect(within(detail).getByRole('note')).toHaveTextContent('This line holds bytes that are not valid UTF-8. The text above has a replacement character for each, so neither it nor Copy is the exact line. The bytes are.');

    // Nothing is asked of the server until the reader asks for the bytes.
    expect(server.sent.some((s) => s.path.includes('/raw'))).toBe(false);
    await user.click(within(detail).getByRole('button', { name: 'Show the bytes' }));
    const hex = await within(detail).findByLabelText('Bytes of the line, as hex');
    expect(hex.textContent).toBe('00000000  63 61 66 e9 20 3c 62 3e  00 ff                    |caf. <b>..|');
    expect(detail).toHaveTextContent('10 bytes, as stored');
    expect(detail).toHaveTextContent(`SHA-256 ${'ab'.repeat(32)}`);
    // Asked for by the key of the event: its file, its offset, its time and its host.
    expect(server.sent.find((s) => s.path.includes('/raw'))?.path).toBe('/api/v1/cases/7/files/11/events/100/raw?ts=2026-03-01T00%3A00%3A05.000Z&host=web-01');
    expect(document.querySelector('b')).toBeNull();

    // The bytes leave the page as a file of bytes.
    const made: Blob[] = [];
    vi.spyOn(URL, 'createObjectURL').mockImplementation((blob) => {
      made.push(blob as Blob);
      return 'blob:bytes';
    });
    const clicked: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function click(this: HTMLAnchorElement) {
      clicked.push(this.download);
    });
    await user.click(within(detail).getByRole('button', { name: 'Save the bytes' }));
    expect(clicked).toEqual(['case-7-file-11-offset-100.bin']);
    expect(made[0]?.type).toBe('application/octet-stream');
    expect(new Uint8Array(await (made[0] as Blob).arrayBuffer())).toEqual(stored);
  });

  it('does not take a line for damaged because it holds a replacement character of its own', async () => {
    open({ [SEARCH]: listing({ rows: [{ offset: 200, raw: 'a line that says � itself', valid: 1 }] }) }, '/cases/7/search?sel=11:200');
    const detail = await screen.findByRole('complementary', { name: 'Event' });
    expect(within(detail).queryByRole('note')).toBeNull();
    expect(within(detail).getByRole('button', { name: 'Show the bytes' })).toBeInTheDocument();
  });

  it('says why when the bytes cannot be read', async () => {
    open({ [SEARCH]: listing({ rows: [{ offset: 100 }] }), 'GET /api/v1/cases/7/files/11/events/100/raw': problem(404, 'not-found', 'no event of this case has that file, offset, time and host') }, '/cases/7/search?sel=11:100');
    const detail = await screen.findByRole('complementary', { name: 'Event' });
    await userEvent.setup().click(within(detail).getByRole('button', { name: 'Show the bytes' }));
    expect(await within(detail).findByRole('alert')).toHaveTextContent('The bytes could not be read: no event of this case has that file, offset, time and host');
  });

  it('says of a piece of a long line which line it is a piece of, and leads to its parts', async () => {
    const server = open({ [SEARCH]: listing({ rows: [{ offset: 65_564, extras: { 'line.first_offset': '28', 'line.part': '2/3', 'timestamp.source': 'previous' } }] }) }, '/cases/7/search?sel=11:65564&from=-7d');
    const user = userEvent.setup();
    const detail = await screen.findByRole('complementary', { name: 'Event' });
    expect(within(detail).getByRole('note')).toHaveTextContent('Part 2 of 3 of one line that begins at offset 28 of this file. A line this long is stored as several events.');

    await user.click(within(detail).getByRole('button', { name: 'Show the parts of this line' }));
    await waitFor(() => {
      expect(server.calls(SEARCH).at(-1)?.body).toEqual({ query: 'file.id=11 line.first_offset="28" | sort file.offset', from: '-7d' });
    });
  });

  it('exports the search of the address to a file the reader chose, and says where the server cut it', async () => {
    const written: string[] = [];
    const picker = vi.fn(() =>
      Promise.resolve({
        name: 'findings.csv',
        createWritable: () =>
          Promise.resolve(
            new WritableStream<Uint8Array>({
              write: (chunk) => {
                written.push(new TextDecoder().decode(chunk));
              },
            }),
          ),
      }),
    );
    vi.stubGlobal('showSaveFilePicker', picker);
    const server = open({ [SEARCH]: listing(), 'GET /api/v1/cases/7/search/export': { status: 200, text: 'ts,host\nrow\n', headers: { 'X-Urlog-Rows': '5000000', 'X-Urlog-Truncated': 'true' } } }, '/cases/7/search?q=status%3D404');
    const user = userEvent.setup();
    await screen.findByText('GET /admin 404');
    // What is typed and not run is not what is exported.
    await user.type(screen.getByLabelText('Query'), ' draft');

    await user.click(screen.getByRole('button', { name: 'Export' }));
    await user.click(await screen.findByRole('menuitemcheckbox', { name: 'Guard formula cells (CSV)' }));
    await user.click(screen.getByRole('menuitem', { name: 'CSV, formula cells guarded' }));

    expect(await screen.findByText(/^Exported/)).toHaveTextContent('Exported findings.csv, 12 B');
    expect(written.join('')).toBe('ts,host\nrow\n');
    expect(server.sent.find((s) => s.path.includes('/export'))?.path).toBe('/api/v1/cases/7/search/export?format=csv&query=status%3D404&formula_guard=true');
    expect(screen.getByText("The file is cut at the server's row limit")).toBeInTheDocument();
    expect(screen.getByText(/The result has 5,000,000 rows/)).toBeInTheDocument();
  });

  it('says that an export of the user is already running when the server answers so', async () => {
    vi.stubGlobal('showSaveFilePicker', () => Promise.resolve({ name: 'x.jsonl', createWritable: () => Promise.resolve(new WritableStream<Uint8Array>()), remove: () => Promise.resolve() }));
    open({ [SEARCH]: listing(), 'GET /api/v1/cases/7/search/export': { status: 429, body: samples.refusals.busy.body, headers: { 'Retry-After': '1' } } });
    const user = userEvent.setup();
    await screen.findByText('GET /admin 404');
    await user.click(screen.getByRole('button', { name: 'Export' }));
    await user.click(await screen.findByRole('menuitem', { name: 'JSON lines' }));
    expect(await screen.findByText('The export did not start')).toBeInTheDocument();
    expect(screen.getByText('An export of yours is already running. One export runs at a time for a user.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});

describe('hostile content on the search page', () => {
  const lines = [
    '<script>window.pwned = 1</script>',
    '<img src=x onerror="window.pwned = 1">',
    '<a href="javascript:window.pwned=1" onclick="window.pwned=1">open</a>',
    'invoice‮fdp.exe and a NUL \u0000 and \u001b[31mred\u001b[0m',
  ];

  it('draws hostile lines, hosts, levels and field values as text in the list and in the detail', async () => {
    const title = document.title;
    open({
      [SEARCH]: listing({
        rows: lines.map((line, i) => ({ offset: 100 + i, message: line, raw: line, host: `<b>h${i}</b>`, program: '"><svg onload=window.pwned=1>', level: '<i>info</i>', user: line, extras: { '<script>key</script>': line } })),
      }),
    });
    const user = userEvent.setup();
    const list = await screen.findByRole('region', { name: 'Events' });

    for (const line of lines.slice(0, 3)) {
      expect(await within(list).findByText(line)).toBeInTheDocument();
    }
    expect(within(list).getByText('<b>h0</b>')).toBeInTheDocument();
    // What would act is shown as a mark with its name.
    expect(within(list).getByTitle('U+202E RIGHT-TO-LEFT OVERRIDE')).toHaveTextContent('U+202E');
    expect(within(list).getByTitle('U+0000 NUL')).toBeInTheDocument();
    expect(within(list).getAllByTitle('U+001B ESCAPE')).toHaveLength(2);

    await user.click(within(list).getAllByRole('button', { name: /Open the event/ })[3] as HTMLElement);
    const detail = await screen.findByRole('complementary', { name: 'Event' });
    expect(within(detail).getByLabelText('Raw line').textContent).toBe('invoiceU+202Efdp.exe and a NUL ␀ and ␛[31mred␛[0m');
    expect(within(detail).getByText('<script>key</script>')).toBeInTheDocument();
    // A value that cannot be typed into a query is not offered as a filter, and the reason is said.
    expect(within(detail).getByRole('button', { name: 'Only user.name' })).toBeDisabled();
    expect(within(detail).getByRole('button', { name: 'Only user.name' })).toHaveAttribute('title', 'The value holds characters a query cannot be typed with');
    expect(within(detail).getByRole('button', { name: /^Only <script>key/ })).toHaveAttribute('title', 'The name of this field cannot be written in a query');

    // No element was built from any of it, nothing ran, and nothing outside the text changed.
    expect(document.querySelectorAll('script, img, svg:not(.icon):not([class^="brand"]), iframe, b, i, a[href^="javascript"]')).toHaveLength(0);
    expect(document.querySelectorAll('[onerror], [onclick], [onload]')).toHaveLength(0);
    expect((window as unknown as { pwned?: number }).pwned).toBeUndefined();
    expect(document.title).toBe(title);
  });

  it('draws a hostile query from the address as text, in the box and in every message that quotes it', async () => {
    const q = '<img src=x onerror=window.pwned=1>';
    open({ [SEARCH]: { status: 400, body: { type: 'https://urlog/problems/query-syntax', title: `unexpected '${q}'`, status: 400, code: 'query_syntax', offset: 0, length: q.length } } }, `/cases/7/search?q=${encodeURIComponent(q)}`);
    expect(await screen.findByRole('alert')).toHaveTextContent(`At character 1: unexpected '${q}'`);
    expect(screen.getByLabelText('Query')).toHaveValue(q);
    expect(document.querySelectorAll('img')).toHaveLength(0);
    expect((window as unknown as { pwned?: number }).pwned).toBeUndefined();
  });

  it('opens an event of a megabyte and of three thousand fields without drawing all of either', async () => {
    const big = 'A'.repeat(1024 * 1024);
    const extras = Object.fromEntries(Array.from({ length: 3000 }, (_, i) => [`sd.param${String(i).padStart(4, '0')}`, `value ${i}`]));
    open({ [SEARCH]: listing({ rows: [{ offset: 100, message: big, raw: big, extras }] }) }, '/cases/7/search?sel=11:100');
    const user = userEvent.setup();

    const detail = await screen.findByRole('complementary', { name: 'Event' });
    // The cell of the list holds four hundred characters of the megabyte.
    const cell = within(region('Events')).getByText(/^A{400}$/);
    expect(cell.closest('td')).toHaveTextContent(/…$/);
    expect(within(detail).getByLabelText('Raw line').textContent).toHaveLength(20_000);
    expect(within(detail).getByText(/Showing the first 20,000 of 1,048,576 characters/)).toBeInTheDocument();
    expect(within(detail).getByText('1,048,576 characters, as stored')).toBeInTheDocument();

    expect(within(detail).getAllByRole('rowheader')).toHaveLength(200);
    expect(within(detail).getByText(/Showing 200 of 3,00\d fields/)).toBeInTheDocument();
    await user.click(within(detail).getByRole('button', { name: 'Show 200 more' }));
    expect(within(detail).getAllByRole('rowheader')).toHaveLength(400);

    await user.type(within(detail).getByLabelText('Part of a field name'), 'param2999');
    expect(within(detail).getAllByRole('rowheader').map((th) => th.textContent)).toEqual(['sd.param2999']);
  });
});
