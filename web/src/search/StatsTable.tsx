import { useVirtualizer } from '@tanstack/react-virtual';
import { useCallback, useMemo, useRef } from 'react';
import { number } from '../app/format';
import { Tag } from '../ui/controls';
import { Inert } from './inert';
import type { Table } from './stream';
import './search.css';

function Spacer({ height, columns }: { height: number; columns: number }) {
  const ref = useCallback(
    (el: HTMLTableCellElement | null) => {
      el?.style.setProperty('height', `${height}px`);
    },
    [height],
  );
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
 * The result of an aggregation, as the server ordered it. What the server cut is said above the rows: a table
 * of more rows than it sends, and a cell of values(f) that stands for more values than it lists.
 */
export function StatsTable({ table }: { table: Table }) {
  const scroller = useRef<HTMLDivElement>(null);
  const rowHeight = useMemo(() => Number.parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--rowH')) || 30, []);
  const virtual = useVirtualizer({ count: table.rows.length, getScrollElement: () => scroller.current, estimateSize: () => rowHeight, overscan: 12 });
  const items = virtual.getVirtualItems();
  const capped = useMemo(() => new Set(table.values_capped_rows), [table.values_capped_rows]);
  const width = table.columns.length + (capped.size > 0 ? 1 : 0);
  const before = items[0]?.start ?? 0;
  const after = virtual.getTotalSize() - (items[items.length - 1]?.end ?? 0);

  return (
    <div className="events">
      <div className="events-head sub" role="status">
        {table.truncated ? (
          <span className="warn-text">
            {number(table.rows.length)} of {number(table.total_rows)} rows shown. Export for all of them.
          </span>
        ) : (
          `${number(table.rows.length)} ${table.rows.length === 1 ? 'row' : 'rows'}`
        )}
        {capped.size > 0 ? <span className="warn-text"> · {number(capped.size)} {capped.size === 1 ? 'row has' : 'rows have'} a list of values capped at 100: the marked cell may stand for more.</span> : null}
      </div>
      <div ref={scroller} className="events-scroll">
        <table className="table events-table">
          <thead>
            <tr>
              {table.columns.map((name, i) => (
                <th key={i} className="mono">
                  <Inert value={name} limit={80} />
                </th>
              ))}
              {capped.size > 0 ? <th>Cut</th> : null}
            </tr>
          </thead>
          <tbody>
            <Spacer height={before} columns={width} />
            {items.map((item) => {
              const row = table.rows[item.index];
              if (row === undefined) {
                return null;
              }
              return (
                <tr key={item.index} className={capped.has(item.index) ? 'row-warn' : undefined}>
                  {table.columns.map((_, i) => (
                    <td key={i} className="mono col-message">
                      <Inert value={row[i] ?? ''} limit={400} />
                    </td>
                  ))}
                  {capped.size > 0 ? <td>{capped.has(item.index) ? <Tag>values capped at 100</Tag> : null}</td> : null}
                </tr>
              );
            })}
            <Spacer height={after} columns={width} />
          </tbody>
        </table>
        {table.rows.length === 0 ? <div className="events-foot sub">The aggregation has no rows.</div> : null}
      </div>
    </div>
  );
}
