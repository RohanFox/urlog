import { useVirtualizer } from '@tanstack/react-virtual';
import { useCallback, useEffect, useMemo, useRef, type ReactNode } from 'react';
import { number } from '../app/format';
import { Button } from '../ui/controls';
import { CELL_CHARS, Inert, plain } from './inert';
import type { RunState } from './run';
import type { Cell } from './stream';
import { instant } from './time';
import { seriesOf } from './timelineModel';
import './search.css';

/** A cell as the text it is shown by. The map of extra fields is shown in the detail, key by key. */
export function cellText(cell: Cell | undefined): string {
  if (cell === undefined || cell === null) {
    return '';
  }
  return typeof cell === 'object' ? JSON.stringify(cell) : String(cell);
}

/** Where each column of a page stands in its rows. */
export function indexOf(columns: readonly string[]): (name: string) => number {
  const at = new Map(columns.map((name, i) => [name, i]));
  return (name) => at.get(name) ?? -1;
}

/** The key of an event: its file and its offset in the file. No two visible events share one. */
export function keyOf(row: readonly Cell[], at: (name: string) => number): string {
  return `${cellText(row[at('file.id')])}:${cellText(row[at('file.offset')])}`;
}

/**
 * The widest value of a column in the loaded rows, in characters, at least its heading's and at most `cap`:
 * what the column's width is made from. The stylesheet caps the width in pixels as well.
 */
function widest(rows: readonly (readonly Cell[])[], i: number, least: number, cap: number): number {
  let n = least;
  if (i < 0) {
    return n;
  }
  for (const row of rows) {
    const length = cellText(row[i]).length;
    if (length > n) {
      n = Math.min(length, cap);
      if (n === cap) {
        break;
      }
    }
  }
  return n;
}

/** How near the end of the loaded rows the reader must be before the next page is asked for. */
const AHEAD = 60;

interface Props {
  run: RunState;
  zone: string;
  /** Extra columns, catalog names. */
  extra: readonly string[];
  /** The key of the open event. */
  selected: string | undefined;
  /** The exact number of events the search matches, once counted. */
  total: number | undefined;
  onOpen: (key: string) => void;
  onMore: () => void;
  onRemoveColumn: (name: string) => void;
  /** Controls for what the pane shows, at the right of its head: the export. */
  tools?: ReactNode;
}

/** Sets the height of a spacer row. A style attribute would be a second home for sizes; this is a measurement. */
function useHeight(height: number) {
  return useCallback(
    (el: HTMLTableCellElement | null) => {
      el?.style.setProperty('height', `${height}px`);
    },
    [height],
  );
}

function Spacer({ height, columns }: { height: number; columns: number }) {
  const ref = useHeight(height);
  if (height <= 0) {
    return null;
  }
  return (
    <tr aria-hidden="true" className="spacer">
      <td ref={ref} colSpan={columns} />
    </tr>
  );
}

/**
 * The events of a search, newest first unless the query sorts them. Only the rows in view are in the document:
 * ten thousand loaded rows cost what forty do. The next page is asked for as the reader nears the end.
 */
export function EventTable({ run, zone, extra, selected, total, onOpen, onMore, onRemoveColumn, tools }: Props) {
  const scroller = useRef<HTMLDivElement>(null);
  const { rows, columns } = run;
  const at = useMemo(() => indexOf(columns), [columns]);
  const rowHeight = useMemo(() => Number.parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--rowH')) || 30, []);
  const virtual = useVirtualizer({ count: rows.length, getScrollElement: () => scroller.current, estimateSize: () => rowHeight, overscan: 12 });
  const items = virtual.getVirtualItems();
  const last = items[items.length - 1]?.index ?? -1;

  const canMore = run.next !== null && run.more.kind === 'idle';
  useEffect(() => {
    if (canMore && last >= rows.length - AHEAD) {
      onMore();
    }
  }, [canMore, last, rows.length, onMore]);

  // The open event stays in view when the keyboard moves it.
  const selectedIndex = useMemo(() => (selected === undefined ? -1 : rows.findIndex((row) => keyOf(row, at) === selected)), [selected, rows, at]);
  useEffect(() => {
    if (selectedIndex >= 0) {
      virtual.scrollToIndex(selectedIndex, { align: 'auto' });
    }
    // The virtualizer is a new object on every render; the index is what the effect follows.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedIndex]);

  const width = 5 + extra.length;
  const host = useMemo(() => widest(rows, at('host.name'), 4, 20), [rows, at]);
  const program = useMemo(() => widest(rows, at('program'), 7, 14), [rows, at]);
  const level = useMemo(() => widest(rows, at('log.level'), 5, 8), [rows, at]);
  // The counts go to the stylesheet through the element's style object, as the spacer rows' heights do.
  const sized = useCallback(
    (el: HTMLTableElement | null) => {
      el?.style.setProperty('--n-host', String(host));
      el?.style.setProperty('--n-program', String(program));
      el?.style.setProperty('--n-level', String(level));
    },
    [host, program, level],
  );
  const before = items[0]?.start ?? 0;
  const after = virtual.getTotalSize() - (items[items.length - 1]?.end ?? 0);

  let foot: ReactNode = null;
  if (run.more.kind === 'loading') {
    foot = <span role="status">Loading the next events</span>;
  } else if (run.more.kind === 'busy') {
    foot = (
      <span role="status">
        The server is busy, retrying in {run.more.seconds} s (try {run.more.attempt} of 5)
      </span>
    );
  } else if (run.more.kind === 'error') {
    const gone = run.more.refusal.code === 'bad_cursor';
    foot = (
      <>
        <span className="err-text" role="alert">
          {gone ? 'This list can no longer be continued. Run the search again.' : `The next events could not be read: ${plain(run.more.refusal.message, 600)}`}
        </span>
        {gone ? null : (
          <Button small icon="retry" onClick={onMore}>
            Try this page again
          </Button>
        )}
      </>
    );
  } else if (run.capped) {
    foot = (
      <span className="warn-text" role="status">
        Showing the first {number(rows.length)} events. Narrow the search or export.{run.cappedMessage === undefined ? '' : ` The server says: ${plain(run.cappedMessage, 600)}`}
      </span>
    );
  } else if (run.next === null && rows.length > 0) {
    foot = <span>End of the results, {number(rows.length)} events</span>;
  }

  return (
    <div className="events">
      <div className="events-head pane-head">
        <span className="sub" role="status">
          {total === undefined ? `${number(rows.length)} loaded, counting` : `${number(rows.length)} of ${number(total)} events loaded`}
        </span>
        {tools}
      </div>
      <div ref={scroller} className="events-scroll">
        <table ref={sized} className="table events-table">
          <colgroup>
            <col className="c-time" />
            <col className="c-host" />
            <col className="c-program" />
            <col className="c-level" />
            <col />
            {extra.map((name) => (
              <col key={name} className="c-extra" />
            ))}
          </colgroup>
          <thead>
            <tr>
              <th className="col-time">Time</th>
              <th className="col-host">Host</th>
              <th className="col-program">Program</th>
              <th className="col-level">Level</th>
              <th>Message</th>
              {extra.map((name) => (
                <th key={name} className="col-extra">
                  <span className="mono">
                    <Inert value={name} limit={60} />
                  </span>
                  <button
                    type="button"
                    className="btn btn-ghost btn-small"
                    aria-label={`Remove the column ${plain(name, 60)}`}
                    onClick={() => {
                      onRemoveColumn(name);
                    }}
                  >
                    ×
                  </button>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            <Spacer height={before} columns={width} />
            {items.map((item) => {
              const row = rows[item.index];
              if (row === undefined) {
                return null;
              }
              const key = keyOf(row, at);
              const level = cellText(row[at('log.level')]);
              const series = seriesOf(level);
              const time = instant(cellText(row[at('_time')]), zone);
              const classes = ['event', series === 'error' ? 'row-err' : series === 'warning' ? 'row-warn' : '', key === selected ? 'event-open' : ''].filter(Boolean).join(' ');
              return (
                <tr key={key} className={classes} aria-selected={key === selected} data-key={key}>
                  <td className="col-time">
                    <button
                      type="button"
                      className="event-time mono"
                      aria-label={`Open the event of ${time}`}
                      onClick={() => {
                        onOpen(key);
                      }}
                    >
                      {time}
                    </button>
                  </td>
                  <td className="col-host mono">
                    <Inert value={cellText(row[at('host.name')])} limit={80} />
                  </td>
                  <td className="col-program mono">
                    <Inert value={cellText(row[at('program')])} limit={80} />
                  </td>
                  <td className="col-level">
                    <Inert value={level} limit={24} />
                  </td>
                  <td className="col-message mono">
                    <Inert value={cellText(row[at('message')])} limit={CELL_CHARS} />
                  </td>
                  {extra.map((name) => {
                    const i = at(name);
                    const extras = row[at('extras')];
                    const value = i >= 0 ? cellText(row[i]) : typeof extras === 'object' && extras !== null ? (extras[name] ?? '') : '';
                    return (
                      <td key={name} className="col-extra mono">
                        <Inert value={value} limit={120} />
                      </td>
                    );
                  })}
                </tr>
              );
            })}
            <Spacer height={after} columns={width} />
          </tbody>
        </table>
        {foot === null ? null : <div className="events-foot sub">{foot}</div>}
      </div>
    </div>
  );
}
