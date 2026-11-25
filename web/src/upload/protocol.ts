import { ApiError, call, raw, type RawAnswer } from '../api/http';
import type { UploadStatusResponse } from '../api/schema.gen';

// The part of the tus 1.0 protocol the backend serves: create, ask, send, remove. One function a request.
// Each answers with what the server said; what to do about it is the state machine's business (machine.ts).

/** Bytes a request carries. The server's own figure for a client. */
export const CHUNK_BYTES = 16 * 1024 * 1024;

/** The server found the bytes of a chunk different from the checksum sent with them, and cut the chunk back. */
export const CHECKSUM_MISMATCH = 460;

/** The header of a HEAD that says where an upload stands, in the words of the status route. */
export const STATE_HEADER = 'Urlog-Upload-State';

/** The problem type of a last chunk that is stored and not yet recorded. */
export const PENDING_TYPE = 'upload-pending';

const TUS = { 'Tus-Resumable': '1.0.0' } as const;

export interface Refusal {
  status: number;
  /** The server's own words. */
  message: string;
  retryAfter: number | undefined;
  /** The record of a stored last chunk waits for the metadata file; the server finishes by itself. */
  pending: boolean;
}

export type Sent = { ok: true; offset: number } | ({ ok: false } & Refusal);
export type Created = { ok: true; id: string } | ({ ok: false } & Refusal);
export type Asked =
  | { kind: 'offset'; offset: number; length: number; state: string | null }
  /** Every byte is stored and the request that brought the last one is still at work. */
  | { kind: 'locked' }
  | { kind: 'gone' }
  | ({ kind: 'refused' } & Refusal);

function base64(bytes: Uint8Array): string {
  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary);
}

function metadata(pairs: Record<string, string | undefined>): string {
  const encoder = new TextEncoder();
  return Object.entries(pairs)
    .filter((pair): pair is [string, string] => pair[1] !== undefined && pair[1] !== '')
    .map(([key, value]) => `${key} ${base64(encoder.encode(value))}`)
    .join(',');
}

function seconds(value: string | null): number | undefined {
  const n = Number(value ?? Number.NaN);
  return Number.isFinite(n) && n >= 0 ? Math.ceil(n) : undefined;
}

function refusal(answer: RawAnswer): Refusal {
  // A refusal of the protocol is plain text; one of the API around it is a problem body.
  let message = answer.text.trim();
  let pending = false;
  try {
    const body = JSON.parse(answer.text) as { title?: unknown; detail?: unknown; type?: unknown };
    if (typeof body.title === 'string') {
      message = typeof body.detail === 'string' ? `${body.title}: ${body.detail}` : body.title;
    }
    pending = typeof body.type === 'string' && body.type.endsWith(`/${PENDING_TYPE}`);
  } catch {
    // Plain text, as the protocol answers.
  }
  return { status: answer.status, message: message === '' ? `the server answered ${answer.status}` : message, retryAfter: seconds(answer.header('Retry-After')), pending };
}

const base = (caseId: number) => `/api/v1/cases/${caseId}/uploads`;

/** Announces a file: its length and name, and the host its lines belong to when the user named one. */
export async function create(caseId: number, file: { name: string; size: number }, host: string | undefined, signal?: AbortSignal): Promise<Created> {
  const answer = await raw({
    method: 'POST',
    url: base(caseId),
    headers: { ...TUS, 'Upload-Length': String(file.size), 'Upload-Metadata': metadata({ filename: file.name, host }) },
    ...(signal === undefined ? {} : { signal }),
  });
  const location = answer.header('Location');
  if (answer.status === 201 && location !== null) {
    return { ok: true, id: location.slice(location.lastIndexOf('/') + 1) };
  }
  return { ok: false, ...refusal(answer) };
}

/** Asks how many bytes the server holds. */
export async function ask(caseId: number, id: string, signal?: AbortSignal): Promise<Asked> {
  const answer = await raw({ method: 'HEAD', url: `${base(caseId)}/${id}`, headers: TUS, ...(signal === undefined ? {} : { signal }) });
  if (answer.status === 423) {
    return { kind: 'locked' };
  }
  if (answer.status === 404 || answer.status === 410) {
    return { kind: 'gone' };
  }
  const offset = Number(answer.header('Upload-Offset') ?? Number.NaN);
  const length = Number(answer.header('Upload-Length') ?? Number.NaN);
  if (answer.status >= 200 && answer.status < 300 && Number.isFinite(offset) && Number.isFinite(length)) {
    return { kind: 'offset', offset, length, state: answer.header(STATE_HEADER) };
  }
  return { kind: 'refused', ...refusal(answer) };
}

/** The SHA-256 of a chunk, as the checksum header carries it. */
export async function checksum(chunk: Blob): Promise<string> {
  return `sha256 ${base64(new Uint8Array(await crypto.subtle.digest('SHA-256', await chunk.arrayBuffer())))}`;
}

/** Sends one chunk from an offset, with its checksum. */
export async function send(
  caseId: number,
  id: string,
  offset: number,
  chunk: Blob,
  sum: string,
  events: { onSent?: (bytes: number) => void; onAllSent?: () => void; signal?: AbortSignal },
): Promise<Sent> {
  const answer = await raw({
    method: 'PATCH',
    url: `${base(caseId)}/${id}`,
    headers: { ...TUS, 'Content-Type': 'application/offset+octet-stream', 'Upload-Offset': String(offset), 'Upload-Checksum': sum },
    body: chunk,
    ...events,
  });
  const next = Number(answer.header('Upload-Offset') ?? Number.NaN);
  if (answer.status === 204 && Number.isFinite(next)) {
    return { ok: true, offset: next };
  }
  return { ok: false, ...refusal(answer) };
}

/** Removes an upload that is not recorded. */
export async function remove(caseId: number, id: string): Promise<void> {
  await raw({ method: 'DELETE', url: `${base(caseId)}/${id}`, headers: TUS });
}

/** What became of an upload. Null when the server no longer knows it. */
export async function status(caseId: number, id: string, signal?: AbortSignal): Promise<UploadStatusResponse | null> {
  try {
    return await call('GetUpload', { params: { caseId, uploadId: id }, ...(signal === undefined ? {} : { signal }) });
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) {
      return null;
    }
    throw error;
  }
}
