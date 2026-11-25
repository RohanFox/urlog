import { ApiError } from '../api/http';
import * as tus from './protocol';

// One upload from the first request to its final state. The rules it keeps:
//   - once the server holds every byte, nothing more is sent: a stored last chunk is never sent again, whether
//     its request answered, answered "pending" (503), or was lost on the way back;
//   - a HEAD answered 423 means the last request is still at work: the bytes are stored, the upload is verifying;
//   - after a cut the server's offset decides where to go on, never the client's own count;
//   - every chunk carries its SHA-256, and a chunk the server found different is sent again from its start.

export type Phase =
  /** No slot yet: other uploads are ahead of it. */
  | 'waiting'
  | 'starting'
  | 'receiving'
  | 'paused'
  /** No answer, or a failure of the server: asking where the upload stands, then going on. */
  | 'retrying'
  /** Every byte is stored; the server hashes the file and records it. Nothing is sent. */
  | 'verifying'
  /** A reload of the page took the file away; the bytes that arrived are still on the server. */
  | 'interrupted'
  | 'done'
  | 'duplicate'
  | 'rejected'
  | 'failed'
  /** The server would not take the file at all: its words say why. */
  | 'refused';

export const FINAL: ReadonlySet<Phase> = new Set<Phase>(['done', 'duplicate', 'rejected', 'failed', 'refused']);
/** Phases in which closing the page would lose work a person may not want to lose. */
export const UNSAFE_TO_LEAVE: ReadonlySet<Phase> = new Set<Phase>(['waiting', 'starting', 'receiving', 'paused', 'retrying']);
/** Phases that hold one of the slots. */
export const ACTIVE: ReadonlySet<Phase> = new Set<Phase>(['starting', 'receiving', 'retrying', 'verifying']);

export interface FileIdentity {
  name: string;
  size: number;
  lastModified: number;
}

export interface UploadView extends FileIdentity {
  key: string;
  caseId: number;
  phase: Phase;
  /** Bytes the server holds, plus what has left of the chunk in flight. */
  sent: number;
  /** The server's words for a refusal, a rejection or a failure. */
  message: string | undefined;
  /** The record the bytes became, or the one that already held them. */
  fileId: number | undefined;
  /** The pause was the session's end, not the user's choice. */
  sessionEnded: boolean;
  host: string | undefined;
  id: string | undefined;
}

export interface Deps {
  protocol: Pick<typeof tus, 'create' | 'ask' | 'send' | 'remove' | 'status' | 'checksum'>;
  sleep: (seconds: number, signal: AbortSignal) => Promise<void>;
  chunkBytes: number;
}

export const realDeps: Deps = {
  protocol: tus,
  chunkBytes: tus.CHUNK_BYTES,
  sleep: (seconds, signal) =>
    new Promise((resolve, reject) => {
      const timer = setTimeout(resolve, seconds * 1000);
      signal.addEventListener('abort', () => {
        clearTimeout(timer);
        reject(new DOMException('aborted', 'AbortError'));
      });
    }),
};

/** Waits between tries after a cut, in seconds. After the last one the upload fails and offers a new start. */
export const RETRY_WAITS = [1, 2, 5, 10, 20] as const;
const POLL_SECONDS = 2;

function aborted(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}

let counter = 0;

export class Upload {
  readonly key: string;
  private file: File | undefined;
  private readonly identity: FileIdentity;
  private readonly caseId: number;
  private readonly host: string | undefined;
  private id: string | undefined;
  private phase: Phase = 'waiting';
  private offset = 0;
  private inFlight = 0;
  private message: string | undefined;
  private fileId: number | undefined;
  private sessionEnded = false;
  /** The server holds every byte. From here on nothing is sent. */
  private stored = false;
  /** The server's offset must be asked before the next chunk: after a pause, a reload or a cut. */
  private mustAsk: boolean;
  private tries = 0;
  private controller: AbortController | undefined;
  private running = false;

  constructor(
    init: { caseId: number; file?: File; identity: FileIdentity; host?: string | undefined; id?: string | undefined },
    private readonly deps: Deps,
    private readonly changed: () => void,
  ) {
    counter += 1;
    this.key = `u${counter}`;
    this.caseId = init.caseId;
    this.file = init.file;
    this.identity = init.identity;
    this.host = init.host;
    this.id = init.id;
    this.mustAsk = init.id !== undefined;
    if (init.file === undefined) {
      this.phase = 'interrupted';
    }
  }

  view(): UploadView {
    return {
      key: this.key,
      caseId: this.caseId,
      ...this.identity,
      phase: this.phase,
      sent: Math.min(this.identity.size, this.offset + this.inFlight),
      message: this.message,
      fileId: this.fileId,
      sessionEnded: this.sessionEnded,
      host: this.host,
      id: this.id,
    };
  }

  /** The file in hand, for starting an upload that ended badly once more. */
  get source(): File | undefined {
    return this.file;
  }

  /** Takes a slot and works until a final state, a pause or the loss of the file. */
  start(): void {
    if (this.running || FINAL.has(this.phase)) {
      return;
    }
    this.running = true;
    this.sessionEnded = false;
    this.controller = new AbortController();
    void this.run(this.controller.signal).finally(() => {
      this.running = false;
      this.changed();
    });
  }

  pause(): void {
    if (FINAL.has(this.phase) || this.phase === 'verifying' || this.phase === 'interrupted') {
      return;
    }
    this.set('paused');
    this.controller?.abort();
  }

  /** Back among the waiting; the queue starts it when a slot is free. */
  resume(): void {
    if (this.phase === 'paused') {
      this.mustAsk = this.id !== undefined;
      this.set('waiting');
    }
  }

  /** Stops and removes what the server holds, unless the file is already with intake. */
  async cancel(): Promise<void> {
    this.controller?.abort();
    if (this.id !== undefined && !this.stored && !FINAL.has(this.phase)) {
      try {
        await this.deps.protocol.remove(this.caseId, this.id);
      } catch {
        // The server removes an unfinished upload by itself when it expires.
      }
    }
  }

  /** After a reload: the user chose a file again. It is taken only when it is the file that was being sent. */
  attach(file: File): boolean {
    if (this.phase !== 'interrupted' || file.name !== this.identity.name || file.size !== this.identity.size || file.lastModified !== this.identity.lastModified) {
      return false;
    }
    this.file = file;
    this.set('waiting');
    return true;
  }

  /**
   * After a reload, before any file is chosen: where the upload stands on the server. One whose bytes are
   * complete needs no file and goes on to its result.
   */
  async refresh(): Promise<void> {
    if (this.phase !== 'interrupted' || this.id === undefined) {
      return;
    }
    try {
      const asked = await this.deps.protocol.ask(this.caseId, this.id);
      if (asked.kind === 'gone') {
        this.end('failed', 'The server no longer holds this upload. Start it again.');
      } else if (asked.kind === 'locked' || (asked.kind === 'offset' && asked.offset >= asked.length)) {
        this.stored = true;
        this.offset = this.identity.size;
        this.set('waiting');
      } else if (asked.kind === 'offset') {
        this.offset = asked.offset;
        this.changed();
      }
    } catch {
      // No answer: the row stays as it is, and choosing the file asks again.
    }
  }

  /** Read through a call: an answer of the server may have changed it since the loop last looked. */
  private holdsAll(): boolean {
    return this.stored;
  }

  private set(phase: Phase): void {
    if (this.phase !== phase) {
      this.phase = phase;
      this.changed();
    }
  }

  private end(phase: Phase, message?: string, fileId?: number | null): void {
    this.message = message;
    this.fileId = fileId ?? undefined;
    this.inFlight = 0;
    this.set(phase);
  }

  private endSession(): void {
    this.sessionEnded = true;
    this.set('paused');
  }

  private async run(signal: AbortSignal): Promise<void> {
    const { protocol } = this.deps;
    try {
      if (this.id === undefined) {
        this.set('starting');
        for (;;) {
          const created = await protocol.create(this.caseId, this.identity, this.host, signal);
          if (created.ok) {
            this.id = created.id;
            this.changed();
            break;
          }
          if (created.status === 401) {
            this.endSession();
            return;
          }
          if (created.status !== 429) {
            this.end('refused', created.message);
            return;
          }
          await this.deps.sleep(created.retryAfter ?? 1, signal);
        }
      }
      const id = this.id;

      for (;;) {
        if (this.stored) {
          await this.verify(id, signal);
          return;
        }
        if (this.mustAsk) {
          if (!(await this.askServer(id, signal))) {
            return;
          }
          continue;
        }
        if (this.offset >= this.identity.size) {
          this.stored = true;
          continue;
        }
        if (this.file === undefined) {
          this.set('interrupted');
          return;
        }

        const end = Math.min(this.offset + this.deps.chunkBytes, this.identity.size);
        const chunk = this.file.slice(this.offset, end);
        const sum = await protocol.checksum(chunk);
        const last = end === this.identity.size;
        this.set('receiving');
        let sent: tus.Sent;
        try {
          sent = await protocol.send(this.caseId, id, this.offset, chunk, sum, {
            onSent: (bytes) => {
              this.inFlight = bytes;
              this.changed();
            },
            // The last byte has left: from here the server is hashing and recording, however long the answer takes.
            onAllSent: () => {
              if (last) {
                this.set('verifying');
              }
            },
            signal,
          });
        } catch (error) {
          this.inFlight = 0;
          if (aborted(error)) {
            return;
          }
          // No answer. The chunk may have arrived whole, in part or not at all: only the server knows.
          if (!(await this.afterCut(signal))) {
            return;
          }
          continue;
        }
        this.inFlight = 0;

        if (sent.ok) {
          this.offset = sent.offset;
          this.tries = 0;
          this.changed();
        } else if (sent.pending) {
          // Stored, and the record waits for the metadata file. The server tries again by itself.
          this.offset = this.identity.size;
          this.stored = true;
        } else if (sent.status === 401) {
          this.endSession();
          return;
        } else if (sent.status === 429) {
          this.set('retrying');
          await this.deps.sleep(sent.retryAfter ?? 1, signal);
        } else if (sent.status === tus.CHECKSUM_MISMATCH || sent.status >= 500) {
          if (!(await this.afterCut(signal))) {
            return;
          }
        } else if (sent.status === 409) {
          // Either the offsets disagree, which the server's offset settles, or the case no longer takes files.
          const before = this.offset;
          if (!(await this.askServer(id, signal))) {
            return;
          }
          if (!this.holdsAll() && this.offset === before) {
            this.end('failed', sent.message);
            return;
          }
        } else {
          this.end('failed', sent.message);
          return;
        }
      }
    } catch (error) {
      if (aborted(error)) {
        return;
      }
      if (error instanceof ApiError && error.status === 401) {
        this.endSession();
        return;
      }
      this.end('failed', error instanceof Error ? error.message : 'the upload stopped');
    }
  }

  /** One more try after a cut, a damaged chunk or a failure of the server. False when the tries ran out. */
  private async afterCut(signal: AbortSignal): Promise<boolean> {
    const wait = RETRY_WAITS[this.tries];
    if (wait === undefined) {
      this.end('failed', 'The connection kept failing. What arrived is still on the server; start again to go on from there.');
      return false;
    }
    this.tries += 1;
    this.set('retrying');
    await this.deps.sleep(wait, signal);
    this.mustAsk = true;
    return true;
  }

  /** Asks the server where the upload stands and takes its word. False when the upload cannot go on. */
  private async askServer(id: string, signal: AbortSignal): Promise<boolean> {
    let asked: tus.Asked;
    try {
      asked = await this.deps.protocol.ask(this.caseId, id, signal);
    } catch (error) {
      if (aborted(error)) {
        return false;
      }
      return this.afterCut(signal);
    }
    switch (asked.kind) {
      case 'locked':
        this.offset = this.identity.size;
        this.stored = true;
        break;
      case 'gone':
        this.end('failed', 'The server no longer holds this upload. Start it again.');
        return false;
      case 'offset':
        this.offset = asked.offset;
        this.stored = asked.offset >= this.identity.size;
        break;
      case 'refused':
        if (asked.status === 401) {
          this.endSession();
          return false;
        }
        if (asked.status >= 500 || asked.status === 429) {
          return this.afterCut(signal);
        }
        this.end('failed', asked.message);
        return false;
    }
    this.mustAsk = false;
    this.changed();
    return true;
  }

  /** Waits for what intake made of the file. Sends nothing. */
  private async verify(id: string, signal: AbortSignal): Promise<void> {
    this.set('verifying');
    for (;;) {
      let now: Awaited<ReturnType<typeof tus.status>>;
      try {
        now = await this.deps.protocol.status(this.caseId, id, signal);
      } catch (error) {
        if (aborted(error) || (error instanceof ApiError && error.status === 401)) {
          throw error;
        }
        // The status route did not answer, or asked for a wait. The file is safe on the server; ask again.
        await this.deps.sleep(error instanceof ApiError && error.retryAfter !== undefined ? error.retryAfter : POLL_SECONDS, signal);
        continue;
      }
      if (now === null) {
        this.end('failed', 'The server no longer knows this upload. Look for the file in the list below.');
        return;
      }
      switch (now.state) {
        case 'done':
          this.end('done', undefined, now.file_id);
          return;
        case 'duplicate':
          this.end('duplicate', now.message ?? undefined, now.file_id);
          return;
        case 'rejected':
          this.end('rejected', now.message ?? 'The server did not take the file.');
          return;
        case 'failed':
          this.end('failed', now.message ?? 'The server could not record the file.');
          return;
        default:
          await this.deps.sleep(POLL_SECONDS, signal);
      }
    }
  }
}
