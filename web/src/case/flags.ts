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
};

export function flagName(flag: string): string {
  return NAMES[flag] ?? flag.replaceAll('_', ' ');
}
