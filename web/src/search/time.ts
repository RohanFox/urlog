// Instants arrive in UTC and are written in the display zone. The zone is a choice of the reader and of the
// address, never of the event.

const formats = new Map<string, Intl.DateTimeFormat>();

function formatOf(zone: string): Intl.DateTimeFormat {
  let format = formats.get(zone);
  if (format === undefined) {
    format = new Intl.DateTimeFormat('en-CA', {
      timeZone: zone,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      fractionalSecondDigits: 3,
      hourCycle: 'h23',
    });
    formats.set(zone, format);
  }
  return format;
}

/** True when the browser knows the zone. A hand-edited address may name anything. */
export function knownZone(zone: string): boolean {
  try {
    formatOf(zone);
    return true;
  } catch {
    return false;
  }
}

/** The zone of this browser, as an IANA id. */
export function browserZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/** `2026-03-01 14:05:09.123` in the zone. An instant that is not one is shown as it came. */
export function instant(iso: string, zone: string, precision: 'ms' | 's' | 'min' = 'ms'): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return iso;
  }
  let part: Record<string, string>;
  try {
    part = Object.fromEntries(
      formatOf(zone)
        .formatToParts(date)
        .map((p) => [p.type, p.value]),
    );
  } catch {
    return iso;
  }
  const day = `${part['year'] ?? ''}-${part['month'] ?? ''}-${part['day'] ?? ''}`;
  const minute = `${part['hour'] ?? ''}:${part['minute'] ?? ''}`;
  if (precision === 'min') {
    return `${day} ${minute}`;
  }
  const second = `${minute}:${part['second'] ?? ''}`;
  return precision === 's' ? `${day} ${second}` : `${day} ${second}.${part['fractionalSecond'] ?? '000'}`;
}
