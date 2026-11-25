import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setCsrfToken } from '../api/http';
import type { FieldsResponse } from '../api/schema.gen';
import { events, fakeServer, problem } from '../test-server';
import { FieldRail } from './FieldRail';
import type { SearchRequest } from './run';
import type { Counts } from './stream';

const SEARCH = 'POST /api/v1/cases/7/search';
const request: SearchRequest = { caseId: 7, q: 'status=404 | head 50', from: '-7d', to: undefined, tz: undefined };

function field(name: string, over: Partial<FieldsResponse['fields'][number]> = {}): FieldsResponse['fields'][number] {
  return { name, type: 'keyword', description: '', aliases: [], sensitive: false, zero_is_absent: false, events: 100, distinct_estimate: 3, top: [], ...over };
}

function catalog(over: Partial<FieldsResponse> = {}): FieldsResponse {
  return {
    case_id: 7,
    span: { from: null, to: null, undated_events: 0, future_events: 0 },
    statistics: { state: 'ok', pending: false, computed_at: '2026-11-18T09:14:02Z', events: 1000, reason: null },
    fields: [
      field('_time', { type: 'time', events: null, distinct_estimate: null, top: null }),
      field('host.name', { events: 900, distinct_estimate: 2, top: [{ value: 'web-01', count: 700 }, { value: 'web-02', count: 200 }] }),
      field('user.name', { type: 'string', sensitive: true, events: 40, distinct_estimate: null, top: null }),
      field('url.query', { type: 'string', events: 0, top: null }),
      field('file.offset', { type: 'int', events: 1000, top: [] }),
    ],
    extras_keys: [
      { key: 'log.syslog.msgid', events: 12 },
      { key: '<img src=x onerror=window.pwned=1>', events: 3 },
    ],
    verbs: ['stats', 'sort', 'head'],
    ...over,
  };
}

const counted: Counts = { kind: 'counts', total: 50, source: 'query', fields: [{ name: 'host.name', top: [{ value: 'web-02', count: 31 }, { value: 'web-01', count: 19 }] }] };
const table = (rows: string[][]) =>
  events([
    { event: 'meta', data: { kind: 'meta', from: '2026-03-01T00:00:00.000Z', to: '2026-03-02T00:00:00.000Z', tz: 'UTC', result: 'stats', columns: [], warnings: [], fields: [], span: { from: null, to: null, undated_events: 0, future_events: 0 } } },
    { event: 'table', data: { kind: 'table', columns: ['user.name', 'count'], rows, truncated: false, total_rows: rows.length, values_capped_rows: [] } },
    { event: 'done', data: { kind: 'done', duration_ms: 3 } },
  ]);

function show(props: Partial<Parameters<typeof FieldRail>[0]> = {}) {
  const onFilter = vi.fn();
  render(<FieldRail catalog={catalog()} catalogError={undefined} request={request} counts={counted} live={false} ended={false} onFilter={onFilter} {...props} />);
  return onFilter;
}

afterEach(() => {
  setCsrfToken(undefined);
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('the fields of a case beside its events', () => {
  it('lists the fields that have events, with their count, and leaves out the ones that are in none or are the identity of an event', () => {
    show();
    const names = screen.getAllByRole('button', { expanded: false }).map((b) => b.textContent);
    expect(names).toEqual(['host.name900', 'user.name40', 'log.syslog.msgid12', '<img src=x onerror=window.pwned=1>3']);
    expect(screen.getByRole('heading', { name: 'Fields outside the catalog' })).toBeInTheDocument();
    expect(document.querySelector('img')).toBeNull();
  });

  it('shows under a field the values this search has most of, each a way to narrow or to leave out', async () => {
    const onFilter = show();
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: /host\.name/ }));

    expect(screen.getByText('In 900 events of the case, about 2 values')).toBeInTheDocument();
    const values = within(screen.getByRole('button', { name: /^host\.name/, expanded: true }).closest('li') as HTMLElement).getAllByRole('listitem');
    // The values and counts of the search, not of the case.
    expect(values.map((li) => li.textContent)).toEqual(['web-0231OnlyExclude', 'web-0119OnlyExclude']);

    await user.click(screen.getByRole('button', { name: 'Only host.name web-02' }));
    await user.click(screen.getByRole('button', { name: 'Exclude host.name web-01' }));
    expect(onFilter.mock.calls).toEqual([
      ['host.name', 'web-02', false],
      ['host.name', 'web-01', true],
    ]);
  });

  it('takes the values of the case for a search that narrows nothing, as the server says they are the same', async () => {
    show({ counts: { kind: 'counts', total: 1000, source: 'field_stats', fields: [] }, request: { ...request, q: '' } });
    await userEvent.setup().click(screen.getByRole('button', { name: /host\.name/ }));
    expect(screen.getByRole('button', { name: 'Only host.name web-01' })).toBeInTheDocument();
    expect(screen.getByText('700')).toBeInTheDocument();
  });

  it('asks for the values of a field the statistics keep none for, with the search of the page and a count by the field', async () => {
    const server = fakeServer({ [SEARCH]: table([['root', '22'], ['', '9'], ['<b>admin</b>', '4']]) });
    setCsrfToken('token-1');
    const onFilter = show();
    const user = userEvent.setup();
    expect(server.calls(SEARCH)).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: /user\.name/ }));
    expect(screen.getByText(/In 40 events of the case · sensitive/)).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Only user.name root' })).toBeInTheDocument();
    // The commands of the page's query are left out; its search part, range and zone are kept.
    expect(server.calls(SEARCH)[0]?.body).toEqual({ query: 'status=404 | stats count by user.name | sort -count | head 10', from: '-7d' });
    // A row without a value is no value of the field; a value with markup in it is text.
    expect(screen.queryByRole('button', { name: 'Only user.name ' })).toBeNull();
    expect(screen.getByText('<b>admin</b>')).toBeInTheDocument();
    expect(document.querySelector('b')).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Only user.name <b>admin</b>' }));
    expect(onFilter).toHaveBeenCalledWith('user.name', '<b>admin</b>', false);
  });

  it('says "busy, retrying" while the server has no slot for the values, and shows them when it has', async () => {
    let asked = 0;
    fakeServer({
      [SEARCH]: () => {
        asked += 1;
        return asked === 1 ? { status: 429, body: { type: 'https://urlog/problems/busy', title: 'too many queries are running', status: 429, code: 'busy' }, headers: { 'Retry-After': '1' } } : table([['root', '22']]);
      },
    });
    show();
    await userEvent.setup().click(screen.getByRole('button', { name: /user\.name/ }));
    expect(await screen.findByText('The server is busy, retrying in 1 s (try 1 of 5)')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(await screen.findByRole('button', { name: 'Only user.name root' }, { timeout: 4000 })).toBeInTheDocument();
  });

  it('says why when the values cannot be counted, and tries again when asked', async () => {
    const server = fakeServer({ [SEARCH]: problem(504, 'query-timeout', 'the query passed its time limit', { code: 'query_timeout' }) });
    show();
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: /user\.name/ }));
    expect(await screen.findByRole('alert')).toHaveTextContent('The values could not be counted: the query passed its time limit.');

    server.set(SEARCH, table([['root', '22']]));
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('button', { name: 'Only user.name root' })).toBeInTheDocument();
  });

  it('asks for the values of a field outside the catalog, and says why one with a name no query can hold has none', async () => {
    const server = fakeServer({ [SEARCH]: table([['ID47', '12']]) });
    show();
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: /log\.syslog\.msgid/ }));
    expect(await screen.findByRole('button', { name: 'Only log.syslog.msgid ID47' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /<img src=x/ }));
    expect(screen.getByText('The name of this field cannot be written in a query, so its values cannot be asked for.')).toBeInTheDocument();
    expect(server.calls(SEARCH)).toHaveLength(1);
    expect((window as unknown as { pwned?: number }).pwned).toBeUndefined();
  });

  it('shows skeletons while the fields load and "counting" while the search has not counted', async () => {
    const { rerender } = render(<FieldRail catalog={undefined} catalogError={undefined} request={request} counts={undefined} live ended={false} onFilter={vi.fn()} />);
    expect(screen.queryAllByRole('button')).toHaveLength(0);
    expect(document.querySelectorAll('.rail-skeleton')).toHaveLength(5);

    rerender(<FieldRail catalog={catalog()} catalogError={undefined} request={request} counts={undefined} live ended={false} onFilter={vi.fn()} />);
    await userEvent.setup().click(screen.getByRole('button', { name: /host\.name/ }));
    expect(screen.getByText('Counting')).toBeInTheDocument();

    rerender(<FieldRail catalog={catalog()} catalogError={undefined} request={request} counts={undefined} live={false} ended onFilter={vi.fn()} />);
    expect(screen.getByText('The values could not be counted: the search did not finish.')).toBeInTheDocument();
  });

  it('says so when the fields cannot be listed, and still shows what the search counted', async () => {
    show({ catalog: undefined, catalogError: 'the event store does not answer' });
    expect(screen.getByRole('alert')).toHaveTextContent('The fields could not be listed');
    expect(screen.getByRole('alert')).toHaveTextContent('the event store does not answer');
    await userEvent.setup().click(screen.getByRole('button', { name: /host\.name/ }));
    expect(screen.getByRole('button', { name: 'Only host.name web-02' })).toBeInTheDocument();
  });

  it.each([
    ['none', false, null, 'Field statistics: not computed yet.'],
    ['skipped', true, 'the case holds more rows than the limit of the statistics', 'Field statistics: skipped, a new run is pending. the case holds more rows than the limit of the statistics'],
    ['timed_out', false, 'the run passed its time limit', 'Field statistics: timed out. the run passed its time limit'],
    ['failed', false, null, 'Field statistics: failed.'],
    ['ok', true, null, 'The field statistics are being computed again; counts are of the last run.'],
  ])('says the state of the statistics when it is %s', (state, pending, reason, words) => {
    show({ catalog: catalog({ statistics: { state, pending, computed_at: null, events: null, reason } }) });
    expect(screen.getByRole('status')).toHaveTextContent(words);
  });

  it('says that nothing has been counted yet when the case has no statistics', () => {
    show({ catalog: catalog({ fields: [field('host.name', { events: 0 })], extras_keys: [] }), counts: { kind: 'counts', total: 50, source: 'query', fields: [] } });
    expect(screen.getByText('No field statistics yet: the fields of this case have not been counted.')).toBeInTheDocument();
  });

  it('narrows the list by a part of a name, and shows the fields outside the catalog thirty at a time', async () => {
    const many = Array.from({ length: 75 }, (_, i) => ({ key: `sd.param${String(i).padStart(2, '0')}`, events: 100 - i }));
    show({ catalog: catalog({ extras_keys: many }) });
    const user = userEvent.setup();
    expect(screen.getAllByRole('button', { name: /sd\.param/ })).toHaveLength(30);
    expect(screen.getByText(/Showing 30 of 75\./)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Show 30 more' }));
    expect(screen.getAllByRole('button', { name: /sd\.param/ })).toHaveLength(60);

    await user.type(screen.getByLabelText('Part of a field name'), 'param74');
    expect(screen.getAllByRole('button', { expanded: false }).map((b) => b.textContent)).toEqual(['sd.param7426']);
    await user.clear(screen.getByLabelText('Part of a field name'));
    await user.type(screen.getByLabelText('Part of a field name'), 'nothing-like-this');
    expect(screen.getByText('No field has that in its name.')).toBeInTheDocument();
  });
});
