import { useEffect, useState } from 'react';
import { count } from '../app/format';
import { Button, Field } from '../ui/controls';
import { plain } from './inert';
import type { Refusal } from './run';
import type { Meta, Span } from './stream';
import { browserZone, instant } from './time';
import { futureRange, UNDATED } from './urlState';
import './search.css';

const PRESETS: ReadonlyArray<{ from: string; label: string }> = [
  { from: '-15m', label: 'Last 15 minutes' },
  { from: '-1h', label: 'Last hour' },
  { from: '-24h', label: 'Last 24 hours' },
  { from: '-7d', label: 'Last 7 days' },
  { from: '-30d', label: 'Last 30 days' },
];

interface Props {
  from: string | undefined;
  to: string | undefined;
  /** The display zone of the address. Absent: UTC. */
  tz: string | undefined;
  /** What the server resolved the range to. Absent until it has answered. */
  meta: Meta | undefined;
  /** The span of the case and the events outside it: from the search once it answers, from the case before. */
  span: Span | undefined;
  /** The server refused the range or the zone. */
  refusal: Refusal | undefined;
  onRange: (from: string | undefined, to: string | undefined) => void;
  onZone: (tz: string | undefined) => void;
}

/**
 * The time a search covers. "Whole case" sends no range, which is the server's default: the recorded span of
 * the case. Beside it stand the events that span leaves out, each count a way to exactly those events.
 */
export function TimeRange({ from, to, tz, meta, span, refusal, onRange, onZone }: Props) {
  const [draftFrom, setDraftFrom] = useState(from ?? '');
  const [draftTo, setDraftTo] = useState(to ?? '');
  useEffect(() => {
    setDraftFrom(from ?? '');
    setDraftTo(to ?? '');
  }, [from, to]);

  const zone = tz ?? 'UTC';
  const local = browserZone();
  const preset = from === undefined && to === undefined ? 'case' : (to === undefined ? PRESETS.find((p) => p.from === from)?.from : undefined) ?? 'custom';
  // The server's message quotes the value it could not read; that is how the field is found.
  const said = refusal === undefined ? undefined : plain(refusal.message, 600);
  const fromBad = said !== undefined && from !== undefined && said.includes(`'${from}'`);
  const toBad = said !== undefined && to !== undefined && said.includes(`'${to}'`);
  const future = span?.to == null ? undefined : futureRange(span.to);

  return (
    <div className="range">
      <form
        className="range-form"
        onSubmit={(event) => {
          event.preventDefault();
          onRange(draftFrom.trim() === '' ? undefined : draftFrom.trim(), draftTo.trim() === '' ? undefined : draftTo.trim());
        }}
      >
        <div className="field">
          <label htmlFor="range-preset">Time range</label>
          <select
            id="range-preset"
            className="input"
            value={preset}
            onChange={(event) => {
              const value = event.target.value;
              if (value === 'case') {
                onRange(undefined, undefined);
              } else if (value !== 'custom') {
                onRange(value, undefined);
              }
            }}
          >
            <option value="case">Whole case</option>
            {PRESETS.map((p) => (
              <option key={p.from} value={p.from}>
                {p.label}
              </option>
            ))}
            <option value="custom">Custom</option>
          </select>
        </div>
        <Field
          label="From"
          className="input mono"
          placeholder="start of the case"
          value={draftFrom}
          error={fromBad || (said !== undefined && !toBad && from !== undefined) ? said : undefined}
          onChange={(event) => {
            setDraftFrom(event.target.value);
          }}
        />
        <Field
          label="To"
          className="input mono"
          placeholder="end of the case"
          value={draftTo}
          error={toBad ? said : undefined}
          onChange={(event) => {
            setDraftTo(event.target.value);
          }}
        />
        <Button type="submit" disabled={draftFrom.trim() === (from ?? '') && draftTo.trim() === (to ?? '')}>
          Apply
        </Button>
        <div className="field">
          <span id="zone-label" className="field-label">
            Times shown in
          </span>
          <div className="seg" role="group" aria-labelledby="zone-label">
            <button
              type="button"
              aria-pressed={zone === 'UTC'}
              onClick={() => {
                onZone(undefined);
              }}
            >
              UTC
            </button>
            {local === 'UTC' ? null : (
              <button
                type="button"
                aria-pressed={zone === local}
                onClick={() => {
                  onZone(local);
                }}
              >
                {local}
              </button>
            )}
            {zone !== 'UTC' && zone !== local ? (
              <button type="button" aria-pressed="true">
                {plain(zone, 60)}
              </button>
            ) : null}
          </div>
        </div>
      </form>
      <p className="range-said sub">
        {said !== undefined && !fromBad && !toBad && from === undefined && to === undefined ? <span className="err-text">{said}. </span> : null}
        {meta === undefined ? (
          from === undefined && to === undefined ? (
            'The whole case'
          ) : (
            <>
              Asked: <span className="mono">{plain(from ?? 'start of the case', 80)}</span> to <span className="mono">{plain(to ?? 'end of the case', 80)}</span>
            </>
          )
        ) : (
          <>
            Searched <span className="mono">{instant(meta.from, zone, 's')}</span> to <span className="mono">{instant(meta.to, zone, 's')}</span> {zone}
          </>
        )}
        {span !== undefined && span.undated_events > 0 ? (
          <>
            {' · '}
            <button
              type="button"
              className="linklike warn-text"
              onClick={() => {
                onRange(UNDATED.from, UNDATED.to);
              }}
            >
              {count(span.undated_events, 'event', 'events')} without a usable time
            </button>
          </>
        ) : null}
        {span !== undefined && span.future_events > 0 && future !== undefined ? (
          <>
            {' · '}
            <button
              type="button"
              className="linklike warn-text"
              onClick={() => {
                onRange(future.from, future.to);
              }}
            >
              {count(span.future_events, 'event', 'events')} dated after their file arrived
            </button>
          </>
        ) : null}
      </p>
    </div>
  );
}
