// The flags a file or a case can carry, in words. A flag the interface does not know is shown under the
// server's own name, so a new one is never hidden.
const NAMES: Record<string, string> = {
  unassigned: 'no host',
  host_inferred: 'host inferred',
  quarantined: 'quarantined',
  timezone_assumed: 'zone assumed',
  year_assumed: 'year assumed',
  input_truncated: 'input truncated',
  container_cut: 'archive cut',
  trailing_bytes: 'trailing bytes',
  spans_dst: 'spans a clock change',
  undated: 'undated events',
  future_dated: 'future-dated events',
  detection_stale: 'detection stale',
  reparse_pending: 're-parse pending',
  seen_elsewhere: 'seen in another case',
  no_stored_histogram: 'no stored timeline',
  control_bytes: 'holds control bytes',
};

/** What a file that was kept and not parsed was taken for, in words. A format without words here is shown by its name. */
const FORMATS: Record<string, string> = {
  'unknown-binary': 'no part of it reads as text',
  empty: 'empty',
  script: 'a script',
  html: 'a web page',
  php: 'a PHP script',
};

/** The reason beside a file that is "not a log". */
export function formatReason(format: string | null): string {
  if (format === null || format === '') {
    return 'no reason on record';
  }
  return FORMATS[format] ?? `taken for ${format}`;
}

/** The states of a file as the overview names them. */
export function stateName(state: string): string {
  return state === 'artifact' ? 'not a log' : state.replaceAll('_', ' ');
}

export function flagName(flag: string): string {
  return NAMES[flag] ?? flag.replaceAll('_', ' ');
}
