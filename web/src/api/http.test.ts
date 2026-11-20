import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, problem, session } from '../test-server';
import { ApiError, call, healthReason, setCsrfToken, setUnauthorizedHandler } from './http';

afterEach(() => {
  setCsrfToken(undefined);
  setUnauthorizedHandler(undefined);
  vi.unstubAllGlobals();
});

describe('call', () => {
  it('sends the CSRF token with a request that changes something and with no other', async () => {
    const server = fakeServer({
      'GET /api/v1/cases': { status: 200, body: [] },
      'POST /api/v1/cases': { status: 201, body: { id: 7 } },
    });
    setCsrfToken('token-1');

    await call('ListCases');
    await call('CreateCase', { body: { name: 'incident' } });

    expect(server.sent[0]?.headers['X-CSRF-Token']).toBeUndefined();
    expect(server.sent[1]?.headers['X-CSRF-Token']).toBe('token-1');
    expect(server.sent[1]?.body).toEqual({ name: 'incident' });
  });

  it('puts the parameters of a route into its path', async () => {
    const server = fakeServer({ 'GET /api/v1/cases/42': { status: 200, body: { id: 42 } } });
    await call('GetCase', { params: { caseId: 42 } });
    expect(server.sent[0]?.path).toBe('/api/v1/cases/42');
  });

  it('holds a call to the shape of its route', () => {
    // The checks below are for the type checker: a wrong body or a missing parameter must not compile.
    const never = (): void => {
      // @ts-expect-error a case is created from a name
      void call('CreateCase', { body: { title: 'incident' } });
      // @ts-expect-error a case is read by its id
      void call('GetCase');
    };
    expect(never).toBeTypeOf('function');
  });

  it('turns a refusal into an ApiError with the server words and the wait it asks for', async () => {
    fakeServer({ 'POST /api/v1/auth/login': { ...problem(429, 'throttled', 'too many login attempts', { detail: 'retry after 38 s' }), headers: { 'Retry-After': '38' } } });
    const error = await call('Login', { body: { name: 'a', password: 'b' }, quiet401: true }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 429, slug: 'throttled', message: 'too many login attempts', detail: 'retry after 38 s', retryAfter: 38 });
  });

  it('reads the code of a search refusal', async () => {
    fakeServer({ 'GET /api/v1/cases': problem(400, 'bad-request', 'not a time', { code: 'bad_request' }) });
    await expect(call('ListCases')).rejects.toMatchObject({ code: 'bad_request', status: 400 });
  });

  it('tells the session about a 401, except where a 401 is the route own answer', async () => {
    fakeServer({
      'GET /api/v1/cases': problem(401, 'unauthorized', 'not logged in'),
      'POST /api/v1/auth/login': problem(401, 'invalid-credentials', 'invalid credentials'),
    });
    const ended = vi.fn();
    setUnauthorizedHandler(ended);

    await expect(call('Login', { body: { name: 'a', password: 'b' }, quiet401: true })).rejects.toMatchObject({ status: 401 });
    expect(ended).not.toHaveBeenCalled();

    await expect(call('ListCases')).rejects.toMatchObject({ status: 401 });
    expect(ended).toHaveBeenCalledOnce();
  });

  it('reports a request without an answer as offline', async () => {
    fakeServer({});
    await expect(call('ListCases')).rejects.toMatchObject({ status: 0, offline: true });
  });

  it('survives an error page that is not a problem body', async () => {
    vi.stubGlobal('fetch', () => Promise.resolve(new Response('<html>502</html>', { status: 502 })));
    await expect(call('ListCases')).rejects.toMatchObject({ status: 502, message: 'the server answered 502' });
  });

  it('answers nothing for a 204', async () => {
    fakeServer({ 'POST /api/v1/auth/logout': { status: 204 } });
    await expect(call('Logout')).resolves.toBeUndefined();
  });
});

describe('healthReason', () => {
  it('reads the reason from an answer that is not a success', async () => {
    fakeServer({ 'GET /api/v1/health': { status: 503, body: { status: 'waiting_for_event_store', reason: 'the event store does not answer' } } });
    await expect(healthReason()).resolves.toBe('the event store does not answer');
  });

  it('has none when the server is silent', async () => {
    fakeServer({});
    await expect(healthReason()).resolves.toBeUndefined();
  });
});

describe('the session shape', () => {
  it('is what the fake server answers with', () => {
    expect(session.csrf_token).toBe('token-1');
  });
});
