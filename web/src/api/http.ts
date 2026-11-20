import { ApiError, offline, problemOf } from './problem';
import { operations, type Operations } from './schema.gen';

// The one module that talks to the backend. It adds the CSRF token to every request that changes something,
// turns a refusal into an ApiError, and tells the session when the server no longer knows it.

type Name = keyof Operations;

type Options<N extends Name> = (Operations[N]['params'] extends undefined ? { params?: undefined } : { params: Operations[N]['params'] }) &
  (Operations[N]['body'] extends undefined ? { body?: undefined } : { body: Operations[N]['body'] }) & {
    query?: Record<string, string | undefined>;
    signal?: AbortSignal;
    /** A route that answers 401 as part of its own contract (sign-in): the session is not told. */
    quiet401?: boolean;
  };

let csrfToken: string | undefined;
let onUnauthorized: (() => void) | undefined;

/** The token of the session, from sign-in or from the session read. Kept in memory only. */
export function setCsrfToken(token: string | undefined): void {
  csrfToken = token;
}

/** Called when any request is answered 401: the session ended on the server. */
export function setUnauthorizedHandler(handler: (() => void) | undefined): void {
  onUnauthorized = handler;
}

function urlOf(template: string, params: Record<string, number | string> | undefined, query: Record<string, string | undefined> | undefined): string {
  let path = template;
  for (const [name, value] of Object.entries(params ?? {})) {
    path = path.replace(`{${name}}`, encodeURIComponent(String(value)));
  }
  const pairs = Object.entries(query ?? {}).filter((pair): pair is [string, string] => pair[1] !== undefined);
  return pairs.length === 0 ? path : `${path}?${new URLSearchParams(pairs).toString()}`;
}

const SAFE = new Set(['GET', 'HEAD', 'OPTIONS']);

/** Calls a route by its name in the description of the backend's routes. */
export async function call<N extends Name>(name: N, ...rest: Options<N> extends { params: unknown } | { body: unknown } ? [Options<N>] : [Options<N>?]): Promise<Operations[N]['result']> {
  const options = (rest[0] ?? {}) as { params?: Record<string, number | string>; body?: unknown; query?: Record<string, string | undefined>; signal?: AbortSignal; quiet401?: boolean };
  const { method, path } = operations[name];
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }
  if (!SAFE.has(method) && csrfToken !== undefined) {
    headers['X-CSRF-Token'] = csrfToken;
  }

  let response: Response;
  try {
    response = await fetch(urlOf(path, options.params, options.query), {
      method,
      headers,
      body: options.body === undefined ? null : JSON.stringify(options.body),
      credentials: 'same-origin',
      signal: options.signal ?? null,
    });
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') {
      throw cause;
    }
    throw offline();
  }

  if (!response.ok) {
    const problem = await problemOf(response);
    if (response.status === 401 && options.quiet401 !== true) {
      onUnauthorized?.();
    }
    throw problem;
  }
  if (response.status === 204) {
    // No body: the routes that answer 204 are the ones whose result the description leaves empty.
    return undefined;
  }
  return (await response.json()) as Operations[N]['result'];
}

/** Why the backend is not serving yet, in its own words. The health route answers with or without a session. */
export async function healthReason(signal?: AbortSignal): Promise<string | undefined> {
  try {
    const response = await fetch(operations.Health.path, { headers: { Accept: 'application/json' }, signal: signal ?? null });
    const body: unknown = await response.json();
    if (typeof body === 'object' && body !== null && 'reason' in body && typeof body.reason === 'string') {
      return body.reason;
    }
  } catch {
    // No answer, or not the health body: there is no reason to show.
  }
  return undefined;
}

export { ApiError };
