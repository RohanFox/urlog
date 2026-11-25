import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type * as exporting from './export';
import { ExportMenu } from './ExportMenu';
import type { SearchRequest } from './run';

// The other setting of the switch: a browser that cannot write a file it chose downloads the address itself.
vi.mock('./export', async (original) => ({ ...(await original<typeof exporting>()), HOLDS: false }));

const request: SearchRequest = { caseId: 7, q: 'status=404', from: undefined, to: undefined, tz: undefined };

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the export menu where the page neither writes the file nor holds it', () => {
  it('offers each format as a plain download of the address and says why there is no progress', async () => {
    vi.stubGlobal('showSaveFilePicker', undefined);
    render(<ExportMenu request={request} disabled={false} />);
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Export' }));

    const csv = await screen.findByRole('menuitem', { name: 'CSV' });
    expect(csv).toHaveAttribute('href', '/api/v1/cases/7/search/export?format=csv&query=status%3D404');
    expect(csv).toHaveAttribute('download', 'case-7-export.csv');
    expect(screen.getByRole('menuitem', { name: 'JSON lines' })).toHaveAttribute('href', '/api/v1/cases/7/search/export?format=json&query=status%3D404');
    expect(screen.getByText(/This browser cannot write a file as it arrives/)).toHaveTextContent('the page shows no progress, and a refusal of the server arrives as the content of the file');

    // The guard is part of the address.
    await user.click(screen.getByRole('menuitemcheckbox', { name: 'Guard formula cells (CSV)' }));
    expect(screen.getByRole('menuitem', { name: 'CSV, formula cells guarded' })).toHaveAttribute('href', '/api/v1/cases/7/search/export?format=csv&query=status%3D404&formula_guard=true');
  });

  it('follows the download where the browser can write the file, whatever the switch says', async () => {
    vi.stubGlobal('showSaveFilePicker', () => Promise.reject(new Error('closed')));
    render(<ExportMenu request={request} disabled={false} />);
    await userEvent.setup().click(screen.getByRole('button', { name: 'Export' }));
    expect(await screen.findByRole('menuitem', { name: 'CSV' })).not.toHaveAttribute('href');
    expect(screen.queryByText(/This browser cannot write a file as it arrives/)).toBeNull();
  });

  it('offers nothing while there is no search to export', () => {
    render(<ExportMenu request={request} disabled />);
    expect(screen.getByRole('button', { name: 'Export' })).toBeDisabled();
  });
});
