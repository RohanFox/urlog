import type { CaseState, MemberRole } from '../api/schema.gen';

/** An instant from the API, written in UTC to the minute: `2026-11-20 07:41 UTC`. */
export function utcMinute(instant: string): string {
  const date = new Date(instant);
  if (Number.isNaN(date.getTime())) {
    return instant;
  }
  return `${date.toISOString().slice(0, 16).replace('T', ' ')} UTC`;
}

const ROLES: Record<MemberRole, string> = { owner: 'Owner', rw: 'Read-write', ro: 'Read-only' };

export function roleName(role: MemberRole): string {
  return ROLES[role];
}

/** A state as the API names it, with a capital and without the underscore. */
export function stateName(state: CaseState): string {
  const words = state.replace('_', ' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}

const UNITS = ['B', 'KiB', 'MiB', 'GiB', 'TiB'] as const;

/** A size in binary units, three significant digits at most: `1.86 GiB`. */
export function bytes(n: number): string {
  let value = n;
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit += 1;
  }
  const digits = unit === 0 || value >= 100 ? 0 : value >= 10 ? 1 : 2;
  return `${value.toFixed(digits)} ${UNITS[unit] ?? 'B'}`;
}

export function number(n: number): string {
  return n.toLocaleString('en-US');
}

/** A share as a whole percentage, never 100 before the end. */
export function percent(part: number, whole: number): number {
  if (whole <= 0) {
    return 0;
  }
  return Math.min(part >= whole ? 100 : 99, Math.floor((part / whole) * 100));
}

export function count(n: number, one: string, many: string): string {
  return `${n.toLocaleString('en-US')} ${n === 1 ? one : many}`;
}
