import { ApiError, get } from '../api/http';
import type { Refusal, SearchRequest } from './run';

// The result of a search as a file. The page fetches it, so it can read the answer first: a refusal is shown
// as one, and a file the server cut says so. Where the browser lets a page write a file it chose, the bytes go
// to disk as they arrive; elsewhere the browser downloads the address itself and the page cannot watch.

export type ExportFormat = 'csv' | 'json';

/**
 * What is exported. `evidence`: the rows, each with the file and offset it came from and its raw line, which
 * is the server's default. `brief`: the rows with the listing's six columns alone. `files`: the files those
 * rows came from, with their hashes.
 */
export type ExportWhat = 'evidence' | 'brief' | 'files';

/** The address of an export: the search of the page, in the format asked for. */
export function exportUrl(request: SearchRequest, format: ExportFormat, formulaGuard: boolean, what: ExportWhat = 'evidence'): string {
  const params = new URLSearchParams({ format, query: request.q });
  if (request.from !== undefined) {
    params.set('from', request.from);
  }
  if (request.to !== undefined) {
    params.set('to', request.to);
  }
  if (request.tz !== undefined) {
    params.set('tz', request.tz);
  }
  if (what === 'files') {
    return `/api/v1/cases/${request.caseId}/search/export/files?${params.toString()}`;
  }
  if (formulaGuard && format === 'csv') {
    params.set('formula_guard', 'true');
  }
  if (what === 'brief') {
    params.set('columns', 'brief');
  }
  return `/api/v1/cases/${request.caseId}/search/export?${params.toString()}`;
}

export function exportName(request: SearchRequest, format: ExportFormat, what: ExportWhat = 'evidence'): string {
  return `case-${request.caseId}-export${what === 'files' ? '-files' : ''}.${format === 'csv' ? 'csv' : 'jsonl'}`;
}

/** The most rows the server puts into one export. */
export const EXPORT_ROWS = 1_000_000;

/**
 * About how large an export will be, from the rows the search matches and the lines the page has loaded: enough
 * to tell a file a page can hold from one it cannot, and no more exact than that.
 */
export function estimateBytes(rows: number | undefined, averageRaw: number, format: ExportFormat, what: ExportWhat): number {
  if (what === 'files') {
    return 0;
  }
  const row = (what === 'brief' ? 150 : 190 + averageRaw) * (format === 'json' ? 1.5 : 1);
  return Math.min(rows ?? EXPORT_ROWS, EXPORT_ROWS) * row;
}

/**
 * The largest file a page holds for a browser that cannot write a file it chose. Holding costs one and a half
 * to 1.65 times the file in the browser's memory (measured), and the rule is 500 MB.
 */
export const HELD_LIMIT_BYTES = 300_000_000;

/** True when a file of about that size may be held in the page; false when the browser must download it itself. */
export function mayHold(estimatedBytes: number): boolean {
  return estimatedBytes <= HELD_LIMIT_BYTES;
}

// The part of the File System Access API this module uses. Chromium has it; the type library does not.
interface Writable extends WritableStream<Uint8Array> {
  abort(reason?: unknown): Promise<void>;
}

interface FileHandle {
  name: string;
  createWritable(): Promise<Writable>;
  remove?: () => Promise<void>;
}

type Picker = (options: { suggestedName: string; types: Array<{ description: string; accept: Record<string, string[]> }> }) => Promise<FileHandle>;

function picker(): Picker | undefined {
  const candidate = (window as unknown as { showSaveFilePicker?: Picker }).showSaveFilePicker;
  return typeof candidate === 'function' ? candidate.bind(window) : undefined;
}

/** True when this browser lets the page write the file as it arrives. */
export function writesAsItArrives(): boolean {
  return picker() !== undefined;
}

export type ExportPhase =
  /** The browser's dialog for the place of the file is open. */
  | 'choosing'
  /** Asked; the server counts the rows before its first byte. */
  | 'asking'
  | 'receiving'
  | 'done'
  | 'refused'
  /** The download broke after it began. Nothing of it is kept. */
  | 'failed'
  | 'cancelled';

export interface ExportState {
  phase: ExportPhase;
  format: ExportFormat;
  file: string | undefined;
  bytes: number;
  /** How many rows the result has, from the server's header, when it counted them. */
  rows: number | undefined;
  /** The file was cut at the server's row limit. */
  cut: boolean;
  refusal: Refusal | undefined;
}

function aborted(cause: unknown): boolean {
  return cause instanceof DOMException && cause.name === 'AbortError';
}

function refusalOf(cause: unknown): Refusal {
  if (cause instanceof ApiError) {
    return { status: cause.status, code: cause.code ?? (cause.offline ? 'offline' : undefined), message: cause.message, detail: cause.detail, offset: cause.offset, length: cause.length };
  }
  return { status: 0, code: 'offline', message: cause instanceof Error ? cause.message : 'the download was interrupted', detail: undefined, offset: undefined, length: undefined };
}

/** One export from the choice of its file to its end. */
export class ExportJob {
  state: ExportState;

  private readonly listeners = new Set<() => void>();
  private readonly abort = new AbortController();
  private readonly url: string;
  private readonly suggested: string;
  private gone = false;

  constructor(request: SearchRequest, format: ExportFormat, formulaGuard: boolean, what: ExportWhat = 'evidence') {
    this.url = exportUrl(request, format, formulaGuard, what);
    this.suggested = exportName(request, format, what);
    this.state = { phase: 'choosing', format, file: undefined, bytes: 0, rows: undefined, cut: false, refusal: undefined };
  }

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  snapshot = (): ExportState => this.state;

  get live(): boolean {
    return this.state.phase === 'choosing' || this.state.phase === 'asking' || this.state.phase === 'receiving';
  }

  cancel(): void {
    if (this.gone || !this.live) {
      return;
    }
    this.abort.abort();
    this.set({ phase: 'cancelled' });
    this.gone = true;
  }

  /** Must be called from the press of a button: the browser opens its dialog only then. */
  async start(): Promise<void> {
    const pick = picker();
    if (pick === undefined) {
      await this.hold();
      return;
    }
    let handle: FileHandle;
    try {
      handle = await pick({
        suggestedName: this.suggested,
        types: [this.state.format === 'csv' ? { description: 'Comma-separated values', accept: { 'text/csv': ['.csv'] } } : { description: 'JSON lines', accept: { 'application/x-ndjson': ['.jsonl'] } }],
      });
    } catch {
      // The dialog was closed: nothing was asked of the server.
      this.set({ phase: 'cancelled' });
      return;
    }
    this.set({ phase: 'asking', file: handle.name });

    let writable: Writable | undefined;
    try {
      const response = await get(this.url, this.abort.signal);
      const rows = Number(response.headers.get('X-Urlog-Rows') ?? NaN);
      this.set({ phase: 'receiving', rows: Number.isFinite(rows) ? rows : undefined, cut: response.headers.get('X-Urlog-Truncated') === 'true' });
      if (response.body === null) {
        throw new Error('the answer had no body');
      }
      writable = await handle.createWritable();
      const counter = new TransformStream<Uint8Array, Uint8Array>({
        transform: (chunk, controller) => {
          this.set({ bytes: this.state.bytes + chunk.byteLength });
          controller.enqueue(chunk);
        },
      });
      // The browser moves the bytes from the network to the file; the page holds a chunk at a time.
      await response.body.pipeThrough(counter).pipeTo(writable, { signal: this.abort.signal });
      this.set({ phase: 'done' });
    } catch (cause) {
      // What was written is not a whole export: it is not kept under the name the reader chose.
      await writable?.abort().catch(() => undefined);
      await handle.remove?.().catch(() => undefined);
      if (aborted(cause) || this.abort.signal.aborted) {
        this.set({ phase: 'cancelled' });
        return;
      }
      this.set(this.state.phase === 'asking' ? { phase: 'refused', refusal: refusalOf(cause) } : { phase: 'failed', refusal: refusalOf(cause) });
    }
  }

  /**
   * Without a file to write to: the page takes the whole answer into a blob and hands that to the browser as
   * a download. The page can read the answer and count the bytes; the price is the file held by the browser
   * until it is saved.
   */
  private async hold(): Promise<void> {
    this.set({ phase: 'asking', file: this.suggested });
    try {
      const response = await get(this.url, this.abort.signal);
      const rows = Number(response.headers.get('X-Urlog-Rows') ?? NaN);
      this.set({ phase: 'receiving', rows: Number.isFinite(rows) ? rows : undefined, cut: response.headers.get('X-Urlog-Truncated') === 'true' });
      if (response.body === null) {
        throw new Error('the answer had no body');
      }
      const parts: Blob[] = [];
      const reader = response.body.getReader();
      for (;;) {
        const { done, value } = await reader.read();
        if (done) {
          break;
        }
        // A blob of each chunk: the browser may keep blobs outside the page's own memory.
        parts.push(new Blob([value]));
        this.set({ bytes: this.state.bytes + value.byteLength });
      }
      const address = URL.createObjectURL(new Blob(parts, { type: response.headers.get('Content-Type') ?? 'application/octet-stream' }));
      const link = document.createElement('a');
      link.href = address;
      link.download = this.suggested;
      link.click();
      // The download reads the blob after the click; the address is given back once it has had time to start.
      setTimeout(() => {
        URL.revokeObjectURL(address);
      }, 60_000);
      this.set({ phase: 'done' });
    } catch (cause) {
      if (aborted(cause) || this.abort.signal.aborted) {
        this.set({ phase: 'cancelled' });
        return;
      }
      this.set(this.state.phase === 'asking' ? { phase: 'refused', refusal: refusalOf(cause) } : { phase: 'failed', refusal: refusalOf(cause) });
    }
  }

  private set(change: Partial<ExportState>): void {
    if (this.gone) {
      return;
    }
    this.state = { ...this.state, ...change };
    for (const listener of this.listeners) {
      listener();
    }
  }
}
