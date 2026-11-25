import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { setCsrfToken } from '../api/http';
import { fakeServer } from '../test-server';
import { ExportMenu } from './ExportMenu';
import type { SearchRequest } from './run';

const request: SearchRequest = { caseId: 7, q: 'status=404', from: undefined, to: undefined, tz: undefined };

/** A result at the server's row limit with lines of 400 bytes: about 590 MB as CSV with the raw line, 150 MB brief. */
const LARGE = { rows: 5_000_000, averageRaw: 400 };
const SMALL = { rows: 65, averageRaw: 120 };

function show(size: { rows: number | undefined; averageRaw: number }, listing = true) {
  render(<ExportMenu request={request} disabled={false} rows={size.rows} averageRaw={size.averageRaw} listing={listing} />);
  return userEvent.setup();
}

afterEach(() => {
  setCsrfToken(undefined);
  vi.unstubAllGlobals();
});

describe('the export menu in a browser that cannot write a file it chose', () => {
  it('offers a large result as a plain download of the address and says why there is no progress', async () => {
    vi.stubGlobal('showSaveFilePicker', undefined);
    const user = show(LARGE);
    await user.click(screen.getByRole('button', { name: 'Export' }));

    const csv = await screen.findByRole('menuitem', { name: 'CSV' });
    // The default is the evidence form, which is the server's default: the address names no columns.
    expect(csv).toHaveAttribute('href', '/api/v1/cases/7/search/export?format=csv&query=status%3D404');
    expect(csv).toHaveAttribute('download', 'case-7-export.csv');
    expect(screen.getByRole('menuitem', { name: 'JSON lines' })).toHaveAttribute('href', '/api/v1/cases/7/search/export?format=json&query=status%3D404');
    expect(screen.getByText(/This browser cannot write a file as it arrives/)).toHaveTextContent('the page shows no progress, and a refusal of the server arrives as the content of the file');

    // The guard is part of the address.
    await user.click(screen.getByRole('menuitemcheckbox', { name: 'Guard formula cells (CSV)' }));
    expect(screen.getByRole('menuitem', { name: 'CSV, formula cells guarded' })).toHaveAttribute('href', '/api/v1/cases/7/search/export?format=csv&query=status%3D404&formula_guard=true');
  });

  it('holds and follows the same result in its brief form, which is small enough', async () => {
    vi.stubGlobal('showSaveFilePicker', undefined);
    const user = show(LARGE);
    await user.click(screen.getByRole('button', { name: 'Export' }));
    await user.click(await screen.findByRole('menuitemcheckbox', { name: 'Brief columns' }));

    expect(screen.getByRole('menuitem', { name: 'CSV, brief' })).not.toHaveAttribute('href');
    // JSON lines of the brief form is half as large again and still under the limit.
    expect(screen.getByRole('menuitem', { name: 'JSON lines, brief' })).not.toHaveAttribute('href');
    expect(screen.queryByText(/This browser cannot write a file as it arrives/)).toBeNull();
  });

  it('holds and follows a small result, and one whose size is not known yet is taken for the largest', async () => {
    vi.stubGlobal('showSaveFilePicker', undefined);
    const user = show(SMALL);
    await user.click(screen.getByRole('button', { name: 'Export' }));
    expect(await screen.findByRole('menuitem', { name: 'CSV' })).not.toHaveAttribute('href');
    expect(screen.queryByText(/This browser cannot write a file as it arrives/)).toBeNull();
  });

  it('takes a result that is not counted yet for the largest there can be', async () => {
    vi.stubGlobal('showSaveFilePicker', undefined);
    const user = show({ rows: undefined, averageRaw: 400 });
    await user.click(screen.getByRole('button', { name: 'Export' }));
    expect(await screen.findByRole('menuitem', { name: 'CSV' })).toHaveAttribute('href');
  });
});

describe('the export menu', () => {
  it('follows any export where the browser can write the file, however large', async () => {
    vi.stubGlobal('showSaveFilePicker', () => Promise.reject(new Error('closed')));
    const user = show(LARGE);
    await user.click(screen.getByRole('button', { name: 'Export' }));
    expect(await screen.findByRole('menuitem', { name: 'CSV' })).not.toHaveAttribute('href');
    expect(screen.queryByText(/This browser cannot write a file as it arrives/)).toBeNull();
  });

  it('says what an export of events carries, and asks for the brief form by name', async () => {
    const written: string[] = [];
    vi.stubGlobal('showSaveFilePicker', () =>
      Promise.resolve({
        name: 'brief.csv',
        createWritable: () =>
          Promise.resolve(
            new WritableStream<Uint8Array>({
              write: (chunk) => {
                written.push(new TextDecoder().decode(chunk));
              },
            }),
          ),
      }),
    );
    const server = fakeServer({ 'GET /api/v1/cases/7/search/export': { status: 200, text: 'ts,host\n', headers: { 'X-Urlog-Rows': '65' } } });
    const user = show(SMALL);
    await user.click(screen.getByRole('button', { name: 'Export' }));
    expect(await screen.findByText(/Every row carries the file and the offset it came from and its raw line/)).toBeInTheDocument();

    await user.click(screen.getByRole('menuitemcheckbox', { name: 'Brief columns' }));
    await user.click(screen.getByRole('menuitem', { name: 'CSV, brief' }));
    expect(await screen.findByText(/^Exported/)).toHaveTextContent('Exported brief.csv, 8 B, 65 rows');
    expect(server.sent[0]?.path).toBe('/api/v1/cases/7/search/export?format=csv&query=status%3D404&columns=brief');
  });

  it('exports the files a result drew from as a file of its own', async () => {
    const written: string[] = [];
    const picker = vi.fn(() =>
      Promise.resolve({
        name: 'files.csv',
        createWritable: () =>
          Promise.resolve(
            new WritableStream<Uint8Array>({
              write: (chunk) => {
                written.push(new TextDecoder().decode(chunk));
              },
            }),
          ),
      }),
    );
    vi.stubGlobal('showSaveFilePicker', picker);
    const server = fakeServer({ 'GET /api/v1/cases/7/search/export/files': { status: 200, text: 'file_id,name,host,size,sha256,rows\r\n11,access.log,web-01,2048,a3f1,65\r\n', headers: { 'X-Urlog-Rows': '1' } } });
    const user = show(SMALL);
    await user.click(screen.getByRole('button', { name: 'Export' }));
    await user.click(await screen.findByRole('menuitem', { name: 'The files of this result, with their hashes (CSV)' }));

    expect(await screen.findByText(/^Exported/)).toHaveTextContent('Exported files.csv');
    expect(picker).toHaveBeenCalledWith(expect.objectContaining({ suggestedName: 'case-7-export-files.csv' }));
    expect(server.sent[0]?.path).toBe('/api/v1/cases/7/search/export/files?format=csv&query=status%3D404');
    expect(written.join('')).toContain('11,access.log,web-01,2048,a3f1,65');
  });

  it('offers an aggregation its table alone: no files to list, no columns to choose', async () => {
    vi.stubGlobal('showSaveFilePicker', undefined);
    const user = show(SMALL, false);
    await user.click(screen.getByRole('button', { name: 'Export' }));
    expect(await screen.findByRole('menuitem', { name: 'CSV' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: /files of this result/ })).toBeNull();
    expect(screen.queryByRole('menuitemcheckbox', { name: 'Brief columns' })).toBeNull();
    expect(screen.queryByText(/Every row carries the file/)).toBeNull();
  });

  it('offers nothing while there is no search to export', () => {
    render(<ExportMenu request={request} disabled rows={undefined} averageRaw={0} listing />);
    expect(screen.getByRole('button', { name: 'Export' })).toBeDisabled();
  });
});
