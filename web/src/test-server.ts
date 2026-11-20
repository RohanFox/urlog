import { vi } from 'vitest';

// A stand-in for the backend in unit tests: answers by method and path, and keeps what it was sent.

export interface Sent {
  method: string;
  path: string;
  headers: Record<string, string>;
  body: unknown;
}

type Answer = { status: number; body?: unknown; headers?: Record<string, string> } | (() => { status: number; body?: unknown; headers?: Record<string, string> });

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
    const route = table.get(`${method} ${path}`);
    if (route === undefined) {
      return Promise.reject(new TypeError(`no route for ${method} ${path}`));
    }
    const answer = typeof route === 'function' ? route() : route;
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
