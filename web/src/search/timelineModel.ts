import type { Bucket, Histogram } from './stream';

// What the timeline draws and selects, apart from how it is drawn: the four series of a bucket, where a bucket
// ends, and the selection a keyboard moves.

export const SERIES = ['error', 'warning', 'info', 'none'] as const;
export type Series = (typeof SERIES)[number];

export const SERIES_NAME: Record<Series, string> = { error: 'Error and above', warning: 'Warning', info: 'Notice, info, debug', none: 'No level' };

const ERRORS = new Set(['emerg', 'emergency', 'panic', 'alert', 'crit', 'critical', 'fatal', 'err', 'error']);
const WARNINGS = new Set(['warning', 'warn']);

/** The series a level belongs to. A level this page does not know is counted with the informational ones. */
export function seriesOf(level: string): Series {
  const name = level.toLowerCase();
  if (name === '') {
    return 'none';
  }
  return ERRORS.has(name) ? 'error' : WARNINGS.has(name) ? 'warning' : 'info';
}

export function totals(bucket: Bucket): Record<Series, number> {
  const out: Record<Series, number> = { error: 0, warning: 0, info: 0, none: 0 };
  for (const [level, n] of Object.entries(bucket.counts)) {
    out[seriesOf(level)] += n;
  }
  return out;
}

const UNIT_MS: Record<string, number> = { s: 1000, m: 60_000, h: 3_600_000, d: 86_400_000, w: 604_800_000 };

/** The width of a bucket as the server names it (`30s`, `12h`, `4w`), in milliseconds. */
export function spanMs(span: string): number {
  const m = /^(\d+)([smhdw])$/.exec(span);
  return m === null ? 0 : Number(m[1]) * (UNIT_MS[m[2] ?? ''] ?? 0);
}

export interface Slot {
  /** Milliseconds since the epoch. */
  start: number;
  end: number;
  counts: Record<Series, number>;
  total: number;
}

/**
 * The buckets as slots on the time axis. The server sends the buckets that hold events; a slot ends where the
 * next bucket would begin, which is its start plus the span.
 */
export function slots(histogram: Histogram): Slot[] {
  const width = spanMs(histogram.span);
  return histogram.buckets.map((bucket) => {
    const start = new Date(bucket.start).getTime();
    const counts = totals(bucket);
    return { start, end: start + width, counts, total: counts.error + counts.warning + counts.info + counts.none };
  });
}

export interface Selection {
  /** Indexes into the slots, both inside, first <= last. */
  first: number;
  last: number;
  /** The end that stays when the arrow keys extend the selection. */
  anchor: number;
}

export type MoveKey = 'ArrowLeft' | 'ArrowRight' | 'Home' | 'End';

/** The selection after a key. Without one, an arrow starts at the nearer end of the axis. */
export function move(selection: Selection | undefined, key: MoveKey, extend: boolean, count: number): Selection | undefined {
  if (count === 0) {
    return undefined;
  }
  if (selection === undefined) {
    const at = key === 'Home' || key === 'ArrowRight' ? 0 : count - 1;
    return { first: at, last: at, anchor: at };
  }
  const head = selection.anchor === selection.first ? selection.last : selection.first;
  const next = key === 'Home' ? 0 : key === 'End' ? count - 1 : Math.min(count - 1, Math.max(0, head + (key === 'ArrowLeft' ? -1 : 1)));
  if (!extend) {
    return { first: next, last: next, anchor: next };
  }
  return { first: Math.min(selection.anchor, next), last: Math.max(selection.anchor, next), anchor: selection.anchor };
}

/** The range a selection stands for, as the instants a search takes. The end is the end of its last slot. */
export function rangeOf(all: readonly Slot[], selection: Selection): { from: string; to: string; events: number } | undefined {
  const first = all[selection.first];
  const last = all[selection.last];
  if (first === undefined || last === undefined) {
    return undefined;
  }
  let events = 0;
  for (let i = selection.first; i <= selection.last; i++) {
    events += all[i]?.total ?? 0;
  }
  return { from: new Date(first.start).toISOString(), to: new Date(last.end).toISOString(), events };
}

/** The slots a stretch of the axis touches, as a selection. Undefined when it touches none. */
export function within(all: readonly Slot[], fromMs: number, toMs: number): Selection | undefined {
  let first = -1;
  let last = -1;
  all.forEach((slot, i) => {
    if (slot.end > fromMs && slot.start < toMs) {
      first = first < 0 ? i : first;
      last = i;
    }
  });
  return first < 0 ? undefined : { first, last, anchor: first };
}

/**
 * The range a dragged stretch of the axis stands for: the stretch itself, to the second, widened to the whole
 * of every bucket it touches. A stretch over an empty part of the axis is still the range that was dragged.
 */
export function stretch(all: readonly Slot[], fromMs: number, toMs: number): { from: string; to: string } {
  const lo = Math.min(fromMs, toMs);
  const hi = Math.max(fromMs, toMs);
  const touched = within(all, lo, hi);
  const first = touched === undefined ? undefined : all[touched.first];
  const last = touched === undefined ? undefined : all[touched.last];
  const from = Math.min(Math.floor(lo / 1000) * 1000, first?.start ?? lo);
  const to = Math.max(Math.ceil(hi / 1000) * 1000, last?.end ?? hi);
  return { from: new Date(from).toISOString(), to: new Date(to).toISOString() };
}
