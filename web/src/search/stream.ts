// The search route answers as a stream of events. A browser's EventSource cannot send a body or a header, so the
// stream is read here: bytes to frames, frames to the shapes the page draws. A frame that is not one of the
// shapes is an error of its own kind, never a guess.

export interface Warning {
  code: string;
  offset: number;
  length: number;
  message: string;
}

export interface Span {
  from: string | null;
  to: string | null;
  undated_events: number;
  future_events: number;
}

export interface Meta {
  kind: 'meta';
  from: string;
  to: string;
  tz: string;
  result: 'events' | 'stats' | 'timechart';
  warnings: Warning[];
  span: Span;
}

export interface Bucket {
  start: string;
  counts: Record<string, number>;
}

export interface Histogram {
  kind: 'histogram';
  span: string;
  total: number;
  buckets: Bucket[];
}

/** One cell of an event: a text, a number, or the map of the fields the catalog does not list. */
export type Cell = string | number | boolean | null | Record<string, string>;

export interface Page {
  kind: 'page';
  first_row: number;
  columns: string[];
  rows: Cell[][];
  next: string | null;
  capped: boolean;
  message: string | undefined;
}

export interface TopValue {
  value: string;
  count: number;
}

export interface Counts {
  kind: 'counts';
  total: number;
  /** `field_stats`: the search narrows nothing, and the values of the case's statistics are these counts. */
  source: string;
  /** The most frequent values of each field the server counted for this search. */
  fields: Array<{ name: string; top: TopValue[] }>;
}

export interface Table {
  kind: 'table';
  columns: string[];
  rows: string[][];
  truncated: boolean;
  total_rows: number;
  values_capped_rows: number[];
}

export interface Done {
  kind: 'done';
  duration_ms: number;
}

export interface StreamError {
  kind: 'error';
  code: string;
  message: string;
}

export type Frame = Meta | Histogram | Page | Counts | Table | Done | StreamError;

/** The stream sent something this client does not know how to read. */
export class FrameError extends Error {
  constructor(what: string) {
    super(`the server sent ${what}`);
    this.name = 'FrameError';
  }
}

type Json = Record<string, unknown>;

function object(value: unknown): value is Json {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function text(o: Json, name: string): string {
  const value = o[name];
  if (typeof value !== 'string') {
    throw new FrameError(`an event without "${name}"`);
  }
  return value;
}

function count(o: Json, name: string): number {
  const value = o[name];
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new FrameError(`an event without "${name}"`);
  }
  return value;
}

function list(o: Json, name: string): unknown[] {
  const value = o[name];
  if (!Array.isArray(value)) {
    throw new FrameError(`an event without "${name}"`);
  }
  return value;
}

function strings(o: Json, name: string): string[] {
  const value = list(o, name);
  if (!value.every((v) => typeof v === 'string')) {
    throw new FrameError(`"${name}" that is not a list of names`);
  }
  return value;
}

function instantOrNull(o: Json, name: string): string | null {
  const value = o[name];
  if (value !== null && typeof value !== 'string') {
    throw new FrameError(`a span without "${name}"`);
  }
  return value;
}

function cell(value: unknown): Cell {
  if (value === null || typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
    return value;
  }
  if (object(value)) {
    const map: Record<string, string> = {};
    for (const [key, v] of Object.entries(value)) {
      map[key] = typeof v === 'string' ? v : JSON.stringify(v);
    }
    return map;
  }
  return JSON.stringify(value);
}

/** One event of the stream as the page reads it. The event's name and the kind in its data must agree. */
export function decode(event: string, data: string): Frame {
  let parsed: unknown;
  try {
    parsed = JSON.parse(data);
  } catch {
    throw new FrameError('an event that is not JSON');
  }
  if (!object(parsed) || parsed['kind'] !== event) {
    throw new FrameError('an event whose kind is not its name');
  }
  const o = parsed;
  switch (event) {
    case 'meta': {
      const result = text(o, 'result');
      if (result !== 'events' && result !== 'stats' && result !== 'timechart') {
        throw new FrameError(`a result of the kind "${result}"`);
      }
      const span = o['span'];
      if (!object(span)) {
        throw new FrameError('a meta without the span of the case');
      }
      return {
        kind: 'meta',
        from: text(o, 'from'),
        to: text(o, 'to'),
        tz: text(o, 'tz'),
        result,
        warnings: list(o, 'warnings').map((w) => {
          if (!object(w)) {
            throw new FrameError('a warning that is not an object');
          }
          return { code: text(w, 'code'), offset: count(w, 'offset'), length: count(w, 'length'), message: text(w, 'message') };
        }),
        span: { from: instantOrNull(span, 'from'), to: instantOrNull(span, 'to'), undated_events: count(span, 'undated_events'), future_events: count(span, 'future_events') },
      };
    }
    case 'histogram':
      return {
        kind: 'histogram',
        span: text(o, 'span'),
        total: count(o, 'total'),
        buckets: list(o, 'buckets').map((b) => {
          if (!object(b) || !object(b['counts'])) {
            throw new FrameError('a bucket without counts');
          }
          const counts: Record<string, number> = {};
          for (const [severity, n] of Object.entries(b['counts'])) {
            counts[severity] = typeof n === 'number' ? n : 0;
          }
          return { start: text(b, 'start'), counts };
        }),
      };
    case 'page': {
      const next = o['next'];
      const message = o['message'];
      const columns = strings(o, 'columns');
      return {
        kind: 'page',
        first_row: count(o, 'first_row'),
        columns,
        rows: list(o, 'rows').map((row) => {
          if (!Array.isArray(row) || row.length !== columns.length) {
            throw new FrameError('a row that does not have the columns of its page');
          }
          return row.map(cell);
        }),
        next: typeof next === 'string' ? next : null,
        capped: o['capped'] === true,
        message: typeof message === 'string' ? message : undefined,
      };
    }
    case 'counts':
      return {
        kind: 'counts',
        total: count(o, 'total'),
        source: text(o, 'source'),
        fields: list(o, 'fields').map((f) => {
          if (!object(f)) {
            throw new FrameError('a counted field that is not an object');
          }
          return {
            name: text(f, 'name'),
            top: list(f, 'top').map((v) => {
              if (!object(v)) {
                throw new FrameError('a counted value that is not an object');
              }
              return { value: text(v, 'value'), count: count(v, 'count') };
            }),
          };
        }),
      };
    case 'table':
      return {
        kind: 'table',
        columns: strings(o, 'columns'),
        rows: list(o, 'rows').map((row) => {
          if (!Array.isArray(row)) {
            throw new FrameError('a table row that is not a list');
          }
          return row.map((v) => (typeof v === 'string' ? v : v === null ? '' : JSON.stringify(v)));
        }),
        truncated: o['truncated'] === true,
        total_rows: count(o, 'total_rows'),
        values_capped_rows: list(o, 'values_capped_rows').filter((v) => typeof v === 'number'),
      };
    case 'done':
      return { kind: 'done', duration_ms: count(o, 'duration_ms') };
    case 'error':
      return { kind: 'error', code: text(o, 'code'), message: text(o, 'message') };
    default:
      throw new FrameError(`an event of the unknown kind "${event}"`);
  }
}

/** Cuts the text of a stream into events. A frame ends at an empty line; what is left after the last one waits. */
export class FrameCutter {
  private rest = '';

  push(chunk: string): Array<{ event: string; data: string }> {
    this.rest += chunk;
    const out: Array<{ event: string; data: string }> = [];
    for (;;) {
      const end = this.rest.indexOf('\n\n');
      if (end < 0) {
        return out;
      }
      const block = this.rest.slice(0, end);
      this.rest = this.rest.slice(end + 2);
      let event = '';
      const data: string[] = [];
      for (const line of block.split('\n')) {
        if (line.startsWith('event: ')) {
          event = line.slice(7);
        } else if (line.startsWith('data: ')) {
          data.push(line.slice(6));
        }
      }
      if (event !== '' || data.length > 0) {
        out.push({ event, data: data.join('\n') });
      }
    }
  }

  /** True when the stream ended inside a frame. */
  get pending(): boolean {
    return this.rest.trim() !== '';
  }
}

/**
 * Reads a stream to its end and hands every event on. It resolves with whether the stream ended on a frame
 * boundary; an end inside a frame, or without a closing event, is the caller's to call a cut.
 */
export async function read(body: ReadableStream<Uint8Array>, onFrame: (frame: Frame) => void): Promise<{ whole: boolean }> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  const cutter = new FrameCutter();
  for (;;) {
    const { done, value } = await reader.read();
    const chunk = done ? decoder.decode() : decoder.decode(value, { stream: true });
    for (const { event, data } of cutter.push(chunk)) {
      onFrame(decode(event, data));
    }
    if (done) {
      return { whole: !cutter.pending };
    }
  }
}
