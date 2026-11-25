import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/http';
import type { UploadStatusResponse } from '../api/schema.gen';
import { FINAL, RETRY_WAITS, Upload, type Deps, type Phase } from './machine';
import { checksum, type Asked, type Created, type Refusal, type Sent } from './protocol';
import { UploadQueue, UPLOADS_AT_ONCE } from './queue';

// A stand-in for the server's side of the upload protocol. It holds bytes, cuts a chunk back when told to lose
// it, checks every chunk against its checksum, and keeps a log of every request, which is what the rules
// about "nothing more is sent" are asserted on.

type SendPlan = 'ok' | 'lost-before' | 'lost-after' | 'pending' | 'damaged' | (Partial<Refusal> & { status: number });

const refuse = (status: number, message = `refused ${status}`, more: Partial<Refusal> = {}): { ok: false } & Refusal => ({ ok: false, status, message, retryAfter: undefined, pending: false, ...more });

class Server {
  stored = 0;
  log: string[] = [];
  sums: string[] = [];
  waits: number[] = [];
  creates: Created[] = [];
  sends: SendPlan[] = [];
  asks: Asked[] = [];
  statuses: Array<Partial<UploadStatusResponse> | null | ApiError> = [];
  /** Called when a PATCH begins, before its plan is applied: a test pauses or aborts from here. */
  onSend: (() => void) | undefined;

  constructor(readonly size: number) {}

  deps(chunkBytes = 4): Deps {
    return {
      chunkBytes,
      sleep: (seconds, signal) => {
        this.waits.push(seconds);
        return signal.aborted ? Promise.reject(new DOMException('aborted', 'AbortError')) : Promise.resolve();
      },
      protocol: {
        checksum,
        create: () => {
          this.log.push('POST');
          return Promise.resolve(this.creates.shift() ?? { ok: true, id: 'up1' });
        },
        ask: () => {
          this.log.push('HEAD');
          return Promise.resolve(this.asks.shift() ?? { kind: 'offset', offset: this.stored, length: this.size, state: 'receiving' });
        },
        send: async (_caseId, _id, offset, chunk, sum, events) => {
          this.log.push(`PATCH ${offset}+${chunk.size}`);
          this.sums.push(sum);
          this.onSend?.();
          if (events.signal?.aborted === true) {
            throw new DOMException('aborted', 'AbortError');
          }
          if (offset !== this.stored) {
            return refuse(409, 'the offset is not where the upload stands');
          }
          const plan = this.sends.shift() ?? 'ok';
          if (plan === 'lost-before') {
            throw new ApiError({ status: 0, slug: 'offline', title: 'no answer from the server' });
          }
          if (plan === 'damaged' || (plan === 'ok' && sum !== (await checksum(chunk)))) {
            return refuse(460, 'the checksum does not match');
          }
          if (typeof plan === 'object') {
            return refuse(plan.status, plan.message, plan);
          }
          events.onSent?.(chunk.size);
          events.onAllSent?.();
          this.stored += chunk.size;
          if (plan === 'lost-after') {
            throw new ApiError({ status: 0, slug: 'offline', title: 'no answer from the server' });
          }
          if (plan === 'pending') {
            return refuse(503, 'the record waits for the metadata file', { retryAfter: 5, pending: true });
          }
          return { ok: true, offset: this.stored } satisfies Sent;
        },
        remove: () => {
          this.log.push('DELETE');
          return Promise.resolve();
        },
        status: () => {
          this.log.push('GET');
          const next = this.statuses.shift();
          if (next instanceof ApiError) {
            return Promise.reject(next);
          }
          if (next === null) {
            return Promise.resolve(null);
          }
          return Promise.resolve({ id: 'up1', state: 'done', offset: this.size, length: this.size, name: 'access.log', created_at: '', expires_at: '', file_id: 9, sha256: 'ab', job_id: 4, message: null, ...next });
        },
      },
    };
  }

  patches(): string[] {
    return this.log.filter((line) => line.startsWith('PATCH'));
  }
}

const file = (size: number, name = 'access.log', lastModified = 1) => new File([new Uint8Array(size).map((_, i) => i % 251)], name, { lastModified });

function start(server: Server, size = server.size, init: { id?: string; withFile?: boolean } = {}) {
  const phases: Phase[] = [];
  const f = file(size);
  const upload: Upload = new Upload(
    { caseId: 7, identity: { name: f.name, size: f.size, lastModified: f.lastModified }, ...(init.withFile === false ? {} : { file: f }), ...(init.id === undefined ? {} : { id: init.id }) },
    server.deps(),
    () => {
      const phase = upload.view().phase;
      if (phases.at(-1) !== phase) {
        phases.push(phase);
      }
    },
  );
  return { upload, phases, f };
}

const until = (upload: Upload, ...wanted: Phase[]) =>
  vi.waitFor(() => {
    expect(wanted.length === 0 ? FINAL.has(upload.view().phase) : wanted.includes(upload.view().phase), `phase is ${upload.view().phase}`).toBe(true);
  });

afterEach(() => {
  window.sessionStorage.clear();
});

describe('an upload', () => {
  it('sends the file in chunks from the offsets the server answers, then waits for the record', async () => {
    const server = new Server(10);
    const { upload, phases } = start(server);
    upload.start();
    await until(upload);

    expect(server.log).toEqual(['POST', 'PATCH 0+4', 'PATCH 4+4', 'PATCH 8+2', 'GET']);
    expect(phases).toEqual(['starting', 'receiving', 'verifying', 'done']);
    expect(upload.view()).toMatchObject({ phase: 'done', fileId: 9, sent: 10 });
  });

  it('sends the SHA-256 of each chunk with it', async () => {
    const server = new Server(10);
    const { upload, f } = start(server);
    upload.start();
    await until(upload);

    expect(server.sums).toEqual([await checksum(f.slice(0, 4)), await checksum(f.slice(4, 8)), await checksum(f.slice(8, 10))]);
    // A known answer: the digest of the three bytes "abc".
    expect(await checksum(new Blob(['abc']))).toBe('sha256 ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=');
  });

  it('sends a chunk the server found damaged again from where it began', async () => {
    const server = new Server(10);
    server.sends = ['ok', 'damaged'];
    const { upload, phases } = start(server);
    upload.start();
    await until(upload);

    expect(server.log).toEqual(['POST', 'PATCH 0+4', 'PATCH 4+4', 'HEAD', 'PATCH 4+4', 'PATCH 8+2', 'GET']);
    expect(phases).toContain('retrying');
    expect(upload.view().phase).toBe('done');
  });

  it('goes on after a cut from the offset the server holds, not from its own count', async () => {
    const server = new Server(10);
    server.sends = ['ok', 'lost-before'];
    const { upload } = start(server);
    upload.start();
    await until(upload);

    expect(server.log).toEqual(['POST', 'PATCH 0+4', 'PATCH 4+4', 'HEAD', 'PATCH 4+4', 'PATCH 8+2', 'GET']);
    expect(server.waits[0]).toBe(RETRY_WAITS[0]);
  });

  it('does not send a chunk again when it arrived and only its answer was lost', async () => {
    const server = new Server(10);
    server.sends = ['lost-after'];
    const { upload } = start(server);
    upload.start();
    await until(upload);

    expect(server.patches()).toEqual(['PATCH 0+4', 'PATCH 4+4', 'PATCH 8+2']);
  });

  it('never sends the last chunk twice: a lost answer to it is settled by asking, and nothing more is sent', async () => {
    const server = new Server(10);
    server.sends = ['ok', 'ok', 'lost-after'];
    const { upload, phases } = start(server);
    upload.start();
    await until(upload);

    expect(server.log).toEqual(['POST', 'PATCH 0+4', 'PATCH 4+4', 'PATCH 8+2', 'HEAD', 'GET']);
    expect(phases.at(-2)).toBe('verifying');
    expect(upload.view().phase).toBe('done');
  });

  it('takes 503 with Retry-After on the last chunk as stored and pending: it polls and sends nothing', async () => {
    const server = new Server(10);
    server.sends = ['ok', 'ok', 'pending'];
    server.statuses = [{ state: 'finalising', file_id: null }, { state: 'finalising', file_id: null }, { state: 'done' }];
    const { upload, phases } = start(server);
    upload.start();
    await until(upload);

    expect(server.log).toEqual(['POST', 'PATCH 0+4', 'PATCH 4+4', 'PATCH 8+2', 'GET', 'GET', 'GET']);
    expect(phases).toEqual(['starting', 'receiving', 'verifying', 'done']);
    expect(upload.view().message).toBeUndefined();
  });

  it('takes 423 on a HEAD as verifying: the last request is still at work and nothing is sent', async () => {
    const server = new Server(10);
    server.sends = ['ok', 'ok', 'lost-after'];
    server.asks = [{ kind: 'locked' }];
    server.statuses = [{ state: 'finalising', file_id: null }, { state: 'done' }];
    const { upload } = start(server);
    upload.start();
    await until(upload);

    expect(server.log).toEqual(['POST', 'PATCH 0+4', 'PATCH 4+4', 'PATCH 8+2', 'HEAD', 'GET', 'GET']);
  });

  it('shows verifying from the moment the last byte has left, before the answer arrives', async () => {
    const server = new Server(4);
    const { upload, phases } = start(server);
    upload.start();
    await until(upload);
    expect(phases).toEqual(['starting', 'receiving', 'verifying', 'done']);
  });

  it.each([
    ['duplicate', { state: 'duplicate', file_id: 3, message: 'the case already holds these bytes' }, 'the case already holds these bytes', 3],
    ['rejected', { state: 'rejected', file_id: null, message: 'a packet capture is not taken' }, 'a packet capture is not taken', undefined],
    ['failed', { state: 'failed', file_id: null, message: 'intake could not record the file' }, 'intake could not record the file', undefined],
  ] as const)('ends as %s with the words of the server', async (phase, answer, message, fileId) => {
    const server = new Server(4);
    server.statuses = [answer];
    const { upload } = start(server);
    upload.start();
    await until(upload);
    expect(upload.view()).toMatchObject({ phase, message, fileId });
  });

  it.each([
    [400, 'filename: required'],
    [409, 'case 7 is closed; only an active case takes files'],
    [413, 'the file is over the limit of 20 GB'],
    [507, 'the queue is paused'],
  ])('refused at creation with %i ends as refused, in the words of the server, and sends nothing', async (status, message) => {
    const server = new Server(10);
    server.creates = [refuse(status, message)];
    const { upload } = start(server);
    upload.start();
    await until(upload);
    expect(upload.view()).toMatchObject({ phase: 'refused', message });
    expect(server.log).toEqual(['POST']);
  });

  it('waits as long as a 429 asks and then goes on, at creation and in the middle', async () => {
    const server = new Server(8);
    server.creates = [refuse(429, 'busy', { retryAfter: 3 })];
    server.sends = [{ status: 429, retryAfter: 7 }];
    const { upload } = start(server);
    upload.start();
    await until(upload);
    expect(server.waits).toEqual([3, 7]);
    expect(server.patches()).toEqual(['PATCH 0+4', 'PATCH 0+4', 'PATCH 4+4']);
    expect(upload.view().phase).toBe('done');
  });

  it('pauses when asked, and on resume asks the server where to go on', async () => {
    const server = new Server(10);
    const { upload } = start(server);
    let paused = false;
    server.onSend = () => {
      if (!paused && server.stored === 4) {
        paused = true;
        upload.pause();
      }
    };
    upload.start();
    await until(upload, 'paused');
    expect(server.patches()).toEqual(['PATCH 0+4', 'PATCH 4+4']);

    upload.resume();
    expect(upload.view().phase).toBe('waiting');
    upload.start();
    await until(upload);
    expect(server.log).toEqual(['POST', 'PATCH 0+4', 'PATCH 4+4', 'HEAD', 'PATCH 4+4', 'PATCH 8+2', 'GET']);
  });

  it('removes what the server holds when cancelled, unless the file is already with intake', async () => {
    const server = new Server(10);
    const { upload } = start(server);
    server.onSend = () => {
      if (server.stored === 4) {
        upload.pause();
      }
    };
    upload.start();
    await until(upload, 'paused');
    await upload.cancel();
    expect(server.log.at(-1)).toBe('DELETE');

    const second = new Server(4);
    const done = start(second);
    done.upload.start();
    await until(done.upload);
    await done.upload.cancel();
    expect(second.log).not.toContain('DELETE');
  });

  it('pauses when the session ends and goes on from the server offset after a new sign-in', async () => {
    const server = new Server(10);
    server.sends = ['ok', { status: 401 }];
    const { upload } = start(server);
    upload.start();
    await until(upload, 'paused');
    expect(upload.view()).toMatchObject({ phase: 'paused', sessionEnded: true });

    upload.resume();
    upload.start();
    await until(upload);
    expect(upload.view()).toMatchObject({ phase: 'done', sessionEnded: false });
    expect(server.patches()).toEqual(['PATCH 0+4', 'PATCH 4+4', 'PATCH 4+4', 'PATCH 8+2']);
  });

  it('fails when the tries after a cut run out, and says the bytes are still on the server', async () => {
    const server = new Server(10);
    server.sends = Array.from({ length: RETRY_WAITS.length + 1 }, () => 'lost-before' as const);
    const { upload } = start(server);
    upload.start();
    await until(upload);
    expect(upload.view().phase).toBe('failed');
    expect(upload.view().message).toMatch(/still on the server/);
    expect(server.waits).toEqual([...RETRY_WAITS]);
  });

  it('settles a 409 by the server offset, and fails with its words when the offsets agree', async () => {
    const moved = new Server(10);
    const a = start(moved);
    moved.onSend = () => {
      // Another request of this upload got there first.
      if (moved.patches().length === 1) {
        moved.stored = 4;
      }
    };
    a.upload.start();
    await until(a.upload);
    expect(moved.log).toEqual(['POST', 'PATCH 0+4', 'HEAD', 'PATCH 4+4', 'PATCH 8+2', 'GET']);

    const closed = new Server(10);
    closed.sends = [{ status: 409, message: 'case 7 is closed; only an active case takes files' }];
    const b = start(closed);
    b.upload.start();
    await until(b.upload);
    expect(b.upload.view()).toMatchObject({ phase: 'failed', message: 'case 7 is closed; only an active case takes files' });
  });

  it('fails when the server no longer holds the upload', async () => {
    const server = new Server(10);
    server.sends = ['lost-before'];
    server.asks = [{ kind: 'gone' }];
    const { upload } = start(server);
    upload.start();
    await until(upload);
    expect(upload.view().phase).toBe('failed');
    expect(server.patches()).toHaveLength(1);
  });

  it('fails with the words of the server for a refusal that asking again would not change', async () => {
    const server = new Server(10);
    server.sends = [{ status: 400, message: 'the upload has expired' }];
    const { upload } = start(server);
    upload.start();
    await until(upload);
    expect(upload.view()).toMatchObject({ phase: 'failed', message: 'the upload has expired' });
  });

  it('keeps asking for the result when the status route does not answer or asks for a wait', async () => {
    const server = new Server(4);
    server.statuses = [new ApiError({ status: 0, slug: 'offline', title: 'no answer' }), new ApiError({ status: 429, slug: 'throttled', title: 'busy', retryAfter: 4 }), { state: 'done' }];
    const { upload } = start(server);
    upload.start();
    await until(upload);
    expect(upload.view().phase).toBe('done');
    expect(server.waits).toEqual([2, 4]);
  });
});

describe('an upload after a reload of the page', () => {
  it('knows how far it got, waits for the same file, and goes on from the server offset', async () => {
    const server = new Server(10);
    server.stored = 4;
    const { upload, f } = start(server, 10, { id: 'up1', withFile: false });
    expect(upload.view().phase).toBe('interrupted');
    await upload.refresh();
    expect(upload.view()).toMatchObject({ phase: 'interrupted', sent: 4 });

    expect(upload.attach(file(10, 'other.log'))).toBe(false);
    expect(upload.attach(file(9))).toBe(false);
    expect(upload.attach(file(10, 'access.log', 2))).toBe(false);
    expect(upload.attach(f)).toBe(true);
    upload.start();
    await until(upload);
    expect(server.log).toEqual(['HEAD', 'HEAD', 'PATCH 4+4', 'PATCH 8+2', 'GET']);
  });

  it('needs no file when every byte had arrived: it goes straight to its result', async () => {
    const server = new Server(10);
    server.stored = 10;
    const { upload } = start(server, 10, { id: 'up1', withFile: false });
    await upload.refresh();
    expect(upload.view().phase).toBe('waiting');
    upload.start();
    await until(upload);
    expect(server.patches()).toEqual([]);
    expect(upload.view().phase).toBe('done');
  });

  it('says so when the server no longer holds it', async () => {
    const server = new Server(10);
    server.asks = [{ kind: 'gone' }];
    const { upload } = start(server, 10, { id: 'up1', withFile: false });
    await upload.refresh();
    expect(upload.view().phase).toBe('failed');
  });
});

describe('the uploads of a tab', () => {
  it(`runs ${UPLOADS_AT_ONCE} at a time and starts the next when one ends`, async () => {
    const server = new Server(4);
    let release: (() => void) | undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    const deps = server.deps();
    const queue = new UploadQueue({ ...deps, protocol: { ...deps.protocol, status: async (...args) => (await held, deps.protocol.status(...args)), send: (_c, _i, _o, chunk) => Promise.resolve({ ok: true, offset: chunk.size }) } });
    queue.add(7, [file(4, 'a.log'), file(4, 'b.log'), file(4, 'c.log')], undefined);

    await vi.waitFor(() => {
      expect(queue.views().map((u) => u.phase)).toEqual(['verifying', 'verifying', 'waiting']);
    });
    expect(queue.unsafeToLeave()).toBe(true);
    release?.();
    await vi.waitFor(() => {
      expect(queue.views().map((u) => u.phase)).toEqual(['done', 'done', 'done']);
    });
    expect(queue.unsafeToLeave()).toBe(false);
  });

  it('remembers an unfinished upload across a reload, for the same user and case only, and forgets it at sign-out', async () => {
    const server = new Server(10);
    server.sends = ['ok', { status: 401 }];
    const before = new UploadQueue(server.deps());
    before.restore(7, 'alice');
    before.add(7, [file(10)], 'web-01');
    await vi.waitFor(() => {
      expect(before.views()[0]?.phase).toBe('paused');
    });

    // A reload: a new queue, the same tab storage, no file in hand.
    const other = new UploadQueue(server.deps());
    other.restore(7, 'bob');
    other.restore(8, 'alice');
    expect(other.views()).toEqual([]);

    const after = new UploadQueue(server.deps());
    after.restore(7, 'alice');
    await vi.waitFor(() => {
      expect(after.views()[0]).toMatchObject({ phase: 'interrupted', name: 'access.log', sent: 4, host: 'web-01', id: 'up1' });
    });
    expect(after.unsafeToLeave()).toBe(false);

    // The same file chosen again continues that upload; it does not start a second one.
    after.add(7, [file(10)], undefined);
    await vi.waitFor(() => {
      expect(after.views().map((u) => u.phase)).toEqual(['done']);
    });
    expect(server.patches()).toEqual(['PATCH 0+4', 'PATCH 4+4', 'PATCH 4+4', 'PATCH 8+2']);
    expect(window.sessionStorage.getItem('urlog.uploads')).toBe('[]');

    before.forget();
    expect(before.views()).toEqual([]);
  });

  it('starts a failed upload again as a new one with the same file', async () => {
    const server = new Server(4);
    server.creates = [refuse(507, 'the queue is paused')];
    const queue = new UploadQueue(server.deps());
    queue.add(7, [file(4)], undefined);
    await vi.waitFor(() => {
      expect(queue.views()[0]?.phase).toBe('refused');
    });
    queue.again(queue.views()[0]?.key ?? '');
    await vi.waitFor(() => {
      expect(queue.views().map((u) => u.phase)).toEqual(['done']);
    });
  });
});
