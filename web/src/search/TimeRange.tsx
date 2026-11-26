import { useEffect, useId, useRef, useState } from 'react';
import { count } from '../app/format';
import { Button, Field } from '../ui/controls';
import { Icon } from '../ui/Icon';
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
  /** The server refused the range or the zone. */
  refusal: Refusal | undefined;
  onRange: (from: string | undefined, to: string | undefined) => void;
  onZone: (tz: string | undefined) => void;
}

function presetOf(from: string | undefined, to: string | undefined): string {
  if (from === undefined && to === undefined) {
    return 'case';
  }
  return (to === undefined ? PRESETS.find((p) => p.from === from)?.from : undefined) ?? 'custom';
}

/** What the range button says: the range as the address holds it, and the zone times are shown in. */
function rangeName(from: string | undefined, to: string | undefined): string {
  const preset = presetOf(from, to);
  if (preset === 'case') {
    return 'Whole case';
  }
  if (preset !== 'custom') {
    return PRESETS.find((p) => p.from === preset)?.label ?? preset;
  }
  return `${plain(from ?? 'start of the case', 40)} to ${plain(to ?? 'end of the case', 40)}`;
}

/**
 * The time a search covers and the zone its times are shown in, as one button that says both. It opens a panel
 * with the presets, From, To and the zone. "Whole case" sends no range, which is the server's default: the
 * recorded span of the case. The panel opens by itself when the server refused the range, to show where.
 */
export function TimeRange({ from, to, tz, refusal, onRange, onZone }: Props) {
  const [draftFrom, setDraftFrom] = useState(from ?? '');
  const [draftTo, setDraftTo] = useState(to ?? '');
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const panel = useId();
  useEffect(() => {
    setDraftFrom(from ?? '');
    setDraftTo(to ?? '');
  }, [from, to]);
  useEffect(() => {
    if (refusal !== undefined) {
      setOpen(true);
    }
  }, [refusal]);
  // A press anywhere else closes the panel, as a menu would; it is not modal and holds nothing unsent but drafts.
  useEffect(() => {
    if (!open) {
      return;
    }
    const away = (event: PointerEvent) => {
      if (event.target instanceof Node && wrap.current?.contains(event.target) !== true) {
        setOpen(false);
      }
    };
    document.addEventListener('pointerdown', away);
    return () => {
      document.removeEventListener('pointerdown', away);
    };
  }, [open]);

  const zone = tz ?? 'UTC';
  const local = browserZone();
  const preset = presetOf(from, to);
  // The server's message quotes the value it could not read; that is how the field is found.
  const said = refusal === undefined ? undefined : plain(refusal.message, 600);
  const fromBad = said !== undefined && from !== undefined && said.includes(`'${from}'`);
  const toBad = said !== undefined && to !== undefined && said.includes(`'${to}'`);
  const close = () => {
    setOpen(false);
  };
  const label = `${rangeName(from, to)} · ${plain(zone, 60)}`;

  return (
    // Escape closes the panel and is not also taken by the page, where it would close the open event.
    // eslint-disable-next-line jsx-a11y/no-static-element-interactions -- the keys come from the controls inside; the wrapper only stops Escape there
    <div
      ref={wrap}
      className="range"
      onKeyDown={(event) => {
        if (event.key === 'Escape' && open) {
          event.preventDefault();
          event.stopPropagation();
          setOpen(false);
          button.current?.focus();
        }
      }}
    >
      <button
        ref={button}
        type="button"
        className={said === undefined ? 'btn range-button' : 'btn range-button range-button-bad'}
        aria-expanded={open}
        aria-controls={open ? panel : undefined}
        aria-label={`Time range: ${label}`}
        title={label}
        onClick={() => {
          setOpen(!open);
        }}
      >
        <span className="ellipsis">{label}</span>
        <Icon name={open ? 'up' : 'down'} />
      </button>
      {open ? (
        <div id={panel} className="range-panel" role="group" aria-label="Time range and zone">
          {said !== undefined && !fromBad && !toBad && from === undefined && to === undefined ? (
            <p className="field-error" role="alert">
              {said}
            </p>
          ) : null}
          <form
            className="range-form"
            onSubmit={(event) => {
              event.preventDefault();
              close();
              onRange(draftFrom.trim() === '' ? undefined : draftFrom.trim(), draftTo.trim() === '' ? undefined : draftTo.trim());
            }}
          >
            <div className="field">
              <label htmlFor={`${panel}-preset`}>Time range</label>
              <select
                id={`${panel}-preset`}
                className="input"
                value={preset}
                onChange={(event) => {
                  const value = event.target.value;
                  if (value === 'case') {
                    close();
                    onRange(undefined, undefined);
                  } else if (value !== 'custom') {
                    close();
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
          </form>
          <div className="field">
            <span id={`${panel}-zone`} className="field-label">
              Times shown in
            </span>
            <div className="seg" role="group" aria-labelledby={`${panel}-zone`}>
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
        </div>
      ) : null}
    </div>
  );
}

interface LineProps {
  from: string | undefined;
  to: string | undefined;
  zone: string;
  /** What the server resolved the range to. Absent until it has answered. */
  meta: Meta | undefined;
  /** The span of the case and the events outside it: from the search once it answers, from the case before. */
  span: Span | undefined;
  onRange: (from: string | undefined, to: string | undefined) => void;
}

/**
 * The range the server searched, written out, and the events the case's span leaves out, each count a way to
 * exactly those events. It stands in the timeline's caption, where it stays in view.
 */
export function RangeLine({ from, to, zone, meta, span, onRange }: LineProps) {
  const future = span?.to == null ? undefined : futureRange(span.to);
  return (
    <span className="range-said sub">
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
    </span>
  );
}
