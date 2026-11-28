import { describe, expect, it } from 'vitest';
import { problemOf } from '../api/problem';
import samples from '../api/samples/search-frames.json';
import { sse } from '../test-server';
import { decode, FrameCutter, FrameError, read, type Frame } from './stream';

// The sample file is written by a test of the backend from what the real handlers send. Reading it here through
// the decoders holds the two sides to one shape.

type Sample = ReadonlyArray<{ event: string; data: unknown }>;

function decodeAll(frames: Sample): Frame[] {
  return frames.map((frame) => decode(frame.event, JSON.stringify(frame.data)));
}

function body(...chunks: string[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(encoder.encode(chunk));
      }
      controller.close();
    },
  });
}

describe('the frames the backend writes', () => {
  it('reads a listing: meta with its warnings and the span of the case, then histogram, page and counts, then done', () => {
    const frames = decodeAll(samples.listing);
    expect(frames.map((f) => f.kind)).toEqual(['meta', 'counts', 'histogram', 'page', 'done']);

    const meta = frames[0];
    expect(meta?.kind === 'meta' && meta.result).toBe('events');
    expect(meta?.kind === 'meta' && meta.warnings.map((w) => [w.code, w.offset, w.length])).toEqual([
      ['unknown_field', 0, 11],
      ['unindexed', 0, 13],
    ]);
    expect(meta?.kind === 'meta' && meta.span).toEqual({ from: '2026-03-01T00:00:00.000Z', to: '2026-03-01T00:00:05.000Z', undated_events: 12, future_events: 1 });

    const page = frames[3];
    expect(page?.kind === 'page' && page.columns).toContain('raw');
    expect(page?.kind === 'page' && page.rows).toHaveLength(2);
    expect(page?.kind === 'page' && page.next).toBe('CURSOR');
    expect(page?.kind === 'page' && page.capped).toBe(false);
    // The map of the fields the catalog does not list arrives as a map of texts.
    const extras = page?.kind === 'page' ? page.rows[0]?.[page.columns.indexOf('extras')] : undefined;
    expect(extras).toEqual({ k: 'v' });
  });

  it('reads the level of each warning and the terms the server searched as text', () => {
    const meta = decodeAll(samples.listing)[0];
    expect(meta?.kind === 'meta' && meta.warnings.map((w) => [w.code, w.level])).toEqual([
      ['unknown_field', 'meaning'],
      ['unindexed', 'cost'],
    ]);
    expect(meta?.kind === 'meta' && meta.terms).toEqual([{ text: 'line', kind: 'word', negated: false, offset: 17, length: 4 }]);
    expect(meta?.kind === 'meta' && meta.fields).toEqual(['nosuchfield']);
  });

  it('reads the meta of a server that sends no level and no terms: every warning a note, no term', () => {
    const data = samples.listing[0]?.data as Record<string, unknown>;
    const old = { ...data, warnings: (data['warnings'] as Array<Record<string, unknown>>).map((w) => ({ code: w['code'], offset: w['offset'], length: w['length'], message: w['message'] })) };
    delete (old as Record<string, unknown>)['terms'];
    delete (old as Record<string, unknown>)['fields'];
    const meta = decode('meta', JSON.stringify(old));
    expect(meta.kind === 'meta' && meta.warnings.map((w) => w.level)).toEqual(['cost', 'cost']);
    expect(meta.kind === 'meta' && meta.terms).toEqual([]);
    expect(meta.kind === 'meta' && meta.fields).toEqual([]);
  });

  it('takes a level it does not know for a reading, never for a note', () => {
    const data = samples.listing[0]?.data as Record<string, unknown>;
    const later = { ...data, warnings: [{ code: 'later', level: 'later', offset: 0, length: 1, message: 'm' }] };
    const meta = decode('meta', JSON.stringify(later));
    expect(meta.kind === 'meta' && meta.warnings[0]?.level).toBe('meaning');
  });

  it('reads the page that reaches the paging limit, with the message of the server', () => {
    const page = decodeAll(samples.page_at_the_limit)[0];
    expect(page?.kind === 'page' && [page.capped, page.next, page.first_row]).toEqual([true, null, 2]);
    expect(page?.kind === 'page' && page.message).toContain('narrow the search');
  });

  it('reads an aggregation that was cut, with the row whose values were capped', () => {
    const table = decodeAll(samples.aggregation)[1];
    expect(table?.kind === 'table' && [table.truncated, table.total_rows, table.rows.length, table.values_capped_rows]).toEqual([true, 3, 2, [1]]);
    expect(table?.kind === 'table' && table.columns).toEqual(['host.name', 'count']);
  });

  it('reads the error that ends a stream', () => {
    const frames = decodeAll(samples.failed_in_the_stream);
    expect(frames.at(-1)).toEqual({ kind: 'error', code: 'query_timeout', message: 'Code: 159. DB::Exception: Timeout exceeded: elapsed 60.0 seconds' });
  });

  it('reads every refusal: its code, where a syntax error stands, how long to wait', async () => {
    const answer = (refusal: { status: number; retry_after: string | null; body: unknown }) =>
      problemOf(new Response(JSON.stringify(refusal.body), { status: refusal.status, headers: refusal.retry_after === null ? {} : { 'Retry-After': refusal.retry_after } }));

    const syntax = await answer(samples.refusals.query_syntax);
    expect([syntax.status, syntax.code, syntax.offset, syntax.length, syntax.message]).toEqual([400, 'query_syntax', 14, 4, 'not supported in v1: eval']);
    const range = await answer(samples.refusals.bad_request);
    expect([range.code, range.offset]).toEqual(['bad_request', undefined]);
    expect(range.message).toContain("'yesterday' is not a time");
    expect((await answer(samples.refusals.bad_cursor)).code).toBe('bad_cursor');
    const busy = await answer(samples.refusals.busy);
    expect([busy.status, busy.code, busy.retryAfter]).toEqual([429, 'busy', 1]);
  });
});

describe('what is not a frame', () => {
  it.each([
    ['meta', '{"kind":"page"}', 'whose kind is not its name'],
    ['meta', 'not json', 'not JSON'],
    ['progress', '{"kind":"progress"}', 'unknown kind'],
    ['page', '{"kind":"page","first_row":0,"columns":["a","b"],"rows":[["only one"]],"next":null,"capped":false}', 'does not have the columns'],
    ['done', '{"kind":"done"}', 'duration_ms'],
  ])('refuses %s %s', (event, data, said) => {
    expect(() => decode(event, data)).toThrow(FrameError);
    expect(() => decode(event, data)).toThrow(said);
  });
});

describe('cutting a stream into frames', () => {
  const text = sse(samples.listing);

  it('finds every frame whatever the chunks are: one character at a time, or all at once', () => {
    const whole = new FrameCutter().push(text);
    expect(whole.map((f) => f.event)).toEqual(['meta', 'counts', 'histogram', 'page', 'done']);

    const cutter = new FrameCutter();
    const byByte = Array.from({ length: text.length }, (_, i) => text.charAt(i)).flatMap((ch) => cutter.push(ch));
    expect(byByte).toEqual(whole);
    expect(cutter.pending).toBe(false);
  });

  it('keeps a frame that is split across two chunks until its end arrives', () => {
    const cutter = new FrameCutter();
    const half = Math.floor(text.indexOf('\n\n') / 2);
    expect(cutter.push(text.slice(0, half))).toEqual([]);
    expect(cutter.pending).toBe(true);
    expect(cutter.push(text.slice(half)).length).toBe(5);
  });

  it('reads a body to its end and says whether it ended on a frame', async () => {
    const seen: string[] = [];
    const encoded = new TextEncoder().encode(text);
    // A chunk boundary inside a character of more than one byte must not break the text.
    const euro = sse([{ event: 'error', data: { kind: 'error', code: 'x', message: 'costs 5 € or more' } }]);
    const bytes = new TextEncoder().encode(euro);
    const at = bytes.indexOf(0xe2) + 1;
    const split = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(encoded);
        controller.enqueue(bytes.slice(0, at));
        controller.enqueue(bytes.slice(at));
        controller.close();
      },
    });
    const messages: string[] = [];
    const { whole } = await read(split, (frame) => {
      seen.push(frame.kind);
      if (frame.kind === 'error') {
        messages.push(frame.message);
      }
    });
    expect(whole).toBe(true);
    expect(seen).toEqual(['meta', 'counts', 'histogram', 'page', 'done', 'error']);
    expect(messages).toEqual(['costs 5 € or more']);
  });

  it('says so when the body ends in the middle of a frame', async () => {
    const seen: string[] = [];
    const { whole } = await read(body(text.slice(0, text.length - 40)), (frame) => {
      seen.push(frame.kind);
    });
    expect(whole).toBe(false);
    expect(seen).toEqual(['meta', 'counts', 'histogram', 'page']);
  });
});
