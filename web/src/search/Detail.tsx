import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link } from 'react-router';
import { call } from '../api/http';
import { number } from '../app/format';
import { Button, Tag } from '../ui/controls';
import { cellText } from './EventTable';
import { CopyButton, Inert, LongText, plain } from './inert';
import type { Cell } from './stream';
import { cannotFilter } from './terms';
import { instant } from './time';
import { MAX_COLUMNS } from './urlState';
import './search.css';

/** How many fields of an event are drawn at first, and with each further step. */
export const STEP_FIELDS = 200;

/** Columns that are the event's identity or are shown in their own place, not in the list of fields. */
const APART = new Set(['_time', 'raw', 'extras', 'file.id', 'file.offset', 'host.id']);

export interface FieldInfo {
  type: string;
  sensitive: boolean;
}

interface Props {
  caseId: number;
  columns: readonly string[];
  row: readonly Cell[];
  zone: string;
  /** What the catalog says about each field. Absent when the fields could not be read; the event is shown all the same. */
  fields: ReadonlyMap<string, FieldInfo> | undefined;
  /** The extra columns the table shows now. */
  shown: readonly string[];
  onFilter: (field: string, value: string, exclude: boolean) => void;
  onColumn: (field: string) => void;
  onClose: () => void;
}

interface Entry {
  name: string;
  value: string;
  /** A number column holding 0: some fields store that for "no value", and the row does not say which. */
  zero: boolean;
  extra: boolean;
}

/** The fields of the event that hold something: an empty text and the unspecified address are "no value". */
function entries(columns: readonly string[], row: readonly Cell[]): Entry[] {
  const named: Entry[] = [];
  const zeros: Entry[] = [];
  const extras: Entry[] = [];
  columns.forEach((name, i) => {
    const cell = row[i];
    if (name === 'extras') {
      if (typeof cell === 'object' && cell !== null) {
        for (const key of Object.keys(cell).sort()) {
          extras.push({ name: key, value: cell[key] ?? '', zero: false, extra: true });
        }
      }
      return;
    }
    if (APART.has(name) || cell === null || cell === undefined || cell === '' || cell === '::') {
      return;
    }
    (cell === 0 ? zeros : named).push({ name, value: cellText(cell), zero: cell === 0, extra: false });
  });
  return [...named, ...extras, ...zeros];
}

/**
 * One event in full, from the row the table already holds: its time, its raw line as it was stored, and every
 * field with a value. Nothing here is asked of the server but the name and hash of the file it came from.
 */
export function Detail({ caseId, columns, row, zone, fields, shown, onFilter, onColumn, onClose }: Props) {
  const at = (name: string) => row[columns.indexOf(name)];
  const time = cellText(at('_time'));
  const raw = cellText(at('raw'));
  const fileId = Number(at('file.id'));
  const offset = cellText(at('file.offset'));
  const all = useMemo(() => entries(columns, row), [columns, row]);
  const [filter, setFilter] = useState('');
  const [limit, setLimit] = useState(STEP_FIELDS);
  const needle = filter.trim().toLowerCase();
  const matching = needle === '' ? all : all.filter((e) => e.name.toLowerCase().includes(needle));
  const visible = matching.slice(0, limit);

  const file = useQuery({
    queryKey: ['file', caseId, fileId],
    queryFn: ({ signal }) => call('GetCaseFile', { params: { caseId, fileId }, signal }),
    enabled: Number.isFinite(fileId) && fileId > 0,
    staleTime: 300_000,
  });

  return (
    <aside className="detail" aria-label="Event">
      <div className="detail-head">
        <h2>Event</h2>
        <span className="grow" />
        <Button small kind="ghost" icon="close" onClick={onClose} aria-label="Close the event" />
      </div>
      <dl className="facts detail-facts">
        <dt>Time</dt>
        <dd className="mono">
          {instant(time, zone)} {zone}
          {zone === 'UTC' ? null : <span className="sub"> · {instant(time, 'UTC')} UTC</span>}
        </dd>
        <dt>Source</dt>
        <dd>
          {file.isPending && file.fetchStatus !== 'idle' ? (
            <span className="skel skel-w60" aria-hidden="true" />
          ) : file.data === undefined ? (
            <>
              file <span className="mono">{number(fileId)}</span>, offset <span className="mono">{offset}</span>
              <span className="sub"> · the file record could not be read</span>
            </>
          ) : (
            <>
              <Link to={`/cases/${caseId}`} className="mono">
                <Inert value={file.data.file.archive_path ?? file.data.file.original_name} limit={200} />
              </Link>
              , offset <span className="mono">{offset}</span>
              <span className="sub mono" title={file.data.file.sha256}>
                {' '}
                · SHA-256 {file.data.file.sha256.slice(0, 10)}…{file.data.file.sha256.slice(-4)}
              </span>
            </>
          )}
        </dd>
      </dl>

      <div className="detail-sub">
        <h3>Raw line</h3>
        <span className="sub">{number(raw.length)} characters, as stored</span>
        <span className="grow" />
        <CopyButton value={raw} label="Copy the whole raw line" />
      </div>
      <LongText value={raw} label="Raw line" />

      <div className="detail-sub">
        <h3>Fields</h3>
        <span className="sub">
          {number(all.length)} with a value{matching.length === all.length ? '' : `, ${number(matching.length)} match`}
        </span>
        <span className="grow" />
        {all.length > 12 ? (
          <>
            <label className="sr-only" htmlFor="detail-filter">
              Part of a field name
            </label>
            <input
              id="detail-filter"
              className="input detail-filter"
              placeholder="Part of a field name"
              value={filter}
              onChange={(event) => {
                setFilter(event.target.value);
                setLimit(STEP_FIELDS);
              }}
            />
          </>
        ) : null}
      </div>
      <table className="table fields-table">
        <tbody>
          {visible.map((entry) => {
            const info = fields?.get(entry.name);
            const why = cannotFilter(entry.name, entry.value);
            const label = plain(entry.name, 80);
            return (
              <tr key={`${entry.extra ? 'x' : 'c'}:${entry.name}`} className={entry.zero ? 'field-zero' : undefined}>
                <th scope="row" className="mono">
                  <Inert value={entry.name} limit={120} />
                  {info?.sensitive === true ? (
                    <>
                      {' '}
                      <Tag>sensitive</Tag>
                    </>
                  ) : null}
                </th>
                <td className="mono field-value">
                  <Inert value={entry.value} limit={2000} />
                  {entry.value.length > 2000 ? <span className="sub"> ({number(entry.value.length)} characters; copy takes all)</span> : null}
                </td>
                <td className="field-actions">
                  <div className="field-buttons">
                    <Button small kind="ghost" disabled={why !== undefined} title={why ?? 'Narrow the search to this value'} aria-label={`Only ${label}`} onClick={() => { onFilter(entry.name, entry.value, false); }}>
                      Only
                    </Button>
                    <Button small kind="ghost" disabled={why !== undefined} title={why ?? 'Leave this value out of the search'} aria-label={`Exclude ${label}`} onClick={() => { onFilter(entry.name, entry.value, true); }}>
                      Exclude
                    </Button>
                    <Button
                      small
                      kind="ghost"
                      disabled={shown.includes(entry.name) || shown.length >= MAX_COLUMNS}
                      title={shown.includes(entry.name) ? 'Already a column' : shown.length >= MAX_COLUMNS ? `The table shows at most ${MAX_COLUMNS} extra columns` : 'Show this field as a column of the table'}
                      aria-label={`Show ${label} as a column`}
                      onClick={() => { onColumn(entry.name); }}
                    >
                      Column
                    </Button>
                    <CopyButton value={entry.value} label={`Copy the value of ${label}`} />
                  </div>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {all.some((e) => e.zero) && visible.some((e) => e.zero) ? <p className="sub detail-note">Fields in grey hold 0, which some fields store for "no value".</p> : null}
      {matching.length > visible.length ? (
        <p className="sub detail-note">
          Showing {number(visible.length)} of {number(matching.length)} fields.{' '}
          <button
            type="button"
            className="linklike"
            onClick={() => {
              setLimit(limit + STEP_FIELDS);
            }}
          >
            Show {number(Math.min(STEP_FIELDS, matching.length - visible.length))} more
          </button>
          , or type a part of a name above.
        </p>
      ) : null}
    </aside>
  );
}
