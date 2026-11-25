import { describe, expect, it } from 'vitest';
import samples from '../api/samples/search-frames.json';
import { decode } from './stream';
import { addTerm, cannotFilter, quote, term } from './terms';
import { instant, knownZone } from './time';
import { move, rangeOf, seriesOf, slots, spanMs, stretch, within } from './timelineModel';
import { format, futureRange, parse, runKey, UNDATED, type SearchUrl } from './urlState';

const EMPTY: SearchUrl = { q: '', from: undefined, to: undefined, tz: undefined, cols: [], sel: undefined };

describe('the address of a search', () => {
  it('reads an empty address as the whole case in UTC', () => {
    expect(parse(new URLSearchParams(''))).toEqual(EMPTY);
    expect(format(EMPTY)).toBe('');
  });

  it('goes to an address and back unchanged, whatever the query holds', () => {
    const url: SearchUrl = { q: 'url.path="/a b" <script>&q=1#x | stats count by host.name', from: '-7d@d', to: '2026-03-02T00:00:00Z', tz: 'Europe/Bucharest', cols: ['source.ip', 'url.path'], sel: { file: 4, offset: 1073741800 } };
    const text = format(url);
    expect(text.startsWith('?')).toBe(true);
    // The query travels as one encoded value: nothing of it becomes a parameter, a fragment or markup.
    expect(text).not.toMatch(/[<>#" ]/);
    expect(parse(new URLSearchParams(text.slice(1)))).toEqual(url);
  });

  it('keeps at most three extra columns and no column twice', () => {
    expect(parse(new URLSearchParams('cols=a,b,a,,c,d')).cols).toEqual(['a', 'b', 'c']);
  });

  it('reads a selection only when it is a file and an offset', () => {
    expect(parse(new URLSearchParams('sel=3:900')).sel).toEqual({ file: 3, offset: 900 });
    for (const bad of ['3', 'a:b', '3:900:1', '-1:2', '<img>:1']) {
      expect(parse(new URLSearchParams({ sel: bad })).sel).toBeUndefined();
    }
  });

  it('counts the query, the range and the zone as the run, and nothing else', () => {
    const a = runKey(7, { ...EMPTY, q: 'x' });
    expect(runKey(7, { ...EMPTY, q: 'x', cols: ['a'], sel: { file: 1, offset: 2 } })).toBe(a);
    for (const other of [{ q: 'y' }, { q: 'x', from: '-1h' }, { q: 'x', to: 'now' }, { q: 'x', tz: 'Europe/Bucharest' }]) {
      expect(runKey(7, { ...EMPTY, ...other })).not.toBe(a);
    }
    expect(runKey(8, { ...EMPTY, q: 'x' })).not.toBe(a);
  });

  it('names the ranges of the events a default search leaves out', () => {
    expect(UNDATED).toEqual({ from: '1970-01-01T00:00:00Z', to: '1980-01-01T00:00:00Z' });
    // From just after the last counted event of the case to the latest instant the time grammar takes.
    expect(futureRange('2026-06-21T23:13:23.829Z')).toEqual({ from: '2026-06-21T23:13:23.830Z', to: '9999-12-31T23:59:59Z' });
  });
});

describe('a comparison written by a click', () => {
  it('quotes every value by the rules of the query language', () => {
    expect(quote('plain')).toBe('"plain"');
    expect(quote('say "hi"')).toBe('"say \\"hi\\""');
    expect(quote('C:\\temp\\')).toBe('"C:\\\\temp\\\\"');
    expect(term('url.path', '/a | stats count', false)).toBe('url.path="/a | stats count"');
    expect(term('user.name', 'root', true)).toBe('user.name!="root"');
  });

  it('adds it to the search part, before any command, and inside quotes sees no pipe', () => {
    expect(addTerm('', 'a="1"')).toBe('a="1"');
    expect(addTerm('status=404', 'a="1"')).toBe('status=404 a="1"');
    expect(addTerm('status=404 | stats count by host.name', 'a="1"')).toBe('status=404 a="1" | stats count by host.name');
    expect(addTerm('| head 5', 'a="1"')).toBe('a="1" | head 5');
    expect(addTerm('msg="a | b" | head 5', 'a="1"')).toBe('msg="a | b" a="1" | head 5');
    expect(addTerm('msg="q\\" | x" | head 5', 'a="1"')).toBe('msg="q\\" | x" a="1" | head 5');
    // An OR would bind looser than the new term.
    expect(addTerm('a=1 OR b=2', 'c="3"')).toBe('(a=1 OR b=2) c="3"');
  });

  it('is not offered for a value or a name the language cannot hold', () => {
    expect(cannotFilter('url.path', '/index.html')).toBeUndefined();
    expect(cannotFilter('log.syslog.structured_data.exampleSDID@32473.eventID', '1011')).toBeUndefined();
    expect(cannotFilter('url.path', 'a\u0000b')).toContain('cannot be typed');
    expect(cannotFilter('url.path', 'a\u202Eb')).toContain('cannot be typed');
    expect(cannotFilter('url.path', 'x'.repeat(501))).toContain('longer than 500');
    expect(cannotFilter('odd name', '1')).toContain('name of this field');
    expect(cannotFilter('a|b', '1')).toContain('name of this field');
  });
});

describe('an instant in the display zone', () => {
  it('is written to the millisecond, the second or the minute', () => {
    expect(instant('2026-03-01T14:05:09.123Z', 'UTC')).toBe('2026-03-01 14:05:09.123');
    expect(instant('2026-03-01T14:05:09.123Z', 'UTC', 's')).toBe('2026-03-01 14:05:09');
    expect(instant('2026-03-01T14:05:09.123Z', 'UTC', 'min')).toBe('2026-03-01 14:05');
  });

  it('follows the zone, across midnight and a change of the clocks', () => {
    expect(instant('2026-03-01T23:30:00.000Z', 'Europe/Bucharest', 's')).toBe('2026-03-02 01:30:00');
    expect(instant('2026-07-01T23:30:00.000Z', 'Europe/Bucharest', 's')).toBe('2026-07-02 02:30:00');
  });

  it('shows what is not an instant as it came, and knows an unknown zone', () => {
    expect(instant('not a time', 'UTC')).toBe('not a time');
    expect(instant('2026-03-01T00:00:00Z', 'Mars/Phobos')).toBe('2026-03-01T00:00:00Z');
    expect(knownZone('Europe/Bucharest')).toBe(true);
    expect(knownZone('Mars/Phobos')).toBe(false);
    expect(instant('9999-12-31T23:59:59.000Z', 'UTC', 's')).toBe('9999-12-31 23:59:59');
  });
});

describe('the timeline', () => {
  const frame = decode('histogram', JSON.stringify(samples.listing[2]?.data));
  const histogram = frame.kind === 'histogram' ? frame : undefined;

  it('reads the width of a bucket as the server names it', () => {
    expect([spanMs('1s'), spanMs('30m'), spanMs('12h'), spanMs('1d'), spanMs('4w'), spanMs('soon')]).toEqual([1000, 1_800_000, 43_200_000, 86_400_000, 2_419_200_000, 0]);
  });

  it('puts every level into one of four series', () => {
    expect(['emerg', 'alert', 'crit', 'err', 'error', 'ERROR', 'fatal'].map(seriesOf)).toEqual(Array(7).fill('error'));
    expect(['warning', 'warn'].map(seriesOf)).toEqual(['warning', 'warning']);
    expect(['notice', 'info', 'debug', 'something else'].map(seriesOf)).toEqual(Array(4).fill('info'));
    expect(seriesOf('')).toBe('none');
  });

  it('turns the buckets of the sample into slots whose counts add up to its total', () => {
    expect(histogram).toBeDefined();
    const all = slots(histogram ?? { kind: 'histogram', span: '1s', total: 0, buckets: [] });
    expect(all.reduce((n, slot) => n + slot.total, 0)).toBe(histogram?.total);
    expect(all.every((slot) => slot.end - slot.start === 1000)).toBe(true);
    expect(all.reduce((n, slot) => n + slot.counts.error, 0)).toBe(2);
  });

  it('moves a selection by the keys: an arrow starts it, shift extends it from where it began', () => {
    expect(move(undefined, 'ArrowLeft', false, 5)).toEqual({ first: 4, last: 4, anchor: 4 });
    expect(move(undefined, 'ArrowRight', false, 5)).toEqual({ first: 0, last: 0, anchor: 0 });
    expect(move(undefined, 'ArrowLeft', false, 0)).toBeUndefined();
    let s = move(undefined, 'ArrowLeft', false, 5);
    s = move(s, 'ArrowLeft', true, 5);
    s = move(s, 'ArrowLeft', true, 5);
    expect(s).toEqual({ first: 2, last: 4, anchor: 4 });
    s = move(s, 'ArrowRight', true, 5);
    expect(s).toEqual({ first: 3, last: 4, anchor: 4 });
    expect(move(s, 'Home', true, 5)).toEqual({ first: 0, last: 4, anchor: 4 });
    expect(move(s, 'ArrowRight', false, 5)).toEqual({ first: 4, last: 4, anchor: 4 });
    expect(move({ first: 0, last: 0, anchor: 0 }, 'ArrowLeft', false, 5)).toEqual({ first: 0, last: 0, anchor: 0 });
  });

  // Five buckets of a second each, the third empty of errors, one event more in each than in the one before.
  const five = slots({
    kind: 'histogram',
    span: '1s',
    total: 15,
    buckets: [1, 2, 3, 4, 5].map((n, i) => ({ start: new Date(Date.parse('2026-03-01T00:00:00.000Z') + i * 1000).toISOString(), counts: { info: n } })),
  });

  it('gives a selection as the range a search takes: from the start of its first slot to the end of its last', () => {
    expect(rangeOf(five, { first: 0, last: 1, anchor: 0 })).toEqual({ from: '2026-03-01T00:00:00.000Z', to: '2026-03-01T00:00:02.000Z', events: 3 });
    expect(rangeOf(five, { first: 90, last: 91, anchor: 90 })).toBeUndefined();
  });

  it('takes a dragged stretch as its range, to the second, widened to the buckets it touches', () => {
    const t0 = Date.parse('2026-03-01T00:00:00.000Z');
    expect(stretch(five, t0 + 1500, t0 + 3200)).toEqual({ from: '2026-03-01T00:00:01.000Z', to: '2026-03-01T00:00:04.000Z' });
    // Dragged right to left, and over a part of the axis that holds nothing: still the stretch that was dragged.
    expect(stretch(five, t0 + 60_700, t0 + 30_200)).toEqual({ from: '2026-03-01T00:00:30.000Z', to: '2026-03-01T00:01:01.000Z' });
  });

  it('finds the slots a dragged stretch touches, and none when it touches none', () => {
    const t0 = Date.parse('2026-03-01T00:00:00.000Z');
    expect(within(five, t0 + 1500, t0 + 3200)).toEqual({ first: 1, last: 3, anchor: 1 });
    expect(within(five, t0 - 9000, t0 - 1)).toBeUndefined();
  });
});
