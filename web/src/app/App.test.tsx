import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setCsrfToken } from '../api/http';
import { fakeServer, problem, session } from '../test-server';
import { App } from './App';
import { safeNext } from './session';

const NOT_LOGGED_IN = problem(401, 'unauthorized', 'not logged in');

const incident = { id: 7, name: 'payroll portal', state: 'active', role: 'owner', owner: 'alice', created_at: '2026-11-19T08:30:00+00:00' };
const old = { id: 3, name: 'vpn spraying', state: 'closed', role: 'ro', owner: 'bob', created_at: '2026-10-02T12:00:00+00:00' };

function open(path: string) {
  window.history.replaceState(null, '', path);
  return render(<App />);
}

afterEach(() => {
  setCsrfToken(undefined);
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('sign-in', () => {
  it('shows the sign-in page to a visitor without a session and the case list after it', async () => {
    const server = fakeServer({
      'GET /api/v1/auth/me': NOT_LOGGED_IN,
      'POST /api/v1/auth/login': { status: 200, body: session },
      'GET /api/v1/cases': { status: 200, body: [incident] },
    });
    const user = userEvent.setup();
    open('/');

    await user.type(await screen.findByLabelText('Name'), 'alice');
    await user.type(screen.getByLabelText('Password'), 'correct horse battery');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('heading', { name: 'Cases' })).toBeInTheDocument();
    expect(await screen.findByText('payroll portal')).toBeInTheDocument();
    expect(server.calls('POST /api/v1/auth/login')[0]?.body).toEqual({ name: 'alice', password: 'correct horse battery' });
    expect(window.location.pathname).toBe('/cases');
  });

  it('answers a refused sign-in with one line that names neither the name nor the password', async () => {
    fakeServer({
      'GET /api/v1/auth/me': NOT_LOGGED_IN,
      'POST /api/v1/auth/login': problem(401, 'invalid-credentials', 'invalid credentials'),
    });
    const user = userEvent.setup();
    open('/login');

    await user.type(await screen.findByLabelText('Name'), 'alice');
    await user.type(screen.getByLabelText('Password'), 'wrong');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    const notice = await screen.findByRole('status');
    expect(notice).toHaveTextContent('That name and password were not accepted');
    expect(notice).not.toHaveTextContent(/attempt|lock/i);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('shows a throttled sign-in as a wait that counts down, not as an error', async () => {
    const server = fakeServer({
      'GET /api/v1/auth/me': NOT_LOGGED_IN,
      'POST /api/v1/auth/login': { ...problem(429, 'throttled', 'too many login attempts'), headers: { 'Retry-After': '2' } },
    });
    const user = userEvent.setup();
    open('/login');

    await user.type(await screen.findByLabelText('Name'), 'alice');
    await user.type(screen.getByLabelText('Password'), 'x');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByText(/Try again in 2 s/)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeDisabled();
    await waitFor(() => { expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled(); }, { timeout: 4000 });
    expect(server.calls('POST /api/v1/auth/login')).toHaveLength(1);
  });

  it('shows a server that fails as an error and keeps what was typed', async () => {
    fakeServer({ 'GET /api/v1/auth/me': NOT_LOGGED_IN });
    const user = userEvent.setup();
    open('/login');

    await user.type(await screen.findByLabelText('Name'), 'alice');
    await user.type(screen.getByLabelText('Password'), 'x');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The server did not answer');
    expect(screen.getByLabelText('Name')).toHaveValue('alice');
  });
});

describe('a session that ends', () => {
  it('sends any page to sign-in on a 401 and brings the user back to the same address', async () => {
    const server = fakeServer({
      'GET /api/v1/auth/me': { status: 200, body: session },
      'GET /api/v1/cases/7': NOT_LOGGED_IN,
      'POST /api/v1/auth/login': { status: 200, body: { ...session, csrf_token: 'token-2' } },
    });
    const user = userEvent.setup();
    open('/cases/7');

    expect(await screen.findByText('Your session ended')).toBeInTheDocument();
    expect(`${window.location.pathname}${window.location.search}`).toBe('/login?next=%2Fcases%2F7');

    server.set('GET /api/v1/cases/7', { status: 200, body: { ...incident, closed_at: null, archived_at: null, members: 1, my_role: 'owner', span: { from: null, to: null, undated_events: 0, future_events: 0 }, statistics: { state: 'none', pending: false, computed_at: null, reason: null }, task: null } });
    await user.type(screen.getByLabelText('Name'), 'alice');
    await user.type(screen.getByLabelText('Password'), 'correct horse battery');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('heading', { name: 'payroll portal' })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/cases/7');
  });

  it('signs out through the menu and forgets what it had read', async () => {
    const server = fakeServer({
      'GET /api/v1/auth/me': { status: 200, body: session },
      'GET /api/v1/cases': { status: 200, body: [incident] },
      'POST /api/v1/auth/logout': { status: 204 },
    });
    const user = userEvent.setup();
    open('/cases');

    await screen.findByText('payroll portal');
    await user.click(screen.getByRole('button', { name: /Signed in as alice/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Sign out' }));

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.queryByText('Your session ended')).toBeNull();
    expect(server.calls('POST /api/v1/auth/logout')[0]?.headers['X-CSRF-Token']).toBe('token-1');
    expect(screen.queryByText('payroll portal')).toBeNull();
    // The next person to sign in on this browser starts at the case list.
    expect(`${window.location.pathname}${window.location.search}`).toBe('/login');
  });

  it('takes only a path of this app as the place to return to', () => {
    expect(safeNext('/cases/7?x=1')).toBe('/cases/7?x=1');
    for (const bad of [null, '', 'https://example.org/', '//example.org', '/\\example.org', 'cases', '/login', '/login?next=/login']) {
      expect(safeNext(bad)).toBe('/cases');
    }
  });
});

describe('the instance', () => {
  it('says so when the backend is not serving yet, with the reason the health route gives', async () => {
    fakeServer({
      'GET /api/v1/auth/me': { ...problem(503, 'not-ready', 'not ready'), headers: { 'Retry-After': '5' } },
      'GET /api/v1/health': { status: 503, body: { status: 'waiting_for_event_store', reason: 'the event store does not answer' } },
    });
    open('/cases');
    expect(await screen.findByText('This instance is not serving yet')).toBeInTheDocument();
    await waitFor(() => { expect(screen.getByRole('status')).toHaveTextContent('the event store does not answer'); });
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('says so when nothing answers, with a way to try again', async () => {
    const server = fakeServer({});
    const user = userEvent.setup();
    open('/cases');
    expect(await screen.findByRole('alert')).toHaveTextContent('The server did not answer');

    server.set('GET /api/v1/auth/me', NOT_LOGGED_IN);
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
  });
});

describe('the case list', () => {
  const signedIn = { 'GET /api/v1/auth/me': { status: 200, body: session } };

  it('invites a first case when there is none, and lists the one that is created', async () => {
    const server = fakeServer({ ...signedIn, 'GET /api/v1/cases': { status: 200, body: [] }, 'POST /api/v1/cases': { status: 201, body: incident } });
    const user = userEvent.setup();
    open('/cases');

    expect(await screen.findByRole('heading', { name: 'Start your first case' })).toBeInTheDocument();
    await user.click(screen.getAllByRole('button', { name: 'New case' })[0] as HTMLElement);
    const dialog = await screen.findByRole('dialog', { name: 'New case' });
    server.set('GET /api/v1/cases', { status: 200, body: [incident] });
    await user.type(within(dialog).getByLabelText('Name'), '  payroll portal ');
    await user.click(within(dialog).getByRole('button', { name: 'Create case' }));

    expect(await screen.findByRole('link', { name: /payroll portal/ })).toHaveAttribute('href', '/cases/7');
    expect(screen.queryByRole('dialog')).toBeNull();
    const sent = server.calls('POST /api/v1/cases')[0];
    expect(sent?.body).toEqual({ name: 'payroll portal' });
    expect(sent?.headers['X-CSRF-Token']).toBe('token-1');
  });

  it('puts a refused name at the field in the server words and creates nothing', async () => {
    fakeServer({ ...signedIn, 'GET /api/v1/cases': { status: 200, body: [incident] }, 'POST /api/v1/cases': problem(409, 'conflict', "a case named 'payroll portal' exists") });
    const user = userEvent.setup();
    open('/cases');

    await screen.findByText('payroll portal');
    await user.click(screen.getByRole('button', { name: 'New case' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Name'), 'payroll portal');
    await user.click(within(dialog).getByRole('button', { name: 'Create case' }));

    expect(await within(dialog).findByText("a case named 'payroll portal' exists")).toBeInTheDocument();
    expect(within(dialog).getByLabelText('Name')).toBeInvalid();
  });

  it('filters by state and by text, and says when the filter leaves nothing', async () => {
    fakeServer({ ...signedIn, 'GET /api/v1/cases': { status: 200, body: [incident, old] } });
    const user = userEvent.setup();
    open('/cases');

    await screen.findByText('payroll portal');
    expect(screen.getByText('1 active · 1 closed · 0 archived')).toBeInTheDocument();
    expect(screen.queryByText('vpn spraying')).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Closed' }));
    expect(screen.getByText('vpn spraying')).toBeInTheDocument();
    expect(screen.getByText('Read-only')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Archived' }));
    expect(screen.getByRole('heading', { name: 'No archived cases' })).toBeInTheDocument();
    expect(screen.getByText('2 cases in all.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Show all cases' }));
    await user.type(screen.getByLabelText('Filter cases'), 'vpn');
    expect(screen.getByText('vpn spraying')).toBeInTheDocument();
    expect(screen.queryByText('payroll portal')).toBeNull();
  });

  it('shows a list that could not be read as an error with a way to try again, and invents no rows', async () => {
    const server = fakeServer({ ...signedIn, 'GET /api/v1/cases': problem(500, 'error', 'failed') });
    const user = userEvent.setup();
    open('/cases');

    expect(await screen.findByRole('alert', {}, { timeout: 8000 })).toHaveTextContent('Your cases could not be loaded');
    expect(screen.queryByRole('table')).toBeNull();

    server.set('GET /api/v1/cases', { status: 200, body: [incident] });
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText('payroll portal')).toBeInTheDocument();
  }, 15_000);
});

describe('a case', () => {
  it('answers a case that is not shared and one that does not exist with the same page', async () => {
    fakeServer({ 'GET /api/v1/auth/me': { status: 200, body: session }, 'GET /api/v1/cases/99': problem(404, 'not-found', 'no such case') });
    open('/cases/99');
    expect(await screen.findByRole('heading', { name: 'No such case' })).toBeInTheDocument();
    expect(screen.getByText(/or it is not shared with you/)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('has a page for an address that names nothing', async () => {
    fakeServer({ 'GET /api/v1/auth/me': { status: 200, body: session } });
    open('/nowhere');
    expect(await screen.findByRole('heading', { name: 'Nothing at this address' })).toBeInTheDocument();
  });
});
