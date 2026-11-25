import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/http';
import samples from '../api/samples/search-frames.json';
import { sse } from '../test-server';
import { BUSY_TRIES, SearchRun, type SearchRequest } from './run';

const request: SearchRequest = { caseId: 7, q: 'status=404', from: '-24h', to: undefined, tz: undefined };

type Frames = ReadonlyArray<{ event: string; data: unknown }>;

const meta = samples.listing[0];
const counts = samples.listing[1];
const histogram = samples.listing[2];
const page = samples.listing[3];
const done = samples.listing[4];

/** A body the test feeds frame by frame and ends, or breaks, when it likes. */
function feed() {
  const encoder = new TextEncoder();
  let controller: ReadableStreamDefaultController<Uint8Array> | undefined;
  const body = new ReadableStream<Uint8Array>({
    start(c) {
      controller = c;
    },
  });
  return {
    body,
    send(frames: Frames) {
      controller?.enqueue(encoder.encode(sse(frames)));
    },
    end() {
      controller?.close();
    },
    fail() {
      controller?.error(new TypeError('network error'));
    },
  };
}

function whole(frames: Frames): ReadableStream<Uint8Array> {
  const f = feed();
  f.send(frames);
  f.end();
  return f.body;
}

function busy(retryAfter = 1): ApiError {
  return new ApiError({ status: 429, slug: 'busy', title: 'too many queries are running', code: 'busy', retryAfter });
}

interface Call {
  url: string;
  body: Record<string, string>;
  signal: AbortSignal;
}

/** The transport of a run, answering in order with what the test lined up. */
function transport(...answers: Array<ReadableStream<Uint8Array> | Error>) {
  const calls: Call[] = [];
  const open = (url: string, body: unknown, signal: AbortSignal) => {
    calls.push({ url, body: body as Record<string, string>, signal });
    const answer = answers.shift();
    if (answer === undefined) {
      return Promise.reject(new Error('no answer lined up'));
    }
    return answer instanceof Error ? Promise.reject(answer) : Promise.resolve(answer);
  };
  return { open, calls };
}

const settle = () => vi.waitFor(() => undefined);

async function until(run: SearchRun, test: () => boolean): Promise<void> {
  await vi.waitFor(() => {
    if (!test()) {
      throw new Error(`still ${run.state.phase}`);
    }
  });
}

afterEach(() => {
  vi.useRealTimers();
});

describe('one search', () => {
  it('sends the query, the range and the zone of its request, and nothing it was not given', async () => {
    const t = transport(whole(samples.listing));
    const run = new SearchRun({ caseId: 7, q: 'a', from: undefined, to: '2026-03-02T00:00:00Z', tz: 'Europe/Bucharest' }, t.open);
    run.start();
    await until(run, () => run.state.phase === 'done');
    expect(t.calls[0]?.url).toBe('/api/v1/cases/7/search');
    expect(t.calls[0]?.body).toEqual({ query: 'a', to: '2026-03-02T00:00:00Z', tz: 'Europe/Bucharest' });
  });

  it.each([
    [[histogram, page, counts]],
    [[counts, page, histogram]],
    [[page, counts, histogram]],
  ])('takes histogram, page and counts in whatever order they arrive', async (order) => {
    const f = feed();
    const run = new SearchRun(request, transport(f.body).open);
    run.start();
    await until(run, () => run.state.phase === 'streaming');

    f.send([meta as Frames[number]]);
    await until(run, () => run.state.meta !== undefined);
    expect([run.state.histogram, run.state.paged, run.state.counts]).toEqual([undefined, false, undefined]);

    // Each part is there as soon as its own event is, whatever the others do.
    for (const frame of order as Frames) {
      f.send([frame]);
      await until(run, () => (frame.event === 'histogram' ? run.state.histogram !== undefined : frame.event === 'page' ? run.state.paged : run.state.counts !== undefined));
      expect(run.state.phase).toBe('streaming');
    }
    f.send([done as Frames[number]]);
    f.end();
    await until(run, () => run.state.phase === 'done');
    expect(run.state.rows).toHaveLength(2);
    expect(run.state.counts?.total).toBe(6);
    expect(run.state.durationMs).toBe(0);
  });

  it('keeps nothing that arrives after it was cancelled, and tells the server by ending the request', async () => {
    const f = feed();
    const t = transport(f.body);
    const run = new SearchRun(request, t.open);
    const told = vi.fn();
    run.subscribe(told);
    run.start();
    await until(run, () => run.state.phase === 'streaming');
    f.send([meta as Frames[number]]);
    await until(run, () => run.state.meta !== undefined);

    run.cancel();
    expect(run.state.phase).toBe('cancelled');
    expect(t.calls[0]?.signal.aborted).toBe(true);
    const after = told.mock.calls.length;

    // Frames of the cancelled run still come down the pipe: none is drawn.
    f.send([histogram as Frames[number], page as Frames[number], counts as Frames[number], done as Frames[number]]);
    f.end();
    await settle();
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect([run.state.histogram, run.state.paged, run.state.counts, run.state.phase]).toEqual([undefined, false, undefined, 'cancelled']);
    expect(told.mock.calls.length).toBe(after);
  });

  it('keeps the parts that arrived when an error event ends the stream', async () => {
    const run = new SearchRun(request, transport(whole([meta as Frames[number], histogram as Frames[number], ...samples.failed_in_the_stream.slice(-1)])).open);
    run.start();
    await until(run, () => run.state.phase === 'failed');
    expect(run.state.histogram).toBeDefined();
    expect(run.state.paged).toBe(false);
    expect(run.state.refusal?.code).toBe('query_timeout');
  });

  it('calls a stream that ends without its closing event a cut', async () => {
    const run = new SearchRun(request, transport(whole([meta as Frames[number], page as Frames[number]])).open);
    run.start();
    await until(run, () => run.state.phase === 'cut');
    expect(run.state.paged).toBe(true);
  });

  it('calls a connection that goes away mid-stream a cut, with what arrived kept', async () => {
    const f = feed();
    const run = new SearchRun(request, transport(f.body).open);
    run.start();
    await until(run, () => run.state.phase === 'streaming');
    f.send([meta as Frames[number], histogram as Frames[number]]);
    await until(run, () => run.state.histogram !== undefined);
    f.fail();
    await until(run, () => run.state.phase === 'cut');
    expect(run.state.histogram).toBeDefined();
  });

  it('is refused before the first byte with the place of a syntax error', async () => {
    const refusal = samples.refusals.query_syntax.body;
    const run = new SearchRun(request, transport(new ApiError({ status: 400, slug: 'query-syntax', title: refusal.title, code: refusal.code, offset: refusal.offset, length: refusal.length })).open);
    run.start();
    await until(run, () => run.state.phase === 'refused');
    expect(run.state.refusal).toMatchObject({ status: 400, code: 'query_syntax', offset: 14, length: 4, message: 'not supported in v1: eval' });
  });

  it('calls an answer it cannot read a failure, never a guess', async () => {
    const run = new SearchRun(request, transport(whole([meta as Frames[number], { event: 'page', data: { kind: 'page', rows: 'none' } }])).open);
    run.start();
    await until(run, () => run.state.phase === 'failed');
    expect(run.state.refusal?.code).toBe('bad_frame');
  });
});

describe('a busy server', () => {
  it('is waited out by Retry-After and asked again, the wait counted down', async () => {
    vi.useFakeTimers();
    const t = transport(busy(3), whole(samples.listing));
    const run = new SearchRun(request, t.open);
    run.start();
    await vi.advanceTimersByTimeAsync(0);
    expect([run.state.phase, run.state.wait]).toEqual(['busy', { seconds: 3, attempt: 1 }]);
    await vi.advanceTimersByTimeAsync(1000);
    expect(run.state.wait).toEqual({ seconds: 2, attempt: 1 });
    expect(t.calls).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(2000);
    await vi.advanceTimersByTimeAsync(0);
    expect(t.calls).toHaveLength(2);
    await vi.waitFor(() => {
      expect(run.state.phase).toBe('done');
    });
    expect(run.state.wait).toBeUndefined();
  });

  it('is an error only after the fifth retry', async () => {
    vi.useFakeTimers();
    const t = transport(...Array.from({ length: BUSY_TRIES + 1 }, () => busy(1)));
    const run = new SearchRun(request, t.open);
    run.start();
    for (let attempt = 1; attempt <= BUSY_TRIES; attempt++) {
      await vi.advanceTimersByTimeAsync(0);
      expect([run.state.phase, run.state.wait?.attempt]).toEqual(['busy', attempt]);
      await vi.advanceTimersByTimeAsync(1000);
    }
    await vi.advanceTimersByTimeAsync(0);
    expect(t.calls).toHaveLength(BUSY_TRIES + 1);
    expect([run.state.phase, run.state.refusal?.code, run.state.refusal?.status]).toEqual(['refused', 'busy', 429]);
  });

  it('stops waiting when the search is cancelled, and asks no more', async () => {
    vi.useFakeTimers();
    const t = transport(busy(5), whole(samples.listing));
    const run = new SearchRun(request, t.open);
    run.start();
    await vi.advanceTimersByTimeAsync(1000);
    run.cancel();
    await vi.advanceTimersByTimeAsync(10_000);
    expect(t.calls).toHaveLength(1);
    expect(run.state.phase).toBe('cancelled');
  });
});

describe('the pages after the first', () => {
  async function listed(...then: Array<ReadableStream<Uint8Array> | Error>) {
    const t = transport(whole(samples.listing), ...then);
    const run = new SearchRun(request, t.open);
    run.start();
    await until(run, () => run.state.phase === 'done');
    return { run, t };
  }

  it('are asked for by the cursor alone, and their rows follow the rows before', async () => {
    const { run, t } = await listed(whole(samples.page_at_the_limit));
    run.more();
    expect(run.state.more.kind).toBe('loading');
    await until(run, () => run.state.more.kind === 'idle');
    expect(t.calls[1]?.body).toEqual({ query: 'status=404', cursor: 'CURSOR' });
    expect(run.state.rows).toHaveLength(4);
    // The page that reaches the limit ends the list and says why.
    expect([run.state.next, run.state.capped]).toEqual([null, true]);
    expect(run.state.cappedMessage).toContain('narrow the search');

    run.more();
    await settle();
    expect(t.calls).toHaveLength(2);
  });

  it('are asked for once, however often the list asks while one is on its way', async () => {
    const f = feed();
    const { run, t } = await listed(f.body);
    run.more();
    run.more();
    run.more();
    await settle();
    expect(t.calls).toHaveLength(2);
    f.send(samples.page_at_the_limit);
    f.end();
    await until(run, () => run.state.more.kind === 'idle');
  });

  it('keep the rows already loaded when one is refused, and can be asked for again', async () => {
    const { run, t } = await listed(new ApiError({ status: 504, slug: 'query-timeout', title: 'the query passed its time limit', code: 'query_timeout' }), whole(samples.page_at_the_limit));
    run.more();
    await until(run, () => run.state.more.kind === 'error');
    expect(run.state.rows).toHaveLength(2);
    expect(run.state.more).toMatchObject({ kind: 'error', refusal: { code: 'query_timeout' } });
    expect(run.state.next).toBe('CURSOR');

    run.more();
    await until(run, () => run.state.more.kind === 'idle');
    expect(run.state.rows).toHaveLength(4);
    expect(t.calls).toHaveLength(3);
  });

  it('end the list with the server\'s words when the cursor stands at the paging limit', async () => {
    const { run } = await listed(new ApiError({ status: 400, slug: 'cursor-limit', title: 'narrow the search or the time range: a result is paged to 10,000 events', code: 'cursor_limit' }));
    run.more();
    await until(run, () => run.state.capped);
    expect([run.state.next, run.state.more.kind]).toEqual([null, 'idle']);
    expect(run.state.cappedMessage).toContain('paged to 10,000 events');
  });

  it('say that the list cannot be continued when the cursor is no longer one of this search', async () => {
    const refusal = samples.refusals.bad_cursor.body;
    const { run } = await listed(new ApiError({ status: 400, slug: 'bad-cursor', title: refusal.title, code: refusal.code }));
    run.more();
    await until(run, () => run.state.more.kind === 'error');
    expect(run.state.more).toMatchObject({ refusal: { code: 'bad_cursor' } });
  });

  it('wait out a busy server like the first request', async () => {
    const { run, t } = await listed(busy(1), whole(samples.page_at_the_limit));
    vi.useFakeTimers();
    run.more();
    await vi.advanceTimersByTimeAsync(0);
    expect(run.state.more).toEqual({ kind: 'busy', seconds: 1, attempt: 1 });
    await vi.advanceTimersByTimeAsync(1000);
    await vi.advanceTimersByTimeAsync(0);
    vi.useRealTimers();
    await until(run, () => run.state.more.kind === 'idle');
    expect(t.calls).toHaveLength(3);
    expect(run.state.rows).toHaveLength(4);
  });
});
