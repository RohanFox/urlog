import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setCsrfToken } from '../api/http';
import { App } from '../app/App';
import { dashboard, fakeServer, fileRecord, problem, session } from '../test-server';
import { uploads } from '../upload/queue';

const ME = { 'GET /api/v1/auth/me': { status: 200, body: session } };
const FILES = '/api/v1/cases/7/files';

function open(routes: Parameters<typeof fakeServer>[0]) {
  const server = fakeServer({ ...ME, ...routes });
  window.history.replaceState(null, '', '/cases/7');
  render(<App />);
  return server;
}

afterEach(() => {
  uploads.forget();
  setCsrfToken(undefined);
  vi.unstubAllGlobals();
});

describe('the evidence page of a case', () => {
  it('shows the case, its span, its queue, its statistics and its files as the server reports them', async () => {
    open({ 'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard() }, [`GET ${FILES}`]: { status: 200, body: { files: [fileRecord()], next_after: null } } });

    expect(await screen.findByRole('heading', { name: 'payroll portal' })).toBeInTheDocument();
    const overview = screen.getByRole('region', { name: 'Case overview' });
    expect(overview).toHaveTextContent('2026-10-01 00:00 UTC to 2026-10-08 23:59 UTC');
    expect(overview).toHaveTextContent('1 file · 2.00 KiB arrived · 36 rows · 2 rejected lines');
    expect(overview).toHaveTextContent('Nothing waits');
    expect(overview).toHaveTextContent('Field statistics');
    expect(overview).toHaveTextContent('Current · computed 2026-11-18 09:14 UTC');

    const row = (await screen.findByText('access.log')).closest('tr') as HTMLElement;
    expect(row).toHaveTextContent('web-01');
    expect(row).toHaveTextContent('nginx combined 99%');
    expect(row).toHaveTextContent('a3f1c29b7d…e07b');
    expect(row).toHaveTextContent('Parsed');
    expect(screen.getByRole('region', { name: 'Add evidence' })).toBeInTheDocument();
  });

  it('shows the events a default search leaves out beside the span: undated and future-dated', async () => {
    open({
      'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard({ span: { from: '2026-10-01T00:00:04Z', to: '2026-10-08T23:59:58Z', undated_events: 12, future_events: 1 } }) },
      [`GET ${FILES}`]: { status: 200, body: { files: [], next_after: null } },
    });
    const overview = await screen.findByRole('region', { name: 'Case overview' });
    expect(overview).toHaveTextContent('12 events without a usable time');
    expect(overview).toHaveTextContent('1 event dated after their file arrived');
  });

  it.each([
    ['skipped', 'the case holds more rows than the limit of the statistics', 'Skipped'],
    ['timed_out', 'the run passed its time limit', 'Timed out'],
    ['failed', 'the event store did not answer', 'Failed'],
    ['none', null, 'Not computed yet'],
  ])('shows the statistics state %s with its reason', async (state, reason, words) => {
    open({
      'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard({ statistics: { state, pending: true, computed_at: null, reason } }) },
      [`GET ${FILES}`]: { status: 200, body: { files: [], next_after: null } },
    });
    const overview = await screen.findByRole('region', { name: 'Case overview' });
    expect(overview).toHaveTextContent(`${words} · a new run is pending`);
    if (reason !== null) {
      expect(overview).toHaveTextContent(reason);
    }
  });

  it('shows a quarantined file as its own state, marked, and counts it among the things to look at', async () => {
    const server = open({
      'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard({ files: { total: 2, arrived: 2, bytes_arrived: 4096, by_state: { parsed: 1, quarantined: 1 }, by_kind: { log: 2 }, rows: 36, rejects: 2, flags: { quarantined: 1, year_assumed: 1 } } }) },
      [`GET ${FILES}`]: { status: 200, body: { files: [fileRecord(), fileRecord({ id: 12, original_name: 'auth.log', state: 'quarantined', quarantined: true, flags: ['year_assumed', 'timezone_assumed'] })], next_after: null } },
    });
    const user = userEvent.setup();

    const row = (await screen.findByText('auth.log')).closest('tr') as HTMLElement;
    expect(row).toHaveClass('row-err');
    expect(row).toHaveTextContent('Quarantined');
    expect(row).toHaveTextContent('year assumed');
    expect(row).toHaveTextContent('zone assumed');

    // The count is a way to the files it counts.
    await user.click(screen.getByTitle('Show these files'));
    await vi.waitFor(() => {
      expect(server.sent.some((s) => s.path.includes('state=quarantined'))).toBe(true);
    });
    // A flag the list cannot filter by is a plain count.
    expect(within(screen.getByRole('region', { name: 'Case overview' })).queryByRole('button', { name: /year assumed/ })).toBeNull();
  });

  it('shows the job that runs on its file, and the reason of a failed parse from its job', async () => {
    open({
      'GET /api/v1/cases/7/dashboard': {
        status: 200,
        body: dashboard({
          files: { total: 2, arrived: 2, bytes_arrived: 4096, by_state: { received: 1, failed: 1 }, by_kind: { log: 2 }, rows: 0, rejects: 0, flags: {} },
          ingest: { queued: 0, parsing: 1, failed: 1, cancelled: 0, done: 0, paused: null, current: [{ job_id: 5, file_id: 11, kind: 'parse', bytes_read: 512, bytes_total: 2048, rows: 9, rejects: 0, started_at: '2026-11-19T08:32:00Z' }], bytes_done: 0, bytes_total: 4096 },
        }),
      },
      [`GET ${FILES}`]: { status: 200, body: { files: [fileRecord({ state: 'received', rows: 0 }), fileRecord({ id: 12, original_name: 'broken.gz', state: 'failed' })], next_after: null } },
      'GET /api/v1/cases/7/jobs': { status: 200, body: { jobs: [{ id: 6, file_id: 12, kind: 'parse', state: 'failed', message: 'the gzip stream is damaged at byte 1,024' }], next_after: null } },
    });

    const running = (await screen.findByText('access.log')).closest('tr') as HTMLElement;
    expect(within(running).getByRole('progressbar')).toHaveAttribute('value', '25');
    expect(running).toHaveTextContent('25% · 9 rows');
    expect(await screen.findByText('the gzip stream is damaged at byte 1,024')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Case overview' })).toHaveTextContent('0 waiting · 1 being parsed · 1 job failed');
  });

  it('invites the first files when the case is empty and shows no table it would have to invent', async () => {
    open({ 'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard({ files: { total: 0, arrived: 0, bytes_arrived: 0, by_state: {}, by_kind: {}, rows: 0, rejects: 0, flags: {} }, hosts: [], span: { from: null, to: null, undated_events: 0, future_events: 0 } }) } });
    expect(await screen.findByText('Add the first evidence files')).toBeInTheDocument();
    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.queryByRole('region', { name: 'Case overview' })).toBeNull();
  });

  it('gives a read-only member no way to upload and says which role it needs', async () => {
    open({
      'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard({ case: { id: 7, name: 'payroll portal', state: 'active', my_role: 'ro', owner: 'alice', task: null } }) },
      [`GET ${FILES}`]: { status: 200, body: { files: [fileRecord()], next_after: null } },
    });
    expect(await screen.findByText('Your role in this case is read-only')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Add evidence' })).toBeNull();
    expect(await screen.findByText('access.log')).toBeInTheDocument();
  });

  it('takes no files into a closed case and says so, without an error', async () => {
    open({
      'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard({ case: { id: 7, name: 'payroll portal', state: 'closed', my_role: 'owner', owner: 'alice', task: null } }) },
      [`GET ${FILES}`]: { status: 200, body: { files: [fileRecord()], next_after: null } },
    });
    expect(await screen.findByText('This case is closed')).toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Add evidence' })).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('says that intake is paused, in the words of the server', async () => {
    open({
      'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard({ ingest: { queued: 2, parsing: 0, failed: 0, cancelled: 0, done: 1, paused: 'the evidence volume is under its free-space mark', current: [], bytes_done: 0, bytes_total: 0 } }) },
      [`GET ${FILES}`]: { status: 200, body: { files: [fileRecord()], next_after: null } },
    });
    expect(await screen.findByText('Intake is paused')).toBeInTheDocument();
    expect(screen.getByText('the evidence volume is under its free-space mark')).toBeInTheDocument();
  });

  it('pages the file list by the cursor the server gives and filters by a part of a name', async () => {
    const server = open({
      'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard({ files: { total: 2, arrived: 2, bytes_arrived: 4096, by_state: { parsed: 2 }, by_kind: { log: 2 }, rows: 72, rejects: 4, flags: {} } }) },
      [`GET ${FILES}?after=0&limit=100`]: { status: 200, body: { files: [fileRecord()], next_after: 11 } },
      [`GET ${FILES}?after=11&limit=100`]: { status: 200, body: { files: [fileRecord({ id: 12, original_name: 'error.log' })], next_after: null } },
      [`GET ${FILES}?after=0&limit=100&q=err`]: { status: 200, body: { files: [fileRecord({ id: 12, original_name: 'error.log' })], next_after: null } },
    });
    const user = userEvent.setup();

    await screen.findByText('access.log');
    expect(screen.queryByText('error.log')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Load 100 more' }));
    expect(await screen.findByText('error.log')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Load 100 more' })).toBeNull();

    await user.type(screen.getByLabelText('Part of a file name'), 'err');
    await vi.waitFor(() => {
      expect(screen.queryByText('access.log')).toBeNull();
    });
    expect(server.sent.some((s) => s.path.endsWith('q=err'))).toBe(true);
  });

  it('keeps the page and says so when the file list cannot be read', async () => {
    open({ 'GET /api/v1/cases/7/dashboard': { status: 200, body: dashboard() }, [`GET ${FILES}`]: problem(400, 'bad-request', "state: 'parsd' is not a state") });
    expect(await screen.findByText('The files could not be listed')).toBeInTheDocument();
    expect(screen.getByText("state: 'parsd' is not a state")).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Case overview' })).toBeInTheDocument();
  });
});
