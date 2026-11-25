import { ApiError, open as openStream } from '../api/http';
import { FrameError, read, type Cell, type Counts, type Frame, type Histogram, type Meta, type Table } from './stream';

// One search from its request to its end. The page holds one of these at a time; starting another cancels the
// one before, and a cancelled run changes nothing more, whatever still arrives for it.

export interface SearchRequest {
  caseId: number;
  q: string;
  from: string | undefined;
  to: string | undefined;
  tz: string | undefined;
}

/** A refusal of the request, or a failure after the stream began, in the server's words. */
export interface Refusal {
  status: number;
  code: string | undefined;
  message: string;
  detail: string | undefined;
  offset: number | undefined;
  length: number | undefined;
}

export type Phase =
  /** Sent, no answer yet. */
  | 'waiting'
  /** Answered 429: the request is sent again when the wait is over. */
  | 'busy'
  /** The stream is open; parts arrive. */
  | 'streaming'
  | 'done'
  /** Refused before the first byte. */
  | 'refused'
  /** An error event ended the stream. */
  | 'failed'
  /** The stream ended without its closing event, or the connection went away. */
  | 'cut'
  | 'cancelled';

export type More = { kind: 'idle' } | { kind: 'loading' } | { kind: 'busy'; seconds: number; attempt: number } | { kind: 'error'; refusal: Refusal };

export interface RunState {
  request: SearchRequest;
  phase: Phase;
  /** While busy: seconds until the next try, and which try that is. */
  wait: { seconds: number; attempt: number } | undefined;
  refusal: Refusal | undefined;
  meta: Meta | undefined;
  histogram: Histogram | undefined;
  /** True once the first page has arrived, also when it holds no row. */
  paged: boolean;
  columns: string[];
  rows: Cell[][];
  next: string | null;
  capped: boolean;
  cappedMessage: string | undefined;
  counts: Counts | undefined;
  table: Table | undefined;
  durationMs: number | undefined;
  more: More;
}

/** How often a request answered "busy" is sent again before the page calls it an error. */
export const BUSY_TRIES = 5;

type Open = typeof openStream;

function refusalOf(cause: unknown): Refusal {
  if (cause instanceof ApiError) {
    return { status: cause.status, code: cause.code ?? (cause.offline ? 'offline' : undefined), message: cause.message, detail: cause.detail, offset: cause.offset, length: cause.length };
  }
  if (cause instanceof FrameError) {
    return { status: 0, code: 'bad_frame', message: cause.message, detail: undefined, offset: undefined, length: undefined };
  }
  return { status: 0, code: 'offline', message: 'the connection was lost', detail: undefined, offset: undefined, length: undefined };
}

function aborted(cause: unknown): boolean {
  return cause instanceof DOMException && cause.name === 'AbortError';
}

export class SearchRun {
  state: RunState;

  private readonly listeners = new Set<() => void>();
  private readonly abort = new AbortController();
  private readonly open: Open;
  private gone = false;
  private started = false;

  constructor(request: SearchRequest, open: Open = openStream) {
    this.open = open;
    this.state = {
      request,
      phase: 'waiting',
      wait: undefined,
      refusal: undefined,
      meta: undefined,
      histogram: undefined,
      paged: false,
      columns: [],
      rows: [],
      next: null,
      capped: false,
      cappedMessage: undefined,
      counts: undefined,
      table: undefined,
      durationMs: undefined,
      more: { kind: 'idle' },
    };
  }

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  snapshot = (): RunState => this.state;

  /** True until the run has ended one way or another. */
  get live(): boolean {
    return this.state.phase === 'waiting' || this.state.phase === 'busy' || this.state.phase === 'streaming';
  }

  start(): void {
    if (this.started) {
      return;
    }
    this.started = true;
    void this.first();
  }

  /** Ends the run. Nothing that arrives afterwards is kept. */
  cancel(): void {
    if (this.gone) {
      return;
    }
    const live = this.live;
    this.gone = true;
    this.abort.abort();
    if (live) {
      this.state = { ...this.state, phase: 'cancelled', wait: undefined, more: { kind: 'idle' } };
      this.tell();
    }
  }

  /** Asks for the page after the last one, once. */
  more(): void {
    const s = this.state;
    if (this.gone || s.next === null || s.more.kind === 'loading' || s.more.kind === 'busy') {
      return;
    }
    void this.page(s.next);
  }

  private set(change: Partial<RunState>): void {
    if (this.gone) {
      return;
    }
    this.state = { ...this.state, ...change };
    this.tell();
  }

  private tell(): void {
    for (const listener of this.listeners) {
      listener();
    }
  }

  private body(cursor?: string): Record<string, string> {
    const r = this.state.request;
    const body: Record<string, string> = { query: r.q };
    if (cursor !== undefined) {
      // The cursor carries the range and the zone of its search.
      body['cursor'] = cursor;
      return body;
    }
    if (r.from !== undefined) {
      body['from'] = r.from;
    }
    if (r.to !== undefined) {
      body['to'] = r.to;
    }
    if (r.tz !== undefined) {
      body['tz'] = r.tz;
    }
    return body;
  }

  /** Sends the request; a "busy" answer is waited out and sent again, up to the limit. */
  private async send(cursor: string | undefined, onWait: (seconds: number, attempt: number) => void): Promise<ReadableStream<Uint8Array>> {
    const url = `/api/v1/cases/${this.state.request.caseId}/search`;
    for (let attempt = 1; ; attempt++) {
      try {
        return await this.open(url, this.body(cursor), this.abort.signal);
      } catch (cause) {
        if (!(cause instanceof ApiError) || cause.status !== 429 || attempt > BUSY_TRIES) {
          throw cause;
        }
        for (let seconds = Math.max(1, cause.retryAfter ?? 1); seconds > 0; seconds--) {
          onWait(seconds, attempt);
          await this.sleep(1000);
        }
      }
    }
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.abort.signal.removeEventListener('abort', stop);
        resolve();
      }, ms);
      const stop = () => {
        clearTimeout(timer);
        reject(new DOMException('aborted', 'AbortError'));
      };
      this.abort.signal.addEventListener('abort', stop, { once: true });
    });
  }

  private async first(): Promise<void> {
    // What the stream said, kept where the reader below can write it.
    const seen = { end: false };
    try {
      const body = await this.send(undefined, (seconds, attempt) => {
        this.set({ phase: 'busy', wait: { seconds, attempt } });
      });
      this.set({ phase: 'streaming', wait: undefined });
      const { whole } = await read(body, (frame) => {
        seen.end = this.take(frame) || seen.end;
      });
      if (!seen.end || !whole) {
        this.set({ phase: 'cut' });
      }
    } catch (cause) {
      if (aborted(cause)) {
        return;
      }
      if (this.state.phase === 'streaming') {
        // The stream had begun: what arrived stays, and the rest is said to be missing.
        this.set(cause instanceof FrameError ? { phase: 'failed', refusal: refusalOf(cause) } : { phase: 'cut' });
      } else {
        this.set({ phase: 'refused', wait: undefined, refusal: refusalOf(cause) });
      }
    }
  }

  /** Puts one event of the first stream into the state. True when it was the closing one. */
  private take(frame: Frame): boolean {
    switch (frame.kind) {
      case 'meta':
        this.set({ meta: frame });
        return false;
      case 'histogram':
        this.set({ histogram: frame });
        return false;
      case 'page':
        this.set({ paged: true, columns: frame.columns, rows: frame.rows, next: frame.next, capped: frame.capped, cappedMessage: frame.message });
        return false;
      case 'counts':
        this.set({ counts: frame });
        return false;
      case 'table':
        this.set({ table: frame });
        return false;
      case 'done':
        this.set({ phase: 'done', durationMs: frame.duration_ms });
        return true;
      case 'error':
        this.set({ phase: 'failed', refusal: { status: 0, code: frame.code, message: frame.message, detail: undefined, offset: undefined, length: undefined } });
        return true;
    }
  }

  private async page(cursor: string): Promise<void> {
    this.set({ more: { kind: 'loading' } });
    try {
      const body = await this.send(cursor, (seconds, attempt) => {
        this.set({ more: { kind: 'busy', seconds, attempt } });
      });
      this.set({ more: { kind: 'loading' } });
      const seen: { page: boolean; failure: Refusal | undefined } = { page: false, failure: undefined };
      await read(body, (frame) => {
        if (frame.kind === 'page') {
          seen.page = true;
          this.set({ rows: [...this.state.rows, ...frame.rows], next: frame.next, capped: frame.capped, cappedMessage: frame.message });
        } else if (frame.kind === 'error') {
          seen.failure = { status: 0, code: frame.code, message: frame.message, detail: undefined, offset: undefined, length: undefined };
        }
      });
      this.set({ more: seen.page ? { kind: 'idle' } : { kind: 'error', refusal: seen.failure ?? refusalOf(undefined) } });
    } catch (cause) {
      if (aborted(cause)) {
        return;
      }
      const refusal = refusalOf(cause);
      if (refusal.code === 'cursor_limit') {
        // The server's own limit, met by a request instead of by a page: the same end of the list.
        this.set({ next: null, capped: true, cappedMessage: refusal.message, more: { kind: 'idle' } });
        return;
      }
      this.set({ more: { kind: 'error', refusal } });
    }
  }
}
