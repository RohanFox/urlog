import type { SearchRequest } from './run';

// The address of the search page is the search: what it holds runs, and nothing else does.

export interface SearchUrl {
  q: string;
  from: string | undefined;
  to: string | undefined;
  /** The display zone, an IANA id. Absent: UTC. */
  tz: string | undefined;
  /** Extra columns of the table, catalog names, at most MAX_COLUMNS. */
  cols: string[];
  /** The open event: its file and its offset in the file. */
  sel: { file: number; offset: number } | undefined;
}

export const MAX_COLUMNS = 3;

function value(params: URLSearchParams, name: string): string | undefined {
  const v = params.get(name);
  return v === null || v === '' ? undefined : v;
}

export function parse(params: URLSearchParams): SearchUrl {
  const sel = /^(\d{1,15}):(\d{1,15})$/.exec(params.get('sel') ?? '');
  return {
    q: params.get('q') ?? '',
    from: value(params, 'from'),
    to: value(params, 'to'),
    tz: value(params, 'tz'),
    cols: [...new Set((params.get('cols') ?? '').split(',').filter((c) => c !== ''))].slice(0, MAX_COLUMNS),
    sel: sel === null ? undefined : { file: Number(sel[1]), offset: Number(sel[2]) },
  };
}

/** The same state as an address. Empty members are left out, so one search has one address. */
export function format(url: SearchUrl): string {
  const params = new URLSearchParams();
  if (url.q !== '') {
    params.set('q', url.q);
  }
  if (url.from !== undefined) {
    params.set('from', url.from);
  }
  if (url.to !== undefined) {
    params.set('to', url.to);
  }
  if (url.tz !== undefined) {
    params.set('tz', url.tz);
  }
  if (url.cols.length > 0) {
    params.set('cols', url.cols.join(','));
  }
  if (url.sel !== undefined) {
    params.set('sel', `${url.sel.file}:${url.sel.offset}`);
  }
  const text = params.toString();
  return text === '' ? '' : `?${text}`;
}

export function requestOf(caseId: number, url: SearchUrl): SearchRequest {
  return { caseId, q: url.q, from: url.from, to: url.to, tz: url.tz };
}

/** Two addresses with the same key are one run: columns and the open event change what is drawn, not what is asked. */
export function runKey(caseId: number, url: SearchUrl): string {
  return JSON.stringify([caseId, url.q, url.from ?? null, url.to ?? null, url.tz ?? null]);
}

/** The events a default range leaves out: those without a usable time are stored at the epoch. */
export const UNDATED = { from: '1970-01-01T00:00:00Z', to: '1980-01-01T00:00:00Z' } as const;

/** The latest instant the time grammar takes: the explicit end of "everything after the last event of the case". */
export const END_OF_TIME = '9999-12-31T23:59:59Z';

/** The range of the events dated after the end of the case, which is the instant of its last dated event. */
export function futureRange(caseTo: string): { from: string; to: string } {
  const after = new Date(new Date(caseTo).getTime() + 1);
  return { from: Number.isNaN(after.getTime()) ? caseTo : after.toISOString(), to: END_OF_TIME };
}
