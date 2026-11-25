/** A refusal or a failure of a request, in one shape whatever its origin. */
export class ApiError extends Error {
  /** The HTTP status, or 0 when no answer arrived at all. */
  readonly status: number;
  /** The last segment of the problem type: `invalid-credentials`, `throttled`, `not-ready`, `conflict`, ... */
  readonly slug: string;
  readonly detail: string | undefined;
  /** The search routes name their refusals: `query_syntax`, `busy`, ... */
  readonly code: string | undefined;
  /** Seconds the server asks the caller to wait. */
  readonly retryAfter: number | undefined;
  /** Where in the query text a syntax error stands, in UTF-16 units as the server counts them. */
  readonly offset: number | undefined;
  readonly length: number | undefined;

  constructor(init: { status: number; slug: string; title: string; detail?: string | undefined; code?: string | undefined; retryAfter?: number | undefined; offset?: number | undefined; length?: number | undefined }) {
    super(init.title);
    this.name = 'ApiError';
    this.status = init.status;
    this.slug = init.slug;
    this.detail = init.detail;
    this.code = init.code;
    this.retryAfter = init.retryAfter;
    this.offset = init.offset;
    this.length = init.length;
  }

  /** No answer arrived: the backend is down or the network is. */
  get offline(): boolean {
    return this.status === 0;
  }
}

function text(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function whole(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : undefined;
}

function seconds(header: string | null): number | undefined {
  if (header === null) {
    return undefined;
  }
  const n = Number(header);
  return Number.isFinite(n) && n >= 0 ? Math.ceil(n) : undefined;
}

/** Reads the problem body of an answer that is not a success. An answer without one still becomes an ApiError. */
export async function problemOf(response: Response): Promise<ApiError> {
  let body: Record<string, unknown> = {};
  try {
    const parsed: unknown = await response.json();
    if (typeof parsed === 'object' && parsed !== null) {
      body = parsed as Record<string, unknown>;
    }
  } catch {
    // Not JSON: a proxy's page, or nothing. The status is all there is.
  }
  const type = text(body['type']) ?? '';
  return new ApiError({
    status: response.status,
    slug: type.slice(type.lastIndexOf('/') + 1),
    title: text(body['title']) ?? `the server answered ${response.status}`,
    detail: text(body['detail']),
    code: text(body['code']),
    retryAfter: seconds(response.headers.get('Retry-After')),
    offset: whole(body['offset']),
    length: whole(body['length']),
  });
}

/** The request never got an answer. */
export function offline(): ApiError {
  return new ApiError({ status: 0, slug: 'offline', title: 'no answer from the server' });
}
