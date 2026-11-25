import { afterEach, describe, expect, it, vi } from 'vitest';
import { setCsrfToken } from '../api/http';
import { events, fakeServer } from '../test-server';
import { hexDump, linePart, partsQuery, rawUrl, TooManyParts, wholeLine, type EventKey } from './bytes';

const key: EventKey = { caseId: 7, fileId: 11, offset: 65_564, ts: '2026-03-01T00:00:05.000Z', host: 'web-01' };

afterEach(() => {
  setCsrfToken(undefined);
  vi.unstubAllGlobals();
});

describe('the bytes of a line', () => {
  it('are asked for by the key of the event, each part of it one encoded value', () => {
    expect(rawUrl(key)).toBe('/api/v1/cases/7/files/11/events/65564/raw?ts=2026-03-01T00%3A00%3A05.000Z&host=web-01');
    // A host out of a log is whatever was written there.
    expect(rawUrl({ ...key, host: 'a&b=c#d <x>' })).toBe('/api/v1/cases/7/files/11/events/65564/raw?ts=2026-03-01T00%3A00%3A05.000Z&host=a%26b%3Dc%23d+%3Cx%3E');
    // An event without a host still names one: the empty one.
    expect(rawUrl({ ...key, host: '' })).toMatch(/&host=$/);
  });

  it('are drawn as hex digits, dots and printable ASCII and nothing else, whatever they are', () => {
    const every = new Uint8Array(256).map((_, i) => i);
    const dump = hexDump(every);
    expect(dump.split('\n')).toHaveLength(16);
    expect(dump.split('\n')[0]).toBe('00000000  00 01 02 03 04 05 06 07  08 09 0a 0b 0c 0d 0e 0f  |................|');
    expect(dump.split('\n')[3]).toBe('00000030  30 31 32 33 34 35 36 37  38 39 3a 3b 3c 3d 3e 3f  |0123456789:;<=>?|');
    expect(dump.split('\n')[15]).toBe('000000f0  f0 f1 f2 f3 f4 f5 f6 f7  f8 f9 fa fb fc fd fe ff  |................|');
    // No control byte, no byte above ASCII, no line break but its own.
    expect(/^[\x20-\x7e\n]*$/.test(dump)).toBe(true);
  });

  it('are drawn up to a limit, the last line as long as it is', () => {
    expect(hexDump(new Uint8Array(10_000)).split('\n')).toHaveLength(256);
    expect(hexDump(new Uint8Array([0x41, 0x42, 0x43]))).toBe('00000000  41 42 43                                          |ABC|');
    expect(hexDump(new Uint8Array(0))).toBe('');
  });
});

describe('a line stored as several events', () => {
  it('is known by the marks of its pieces', () => {
    expect(linePart({ 'line.first_offset': '28', 'line.part': '2/3' })).toEqual({ firstOffset: 28, part: 2, total: 3 });
    // The total is not known while the reader of the file had cut the line.
    expect(linePart({ 'line.first_offset': '28', 'line.part': '2' })).toEqual({ firstOffset: 28, part: 2, total: undefined });
    expect(linePart({ 'timestamp.source': 'previous' })).toBeUndefined();
    expect(linePart({ 'line.first_offset': '<img>', 'line.part': '2/3' })).toBeUndefined();
    expect(linePart({ 'line.first_offset': '28', 'line.part': '2 of 3' })).toBeUndefined();
    expect(linePart(undefined)).toBeUndefined();
    expect(linePart('text')).toBeUndefined();
  });

  it('is found again by a query of numbers alone, in the order of the file', () => {
    expect(partsQuery(11, 28)).toBe('file.id=11 line.first_offset="28" | sort file.offset');
  });

  const COLUMNS = ['_time', 'host.name', 'file.id', 'file.offset', 'extras'];
  const pieces = (offsets: number[], next: string | null = null) =>
    events([
      { event: 'meta', data: { kind: 'meta', from: 'a', to: 'b', tz: 'UTC', result: 'events', columns: [], warnings: [], fields: [], span: { from: null, to: null, undated_events: 0, future_events: 0 } } },
      { event: 'page', data: { kind: 'page', first_row: 0, columns: COLUMNS, rows: offsets.map((offset) => ['2026-03-01T00:00:05.000Z', 'web-01', 11, offset, {}]), next, capped: false } },
      { event: 'done', data: { kind: 'done', duration_ms: 1 } },
    ]);

  it('is joined from the bytes of its pieces, fetched in the order of the file', async () => {
    const server = fakeServer({
      'POST /api/v1/cases/7/search': pieces([28, 65_564, 131_100]),
      'GET /api/v1/cases/7/files/11/events/28/raw': { status: 200, bytes: new Uint8Array([1, 2, 3]) },
      'GET /api/v1/cases/7/files/11/events/65564/raw': { status: 200, bytes: new Uint8Array([4, 0, 255]) },
      'GET /api/v1/cases/7/files/11/events/131100/raw': { status: 200, bytes: new Uint8Array([9]) },
    });
    const progress: string[] = [];
    const whole = await wholeLine(key, 28, new AbortController().signal, (done, of) => progress.push(`${done}/${of}`));

    expect([...whole]).toEqual([1, 2, 3, 4, 0, 255, 9]);
    expect(progress).toEqual(['1/3', '2/3', '3/3']);
    // The pieces carry the time of their line: the search covers that one instant.
    expect(server.sent[0]?.body).toEqual({ query: 'file.id=11 line.first_offset="28" | sort file.offset', from: '2026-03-01T00:00:05.000Z', to: '2026-03-01T00:00:05.001Z' });
    expect(server.sent.slice(1).map((s) => s.path.split('?')[0])).toEqual(['/api/v1/cases/7/files/11/events/28/raw', '/api/v1/cases/7/files/11/events/65564/raw', '/api/v1/cases/7/files/11/events/131100/raw']);
  });

  it('is not joined when it has more parts than one page of a listing holds', async () => {
    const server = fakeServer({ 'POST /api/v1/cases/7/search': pieces([28, 65_564], 'CURSOR') });
    await expect(wholeLine(key, 28, new AbortController().signal, () => undefined)).rejects.toBeInstanceOf(TooManyParts);
    expect(server.sent).toHaveLength(1);
  });

  it('is not saved in part: a piece that cannot be read fails the whole', async () => {
    fakeServer({
      'POST /api/v1/cases/7/search': pieces([28, 65_564]),
      'GET /api/v1/cases/7/files/11/events/28/raw': { status: 200, bytes: new Uint8Array([1]) },
      'GET /api/v1/cases/7/files/11/events/65564/raw': { status: 404, body: { type: 'https://urlog/problems/not-found', title: 'no event of this case has that file, offset, time and host', status: 404 } },
    });
    await expect(wholeLine(key, 28, new AbortController().signal, () => undefined)).rejects.toThrow('no event of this case');
  });
});
