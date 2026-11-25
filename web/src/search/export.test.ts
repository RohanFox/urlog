import { CompletionContext } from '@codemirror/autocomplete';
import { EditorState } from '@codemirror/state';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setCsrfToken } from '../api/http';
import { fakeServer, problem } from '../test-server';
import { complete, type Words } from './Editor';
import { ExportJob, exportName, exportUrl } from './export';
import type { SearchRequest } from './run';
import { decode } from './stream';
import { isWord, searchPart } from './terms';

const request: SearchRequest = { caseId: 7, q: 'status=404 "a b" | head 5', from: '-7d@d', to: undefined, tz: 'Europe/Bucharest' };
const EXPORT = 'GET /api/v1/cases/7/search/export';

function body(...chunks: string[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(encoder.encode(chunk));
      }
      controller.close();
    },
  });
}

/** A file the page was let to write, as the browser hands it out: what was written, and what became of it. */
function fileHandle(name = 'chosen.csv') {
  const written: string[] = [];
  const state = { closed: false, aborted: false, removed: false };
  const decoder = new TextDecoder();
  const handle = {
    name,
    createWritable: () =>
      Promise.resolve(
        new WritableStream<Uint8Array>({
          write(chunk) {
            written.push(decoder.decode(chunk));
          },
          close() {
            state.closed = true;
          },
          abort() {
            state.aborted = true;
          },
        }),
      ),
    remove: () => {
      state.removed = true;
      return Promise.resolve();
    },
  };
  return { handle, written, state };
}

function withPicker(handle: unknown) {
  const picker = vi.fn(() => (handle instanceof Error ? Promise.reject(handle) : Promise.resolve(handle)));
  vi.stubGlobal('showSaveFilePicker', picker);
  return picker;
}

async function finished(job: ExportJob): Promise<void> {
  await vi.waitFor(() => {
    if (job.live) {
      throw new Error(`still ${job.state.phase}`);
    }
  });
}

afterEach(() => {
  setCsrfToken(undefined);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the address of an export', () => {
  it('is the search of the page in the format asked for, each part one encoded value', () => {
    expect(exportUrl(request, 'csv', false)).toBe('/api/v1/cases/7/search/export?format=csv&query=status%3D404+%22a+b%22+%7C+head+5&from=-7d%40d&tz=Europe%2FBucharest');
    expect(exportUrl({ caseId: 7, q: '', from: undefined, to: 'now', tz: undefined }, 'json', false)).toBe('/api/v1/cases/7/search/export?format=json&query=&to=now');
  });

  it('asks for the formula guard only for CSV', () => {
    expect(exportUrl(request, 'csv', true)).toContain('&formula_guard=true');
    expect(exportUrl(request, 'json', true)).not.toContain('formula_guard');
  });

  it('names the file by the case and the format', () => {
    expect([exportName(request, 'csv'), exportName(request, 'json')]).toEqual(['case-7-export.csv', 'case-7-export.jsonl']);
  });
});

describe('an export written to a file the reader chose', () => {
  it('asks where first, then writes what arrives, counts it, and reads the rows and the cut from the headers', async () => {
    const file = fileHandle();
    const picker = withPicker(file.handle);
    const server = fakeServer({ [EXPORT]: { status: 200, stream: body('ts,host\n', '2026-03-01T00:00:00.000Z,web-01\n'), headers: { 'Content-Type': 'text/csv; charset=utf-8', 'X-Urlog-Rows': '12619819', 'X-Urlog-Truncated': 'true' } } });
    const job = new ExportJob(request, 'csv', false);
    const phases: string[] = [];
    job.subscribe(() => {
      if (phases.at(-1) !== job.state.phase) {
        phases.push(job.state.phase);
      }
    });

    await job.start();
    expect(picker).toHaveBeenCalledWith(expect.objectContaining({ suggestedName: 'case-7-export.csv' }));
    expect(phases).toEqual(['asking', 'receiving', 'done']);
    expect(file.written.join('')).toBe('ts,host\n2026-03-01T00:00:00.000Z,web-01\n');
    expect(file.state).toEqual({ closed: true, aborted: false, removed: false });
    expect(job.state).toMatchObject({ phase: 'done', file: 'chosen.csv', bytes: 40, rows: 12619819, cut: true });
    expect(server.sent[0]?.path).toBe(exportUrl(request, 'csv', false));
  });

  it('asks nothing of the server when the dialog is closed', async () => {
    withPicker(new Error('The user aborted a request.'));
    const server = fakeServer({});
    const job = new ExportJob(request, 'csv', false);
    await job.start();
    expect(job.state.phase).toBe('cancelled');
    expect(job.state.file).toBeUndefined();
    expect(server.sent).toHaveLength(0);
  });

  it('shows a refusal as one and leaves no file behind', async () => {
    const file = fileHandle();
    withPicker(file.handle);
    fakeServer({ [EXPORT]: { status: 429, body: { type: 'https://urlog/problems/busy', title: 'too many queries are running', status: 429, code: 'busy' }, headers: { 'Retry-After': '1' } } });
    const job = new ExportJob(request, 'csv', false);
    await job.start();
    expect(job.state).toMatchObject({ phase: 'refused', bytes: 0, refusal: { status: 429, code: 'busy' } });
    expect(file.written).toEqual([]);
    expect(file.state.removed).toBe(true);
  });

  it('shows a refused query in the words of the server', async () => {
    withPicker(fileHandle().handle);
    fakeServer({ [EXPORT]: problem(400, 'query-syntax', 'not supported in v1: eval', { code: 'query_syntax', offset: 14, length: 4 }) });
    const job = new ExportJob(request, 'csv', false);
    await job.start();
    expect(job.state.refusal).toMatchObject({ status: 400, code: 'query_syntax', message: 'not supported in v1: eval' });
  });

  it('keeps nothing of a download that breaks off after it began', async () => {
    const file = fileHandle();
    withPicker(file.handle);
    const encoder = new TextEncoder();
    let controller: ReadableStreamDefaultController<Uint8Array> | undefined;
    const broken = new ReadableStream<Uint8Array>({
      start(c) {
        controller = c;
      },
    });
    fakeServer({ [EXPORT]: { status: 200, stream: broken, headers: { 'X-Urlog-Rows': '3' } } });
    const job = new ExportJob(request, 'csv', false);
    const done = job.start();
    await vi.waitFor(() => {
      expect(job.state.phase).toBe('receiving');
    });
    controller?.enqueue(encoder.encode('ts,host\n'));
    await vi.waitFor(() => {
      expect(job.state.bytes).toBe(8);
    });
    controller?.error(new TypeError('network error'));
    await done;

    expect(job.state.phase).toBe('failed');
    expect(file.state.aborted).toBe(true);
    expect(file.state.closed).toBe(false);
    expect(file.state.removed).toBe(true);
  });

  it('stops and keeps nothing when it is cancelled', async () => {
    const file = fileHandle();
    withPicker(file.handle);
    let controller: ReadableStreamDefaultController<Uint8Array> | undefined;
    const open = new ReadableStream<Uint8Array>({
      start(c) {
        controller = c;
      },
    });
    fakeServer({ [EXPORT]: { status: 200, stream: open } });
    const job = new ExportJob(request, 'json', false);
    const done = job.start();
    await vi.waitFor(() => {
      expect(job.state.phase).toBe('receiving');
    });
    controller?.enqueue(new TextEncoder().encode('{"a":1}\n'));
    job.cancel();
    expect(job.state.phase).toBe('cancelled');
    await done;
    await finished(job);
    expect(file.state.closed).toBe(false);
    expect(file.state.removed).toBe(true);
  });
});

describe('an export in a browser that cannot write a file it chose', () => {
  it('holds what arrives and hands it to the browser as a download under the name of the export', async () => {
    vi.stubGlobal('showSaveFilePicker', undefined);
    const made: Blob[] = [];
    const clicked: Array<{ href: string; download: string }> = [];
    vi.spyOn(URL, 'createObjectURL').mockImplementation((blob) => {
      made.push(blob as Blob);
      return 'blob:held';
    });
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function click(this: HTMLAnchorElement) {
      clicked.push({ href: this.href, download: this.download });
    });
    fakeServer({ [EXPORT]: { status: 200, stream: body('{"a":1}\n', '{"a":2}\n'), headers: { 'Content-Type': 'application/x-ndjson', 'X-Urlog-Rows': '2' } } });

    const job = new ExportJob(request, 'json', false);
    await job.start();
    expect(job.state).toMatchObject({ phase: 'done', file: 'case-7-export.jsonl', bytes: 16, rows: 2, cut: false });
    expect(clicked).toEqual([{ href: 'blob:held', download: 'case-7-export.jsonl' }]);
    expect(await made[0]?.text()).toBe('{"a":1}\n{"a":2}\n');
    expect(made[0]?.type).toBe('application/x-ndjson');
  });

  it('hands nothing over when the server refuses', async () => {
    vi.stubGlobal('showSaveFilePicker', undefined);
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    fakeServer({ [EXPORT]: problem(504, 'query-timeout', 'the query passed its time limit', { code: 'query_timeout' }) });
    const job = new ExportJob(request, 'csv', false);
    await job.start();
    expect(job.state).toMatchObject({ phase: 'refused', refusal: { code: 'query_timeout' } });
    expect(click).not.toHaveBeenCalled();
  });
});

describe('the word at the cursor, completed', () => {
  const words: Words = {
    fields: [
      { name: 'http.response.status_code', detail: 'int' },
      { name: 'status', detail: 'http.response.status_code' },
      { name: 'host.name', detail: 'keyword' },
    ],
    verbs: ['where', 'stats', 'sort', 'head'],
  };

  function at(text: string, explicit = false) {
    return complete(words, new CompletionContext(EditorState.create({ doc: text }), text.length, explicit));
  }

  it('offers the fields of the case for a word of the search, from where the word begins', () => {
    const result = at('host.name=web-01 sta');
    expect(result?.from).toBe(17);
    expect(result?.options.map((o) => o.label)).toEqual(['http.response.status_code', 'status', 'host.name']);
    expect(result?.options[1]?.detail).toBe('http.response.status_code');
  });

  it('offers the verbs after a pipe, and only them', () => {
    expect(at('status=404 | st')?.options.map((o) => o.label)).toEqual(['where', 'stats', 'sort', 'head']);
    expect(at('status=404 |', true)?.options.map((o) => o.label)).toEqual(['where', 'stats', 'sort', 'head']);
  });

  it('offers nothing for a value, nor without a word unless it is asked for', () => {
    expect(at('status=40')).toBeNull();
    expect(at('status!=4')).toBeNull();
    expect(at('status=404 ')).toBeNull();
    expect(at('status=404 ', true)?.options).toHaveLength(3);
  });

  it('offers nothing when the case has no names to offer', () => {
    expect(complete({ fields: [], verbs: [] }, new CompletionContext(EditorState.create({ doc: 'sta' }), 3, false))).toBeNull();
  });
});

describe('small rules the sidebar and the box share', () => {
  it('takes the search part of a query: what stands before its first command', () => {
    expect(searchPart('status=404 | head 5')).toBe('status=404');
    expect(searchPart('msg="a | b" host=x | stats count')).toBe('msg="a | b" host=x');
    expect(searchPart('| stats count')).toBe('');
    expect(searchPart('')).toBe('');
  });

  it('knows a name that can stand in a query as it is', () => {
    expect(['url.path', 'log.syslog.structured_data.exampleSDID@32473.eventID', '_time'].every(isWord)).toBe(true);
    expect(['a b', 'a|b', '<img>', '', '1abc', 'a=b'].some(isWord)).toBe(false);
  });

  it('reads the values the server counted for a search', () => {
    const frame = decode('counts', JSON.stringify({ kind: 'counts', total: 9, source: 'query', fields: [{ name: 'program', top: [{ value: 'nginx', count: 7 }, { value: 'sshd', count: 2 }] }, { name: 'host.name', top: [] }] }));
    expect(frame).toEqual({ kind: 'counts', total: 9, source: 'query', fields: [{ name: 'program', top: [{ value: 'nginx', count: 7 }, { value: 'sshd', count: 2 }] }, { name: 'host.name', top: [] }] });
    expect(() => decode('counts', JSON.stringify({ kind: 'counts', total: 9, source: 'query', fields: [{ name: 'program' }] }))).toThrow('top');
  });
});
