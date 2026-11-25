import * as Menu from '@radix-ui/react-dropdown-menu';
import { useState, useSyncExternalStore } from 'react';
import { bytes, number } from '../app/format';
import { Banner, Button } from '../ui/controls';
import { Icon } from '../ui/Icon';
import { estimateBytes, ExportJob, exportName, exportUrl, mayHold, writesAsItArrives, type ExportFormat, type ExportState, type ExportWhat } from './export';
import { plain } from './inert';
import type { SearchRequest } from './run';
import './search.css';

const NOTHING = () => () => undefined;

const FORMATS: ReadonlyArray<{ key: ExportFormat; label: string }> = [
  { key: 'csv', label: 'CSV' },
  { key: 'json', label: 'JSON lines' },
];

function said(state: ExportState): string {
  const refusal = state.refusal;
  if (refusal === undefined) {
    return '';
  }
  if (refusal.status === 429) {
    return 'An export of yours is already running. One export runs at a time for a user.';
  }
  const words = `${plain(refusal.message, 400)}${refusal.detail === undefined ? '' : `. ${plain(refusal.detail, 400)}`}`;
  return refusal.code === 'query_timeout' ? `${words}. Narrow the time range or the query.` : words;
}

interface Props {
  request: SearchRequest;
  disabled: boolean;
  /** How many events the search matches, once counted, and how long their lines are on average: for the size of the file. */
  rows: number | undefined;
  averageRaw: number;
  /** False for an aggregation: its export is its table, with no files to list and no columns to choose. */
  listing: boolean;
}

/**
 * The search of the address as a file. Not the draft in the box: what is exported is what the page shows.
 * An export of events is evidence, so by default every row carries the file and offset it came from and its
 * raw line, and the files themselves are listed, with their hashes, by an export of their own. Where the
 * browser can write a file the reader chose, or the file is small enough to hold, the page follows the
 * download; otherwise the browser downloads by itself and the page says why it has no progress to show.
 */
export function ExportMenu({ request, disabled, rows, averageRaw, listing }: Props) {
  const [guard, setGuard] = useState(false);
  const [brief, setBrief] = useState(false);
  const [job, setJob] = useState<ExportJob | undefined>(undefined);
  const state = useSyncExternalStore(job?.subscribe ?? NOTHING, () => job?.snapshot());
  const live = state !== undefined && (state.phase === 'choosing' || state.phase === 'asking' || state.phase === 'receiving');
  const what: ExportWhat = listing && brief ? 'brief' : 'evidence';
  const writes = writesAsItArrives();

  const start = (format: ExportFormat, of: ExportWhat) => {
    const next = new ExportJob(request, format, guard, of);
    setJob(next);
    void next.start();
  };
  // A file too large to hold, in a browser that cannot write one: the browser's own download.
  const followed = (format: ExportFormat) => writes || mayHold(estimateBytes(rows, averageRaw, format, what));
  const anyLink = FORMATS.some((format) => !followed(format.key));

  return (
    <div className="export">
      <div className="export-row">
        <Menu.Root modal={false}>
          <Menu.Trigger className="btn" disabled={disabled || live}>
            <Icon name="download" />
            Export
            <Icon name="down" />
          </Menu.Trigger>
          <Menu.Portal>
            <Menu.Content className="menu" align="start" sideOffset={4}>
              <Menu.Label className="menu-label">The search of the address, as a file</Menu.Label>
              {FORMATS.map((format) => {
                const label = `${format.label}${what === 'brief' ? ', brief' : ''}${format.key === 'csv' && guard ? ', formula cells guarded' : ''}`;
                return followed(format.key) ? (
                  <Menu.Item
                    key={format.key}
                    className="menu-item"
                    onSelect={() => {
                      start(format.key, what);
                    }}
                  >
                    {label}
                  </Menu.Item>
                ) : (
                  <Menu.Item key={format.key} className="menu-item" asChild>
                    <a href={exportUrl(request, format.key, guard, what)} download={exportName(request, format.key, what)}>
                      {label}
                    </a>
                  </Menu.Item>
                );
              })}
              {listing ? (
                <Menu.Item
                  className="menu-item"
                  onSelect={() => {
                    start('csv', 'files');
                  }}
                >
                  The files of this result, with their hashes (CSV)
                </Menu.Item>
              ) : null}
              <Menu.Separator className="menu-line" />
              {listing ? (
                <Menu.CheckboxItem
                  className="menu-item"
                  checked={brief}
                  onCheckedChange={(checked) => {
                    setBrief(checked);
                  }}
                  onSelect={(event) => {
                    event.preventDefault();
                  }}
                >
                  <span className="menu-check">
                    <Menu.ItemIndicator>
                      <Icon name="check" />
                    </Menu.ItemIndicator>
                  </span>
                  Brief columns
                </Menu.CheckboxItem>
              ) : null}
              <Menu.CheckboxItem
                className="menu-item"
                checked={guard}
                onCheckedChange={(checked) => {
                  setGuard(checked);
                }}
                onSelect={(event) => {
                  event.preventDefault();
                }}
              >
                <span className="menu-check">
                  <Menu.ItemIndicator>
                    <Icon name="check" />
                  </Menu.ItemIndicator>
                </span>
                Guard formula cells (CSV)
              </Menu.CheckboxItem>
              {listing ? (
                <Menu.Label className="menu-label menu-note">
                  Every row carries the file and the offset it came from and its raw line, so it can be traced to the evidence. Brief columns leaves those out, unless the query names its columns with table.
                </Menu.Label>
              ) : null}
              <Menu.Label className="menu-label menu-note">
                A guarded cell that begins with = + - @ or a tab gets a leading apostrophe, so a spreadsheet does not run it. The file then differs from the stored values and says so in its first line.
              </Menu.Label>
              {anyLink ? (
                <Menu.Label className="menu-label menu-note">
                  This browser cannot write a file as it arrives, and a result of this size is too much to hold in the page. It downloads the file by itself: the page shows no progress, and a refusal of the server arrives as the content of the file.
                </Menu.Label>
              ) : null}
            </Menu.Content>
          </Menu.Portal>
        </Menu.Root>
        {state?.phase === 'choosing' ? (
          <span className="sub" role="status">
            Choose where to save the file
          </span>
        ) : null}
        {state?.phase === 'asking' ? (
          <span className="sub" role="status">
            Exporting to <span className="mono">{plain(state.file ?? '', 80)}</span>: the server is counting the rows
          </span>
        ) : null}
        {state?.phase === 'receiving' ? (
          <span className="sub" role="status">
            Exporting to <span className="mono">{plain(state.file ?? '', 80)}</span>: {bytes(state.bytes)} received{state.rows === undefined ? '' : ` of a result of ${number(state.rows)} rows`}
          </span>
        ) : null}
        {live ? (
          <Button small onClick={() => job?.cancel()}>
            Cancel the export
          </Button>
        ) : null}
        {state?.phase === 'done' ? (
          <span className="sub" role="status">
            Exported <span className="mono">{plain(state.file ?? '', 80)}</span>, {bytes(state.bytes)}
            {state.rows === undefined || state.cut ? '' : `, ${number(state.rows)} rows`}
          </span>
        ) : null}
        {state?.phase === 'cancelled' && state.file !== undefined ? (
          <span className="sub" role="status">
            The export was cancelled. No file was kept.
          </span>
        ) : null}
      </div>
      {state?.phase === 'done' && state.cut ? (
        <Banner kind="notice" title="The file is cut at the server's row limit">
          The result has {state.rows === undefined ? 'more' : number(state.rows)} rows; the file holds the first of them, up to the limit. Narrow the search to export the rest.
        </Banner>
      ) : null}
      {state?.phase === 'refused' ? (
        <Banner kind={state.refusal?.status === 429 ? 'notice' : 'error'} title={state.refusal?.status === 429 ? 'The export did not start' : 'The export was refused'}>
          {said(state)}
        </Banner>
      ) : null}
      {state?.phase === 'failed' ? (
        <Banner kind="error" title="The export broke off before its end">
          {bytes(state.bytes)} had arrived. The incomplete file was not kept. {said(state)}
        </Banner>
      ) : null}
    </div>
  );
}
