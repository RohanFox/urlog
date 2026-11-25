import { vi } from 'vitest';

// A stand-in for the backend in unit tests: answers by method and path, and keeps what it was sent.

export interface Sent {
  method: string;
  path: string;
  headers: Record<string, string>;
  body: unknown;
}

interface Reply {
  status: number;
  body?: unknown;
  headers?: Record<string, string>;
  /** A body that is not JSON (an event stream), sent as it is. */
  text?: string;
  /** A body the test feeds by hand, to hold an answer open or to end it where it likes. */
  stream?: ReadableStream<Uint8Array>;
}

type Answer = Reply | (() => Reply);

/** The frames of a search as the server writes them. */
export function sse(frames: ReadonlyArray<{ event: string; data: unknown }>): string {
  return frames.map((frame) => `event: ${frame.event}
data: ${JSON.stringify(frame.data)}

`).join('');
}

/** A stream answer: 200 with the frames, as one body. */
export function events(frames: ReadonlyArray<{ event: string; data: unknown }>): Reply {
  return { status: 200, text: sse(frames) };
}

export function problem(status: number, slug: string, title: string, extra: Record<string, unknown> = {}) {
  return { status, body: { type: `https://urlog/problems/${slug}`, title, status, ...extra } };
}

export function fakeServer(routes: Record<string, Answer>) {
  const sent: Sent[] = [];
  const table = new Map(Object.entries(routes));

  vi.stubGlobal('fetch', (input: string, init: RequestInit = {}) => {
    const method = init.method ?? 'GET';
    const path = input;
    const headers = { ...(init.headers as Record<string, string> | undefined) };
    sent.push({ method, path, headers, body: typeof init.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined });
    // A route is found by its whole address, or by its path when the test does not care about the query.
    const route = table.get(`${method} ${path}`) ?? table.get(`${method} ${path.split('?')[0] ?? path}`);
    if (route === undefined) {
      return Promise.reject(new TypeError(`no route for ${method} ${path}`));
    }
    const answer = typeof route === 'function' ? route() : route;
    if (answer.text !== undefined || answer.stream !== undefined) {
      return Promise.resolve(new Response(answer.stream ?? answer.text ?? '', { status: answer.status, headers: { 'Content-Type': 'text/event-stream', ...answer.headers } }));
    }
    const hasBody = answer.body !== undefined && answer.status !== 204;
    return Promise.resolve(
      new Response(hasBody ? JSON.stringify(answer.body) : null, {
        status: answer.status,
        headers: { ...(hasBody ? { 'Content-Type': answer.status >= 400 ? 'application/problem+json' : 'application/json' } : {}), ...answer.headers },
      }),
    );
  });

  return {
    sent,
    /** Replaces or adds a route while a test runs. */
    set(key: string, answer: Answer) {
      table.set(key, answer);
    },
    calls(key: string): Sent[] {
      return sent.filter((s) => `${s.method} ${s.path}` === key);
    },
  };
}

export const alice = { id: 1, name: 'alice', role: 'analyst', locked_until: null, last_login_at: null, created_at: '2026-11-01T10:00:00+00:00' } as const;

export const session = { user: alice, csrf_token: 'token-1' } as const;

/** A case's dashboard as the backend answers it, for a case with one parsed file. A test overrides what it is about. */
export function dashboard(over: Record<string, unknown> = {}) {
  return {
    case: { id: 7, name: 'payroll portal', state: 'active', my_role: 'owner', owner: 'alice', task: null },
    span: { from: '2026-10-01T00:00:04Z', to: '2026-10-08T23:59:58Z', undated_events: 0, future_events: 0 },
    statistics: { state: 'ok', pending: false, computed_at: '2026-11-18T09:14:02Z', reason: null },
    files: { total: 1, arrived: 1, bytes_arrived: 2048, by_state: { parsed: 1 }, by_kind: { log: 1 }, rows: 36, rejects: 2, flags: {} },
    ingest: { queued: 0, parsing: 0, failed: 0, cancelled: 0, done: 1, paused: null, current: [], bytes_done: 2048, bytes_total: 2048 },
    hosts: [{ id: 3, name: 'web-01', timezone: 'UTC', timezone_assumed: true, source_options: null, merged_into: null, files: 1, rows: 36, rejects: 2, from: '2026-10-01T00:00:04Z', to: '2026-10-08T23:59:58Z', version: 1 }],
    unassigned: null,
    usage: null,
    usage_unavailable: null,
    ...over,
  };
}

/** A file record as the list answers it. */
export function fileRecord(over: Record<string, unknown> = {}) {
  return {
    id: 11,
    parent_file_id: null,
    root_file_id: null,
    archive_path: null,
    original_name: 'access.log',
    sha256: 'a3f1c29b7d4e8f60112233445566778899aabbccddeeff00112233445566e07b',
    size: 2048,
    hash_of_stored_bytes: true,
    source: 'web',
    uploader: 'alice',
    received_at: '2026-11-19T08:31:00+00:00',
    state: 'parsed',
    quarantined: false,
    host: { id: 3, name: 'web-01', inferred: false, rule: null },
    timezone: null,
    year: { value: null, given: false, assumed: false },
    encoding: 'utf-8',
    compression: null,
    detection: { kind: 'log', source: 'nginx', confidence: 0.99, layout: 'combined', format: null, stale: false },
    override: null,
    effective: { source: 'nginx', options: {}, options_from: {} },
    parse_generation: 1,
    rows: 36,
    rejects: 2,
    span: { from: '2026-10-01T00:00:04Z', to: '2026-10-08T23:59:58Z', undated_events: 0, future_events: 0 },
    stored_histogram: true,
    flags: [],
    reparse: null,
    settings_applied: true,
    version: 1,
    ...over,
  };
}
