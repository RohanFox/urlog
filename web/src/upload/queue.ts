import { useSyncExternalStore } from 'react';
import { ACTIVE, FINAL, realDeps, UNSAFE_TO_LEAVE, Upload, type Deps, type FileIdentity, type UploadView } from './machine';

// The uploads of this tab: which run, which wait, and what a reload of the page leaves behind.

/**
 * Uploads at work at once. Receiving is cheap, but each finished upload is hashed by the request that brings
 * its last byte and parsed by a queue that takes one job at a time, so more at once only moves the wait.
 */
export const UPLOADS_AT_ONCE = 2;

/**
 * What survives a reload: the address of each unfinished upload and enough about its file to know it again.
 * In the tab's own storage, so it ends with the tab. The bytes themselves do not survive: a page cannot open
 * a file again by itself, so the user chooses it once more and the upload goes on from the server's offset.
 */
const STORAGE_KEY = 'urlog.uploads';

interface Remembered extends FileIdentity {
  caseId: number;
  user: string;
  id: string;
  host: string | undefined;
}

function readRemembered(): Remembered[] {
  try {
    const parsed: unknown = JSON.parse(window.sessionStorage.getItem(STORAGE_KEY) ?? '[]');
    return Array.isArray(parsed) ? (parsed as Remembered[]).filter((r) => typeof r.id === 'string' && typeof r.name === 'string' && typeof r.size === 'number') : [];
  } catch {
    return [];
  }
}

export class UploadQueue {
  private uploads: Upload[] = [];
  private snapshot: readonly UploadView[] = [];
  private readonly listeners = new Set<() => void>();
  private user = '';
  private readonly restored = new Set<string>();
  private granting = false;
  private recount = false;

  constructor(private readonly deps: Deps = realDeps) {}

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  /** Every upload of the tab, oldest first. The same array until something changes. */
  views = (): readonly UploadView[] => this.snapshot;

  /** Files the user dropped or chose. One that matches an interrupted upload continues it. */
  add(caseId: number, files: readonly File[], host: string | undefined): void {
    for (const file of files) {
      const waiting = this.uploads.find((u) => u.view().caseId === caseId && u.view().phase === 'interrupted' && u.attach(file));
      if (waiting === undefined) {
        this.uploads.push(new Upload({ caseId, file, identity: { name: file.name, size: file.size, lastModified: file.lastModified }, host }, this.deps, this.changed));
      }
    }
    this.changed();
  }

  /** After a reload or a sign-in: the unfinished uploads this tab remembers for the user and the case. */
  restore(caseId: number, user: string): void {
    this.user = user;
    for (const r of readRemembered()) {
      if (r.caseId !== caseId || r.user !== user || this.restored.has(r.id) || this.uploads.some((u) => u.view().id === r.id)) {
        continue;
      }
      this.restored.add(r.id);
      const upload = new Upload({ caseId, identity: { name: r.name, size: r.size, lastModified: r.lastModified }, host: r.host, id: r.id }, this.deps, this.changed);
      this.uploads.push(upload);
      void upload.refresh();
    }
    // Uploads the end of the session paused go on by themselves once someone is signed in again.
    for (const upload of this.uploads) {
      if (upload.view().sessionEnded) {
        upload.resume();
      }
    }
    this.changed();
  }

  pause(key: string): void {
    this.find(key)?.pause();
  }

  resume(key: string): void {
    this.find(key)?.resume();
    this.changed();
  }

  /** Cancels an upload that is not stored yet, or clears the row of one that has ended. */
  async dismiss(key: string): Promise<void> {
    const upload = this.find(key);
    if (upload === undefined) {
      return;
    }
    this.uploads = this.uploads.filter((u) => u !== upload);
    this.changed();
    await upload.cancel();
  }

  /** Starts a refused or failed upload again with the same file, as a new upload. */
  again(key: string): void {
    const upload = this.find(key);
    const file = upload?.source;
    if (upload === undefined || file === undefined) {
      return;
    }
    const { caseId, host } = upload.view();
    this.uploads = this.uploads.filter((u) => u !== upload);
    this.add(caseId, [file], host);
  }

  /** True while closing the page would lose bytes that have not reached the server. */
  unsafeToLeave(): boolean {
    return this.snapshot.some((u) => UNSAFE_TO_LEAVE.has(u.phase));
  }

  /** The session ended for good (a sign-out): nothing of this user's uploads stays in the tab. */
  forget(): void {
    for (const upload of this.uploads) {
      upload.pause();
    }
    this.uploads = [];
    this.restored.clear();
    try {
      window.sessionStorage.removeItem(STORAGE_KEY);
    } catch {
      // Storage refused: there was nothing kept.
    }
    this.changed();
  }

  private find(key: string): Upload | undefined {
    return this.uploads.find((u) => u.key === key);
  }

  private readonly changed = (): void => {
    // An upload that starts reports its first change before start() returns. Such a call is noted and the
    // slots are counted again afterwards, so the count is never one taken before that upload began.
    if (this.granting) {
      this.recount = true;
      return;
    }
    this.granting = true;
    try {
      do {
        this.recount = false;
        for (const upload of this.uploads) {
          const active = this.uploads.filter((u) => ACTIVE.has(u.view().phase)).length;
          if (active >= UPLOADS_AT_ONCE) {
            break;
          }
          // Hand a free slot to the upload that has waited longest.
          if (upload.view().phase === 'waiting') {
            upload.start();
          }
        }
      } while (this.noted());
    } finally {
      this.granting = false;
    }
    this.snapshot = this.uploads.map((u) => u.view());
    this.remember();
    for (const listener of this.listeners) {
      listener();
    }
  };

  /** Read through a call: an upload that started inside the loop may have set it. */
  private noted(): boolean {
    return this.recount;
  }

  private remember(): void {
    const mine: Remembered[] = this.snapshot
      .filter((u) => u.id !== undefined && !FINAL.has(u.phase))
      .map((u) => ({ caseId: u.caseId, user: this.user, id: u.id ?? '', name: u.name, size: u.size, lastModified: u.lastModified, host: u.host }));
    try {
      // Uploads of other cases that this page has not restored stay remembered.
      const others = readRemembered().filter((r) => !this.restored.has(r.id) && !mine.some((m) => m.id === r.id) && !this.snapshot.some((u) => u.id === r.id));
      window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify([...others, ...mine]));
    } catch {
      // Storage refused: uploads still work, and a reload forgets them.
    }
  }
}

/** The queue of the tab. It outlives the pages, so an upload goes on while the user looks at another case. */
export const uploads = new UploadQueue();

export function useUploads(caseId: number): readonly UploadView[] {
  const all = useSyncExternalStore(uploads.subscribe, uploads.views);
  return all.filter((u) => u.caseId === caseId);
}
