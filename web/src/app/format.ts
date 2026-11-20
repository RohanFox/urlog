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

export function count(n: number, one: string, many: string): string {
  return `${n.toLocaleString('en-US')} ${n === 1 ? one : many}`;
}
