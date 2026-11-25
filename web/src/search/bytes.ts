import { get, open } from '../api/http';
import { read, type Cell } from './stream';

// The stored bytes of a line. Every other answer of the server is the line decoded as text; these are the bytes,
// fetched from the route that answers nothing else, shown as hex and handed over as a file. They are never
// decoded into the page.

/** The key of an event as its row gives it. */
export interface EventKey {
  caseId: number;
  fileId: number;
  offset: number;
  /** The event's time, as the row has it. */
  ts: string;
  host: string;
}

export function rawUrl(key: EventKey): string {
  const params = new URLSearchParams({ ts: key.ts, host: key.host });
  return `/api/v1/cases/${key.caseId}/files/${key.fileId}/events/${key.offset}/raw?${params.toString()}`;
}

export interface LineBytes {
  bytes: Uint8Array;
  /** The SHA-256 of the bytes, as the server computed it over what it sent. */
  sha256: string | undefined;
}

export async function fetchBytes(key: EventKey, signal: AbortSignal): Promise<LineBytes> {
  const response = await get(rawUrl(key), signal);
  return { bytes: new Uint8Array(await response.arrayBuffer()), sha256: response.headers.get('X-Urlog-Raw-Sha256') ?? undefined };
}

/** How many bytes the page draws as hex. The rest is in the file. */
export const HEX_BYTES = 4096;

const HEX = '0123456789abcdef';

/**
 * A hex dump of the first bytes: offset, sixteen bytes, and beside them the printable ASCII among them with a
 * dot for everything else. What is drawn is made of hex digits, dots and printable ASCII only, whatever the bytes are.
 */
export function hexDump(bytes: Uint8Array, limit = HEX_BYTES): string {
  const lines: string[] = [];
  const end = Math.min(bytes.length, limit);
  for (let at = 0; at < end; at += 16) {
    let hex = '';
    let text = '';
    for (let i = at; i < at + 16; i++) {
      if (i < end) {
        const b = bytes[i] ?? 0;
        hex += `${HEX[b >> 4] ?? ''}${HEX[b & 15] ?? ''} `;
        text += b >= 0x20 && b < 0x7f ? String.fromCharCode(b) : '.';
      } else {
        hex += '   ';
      }
      if (i === at + 7) {
        hex += ' ';
      }
    }
    lines.push(`${at.toString(16).padStart(8, '0')}  ${hex} |${text}|`);
  }
  return lines.join('\n');
}

/** Hands bytes to the browser as a download. They leave the page as a file of bytes, under a name that says where they are from. */
export function saveBytes(bytes: Uint8Array, name: string): void {
  const address = URL.createObjectURL(new Blob([bytes.slice().buffer], { type: 'application/octet-stream' }));
  const link = document.createElement('a');
  link.href = address;
  link.download = name;
  link.click();
  setTimeout(() => {
    URL.revokeObjectURL(address);
  }, 60_000);
}

/** The place of a piece in a line the parser stored as several events. */
export interface LinePart {
  firstOffset: number;
  part: number;
  /** The number of pieces, when the parser knew it as it wrote this one. */
  total: number | undefined;
}

/** Reads the marks of a piece from the extras of its event. Undefined for a whole line. */
export function linePart(extras: Cell | undefined): LinePart | undefined {
  if (typeof extras !== 'object' || extras === null) {
    return undefined;
  }
  const first = /^\d{1,15}$/.exec(extras['line.first_offset'] ?? '');
  const part = /^(\d{1,9})(?:\/(\d{1,9}))?$/.exec(extras['line.part'] ?? '');
  if (first === null || part === null) {
    return undefined;
  }
  return { firstOffset: Number(first[0]), part: Number(part[1]), total: part[2] === undefined ? undefined : Number(part[2]) };
}

/** The query that lists the pieces of one line, in the order of the file. */
export function partsQuery(fileId: number, firstOffset: number): string {
  return `file.id=${fileId} line.first_offset="${firstOffset}" | sort file.offset`;
}

/** The most pieces the page joins into one file: one page of a listing. A longer line is exported from its parts. */
export const MAX_JOINED_PARTS = 200;

export class TooManyParts extends Error {
  constructor() {
    super(`the line has more than ${MAX_JOINED_PARTS} parts`);
    this.name = 'TooManyParts';
  }
}

/**
 * The whole of a line that was stored as several events: its pieces found by a search of their own, the bytes
 * of each fetched in the order of the file, and joined. Every piece of a line carries the time of the line, so
 * the search covers that one instant.
 */
export async function wholeLine(key: EventKey, firstOffset: number, signal: AbortSignal, onProgress: (done: number, of: number) => void): Promise<Uint8Array> {
  const from = new Date(key.ts);
  const to = new Date(from.getTime() + 1);
  const body = await open(`/api/v1/cases/${key.caseId}/search`, { query: partsQuery(key.fileId, firstOffset), from: from.toISOString(), to: to.toISOString() }, signal);
  const parts: EventKey[] = [];
  // What the stream said, kept where its reader can write it.
  const seen: { more: boolean; failure: string | undefined } = { more: false, failure: undefined };
  const text = (cell: Cell | undefined) => (typeof cell === 'string' ? cell : '');
  await read(body, (frame) => {
    if (frame.kind === 'page') {
      const at = (name: string) => frame.columns.indexOf(name);
      for (const row of frame.rows) {
        parts.push({ caseId: key.caseId, fileId: Number(row[at('file.id')]), offset: Number(row[at('file.offset')]), ts: text(row[at('_time')]), host: text(row[at('host.name')]) });
      }
      seen.more = frame.next !== null;
    } else if (frame.kind === 'error') {
      seen.failure = frame.message;
    }
  });
  if (seen.failure !== undefined) {
    throw new Error(seen.failure);
  }
  if (seen.more || parts.length > MAX_JOINED_PARTS) {
    throw new TooManyParts();
  }
  if (parts.length === 0) {
    throw new Error('the parts of the line were not found');
  }
  const chunks: Uint8Array[] = [];
  let length = 0;
  for (const [i, part] of parts.entries()) {
    const { bytes } = await fetchBytes(part, signal);
    chunks.push(bytes);
    length += bytes.length;
    onProgress(i + 1, parts.length);
  }
  const joined = new Uint8Array(length);
  let at = 0;
  for (const chunk of chunks) {
    joined.set(chunk, at);
    at += chunk.length;
  }
  return joined;
}
