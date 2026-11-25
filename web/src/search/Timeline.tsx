import { useEffect, useMemo, useRef, useState } from 'react';
import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';
import { count, number } from '../app/format';
import type { Histogram } from './stream';
import { instant } from './time';
import { move, rangeOf, SERIES, SERIES_NAME, slots, stretch, type Selection, type Series, type Slot } from './timelineModel';
import './search.css';

interface Props {
  histogram: Histogram;
  /** The range that was searched, which is the width of the axis whatever the buckets cover. */
  from: string;
  to: string;
  zone: string;
  onSelect: (from: string, to: string) => void;
}

/** The colour of each series, read from the tokens when the chart is built: a canvas does not resolve a variable. */
const TOKEN: Record<Series, string> = { error: '--sev-error', warning: '--sev-warning', info: '--sev-info', none: '--sev-none' };

function token(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function pixels(name: string): number {
  return Number.parseFloat(token(name)) || 0;
}

/** Counts up the series so that each is drawn over the taller one behind it: stacked bars from plain ones. */
function stacked(all: readonly Slot[]): uPlot.AlignedData {
  const x = all.map((s) => s.start / 1000);
  const e = all.map((s) => s.counts.error);
  const w = all.map((s) => s.counts.error + s.counts.warning);
  const i = all.map((s) => s.counts.error + s.counts.warning + s.counts.info);
  const n = all.map((s) => s.total);
  return [x, n, i, w, e];
}

/** The labels of the time axis in the display zone: the date where a tick begins a day or the ticks are days, the time otherwise. */
function tickLabels(ticks: readonly number[], step: number, zone: string): string[] {
  let day = '';
  return ticks.map((seconds) => {
    const text = instant(new Date(seconds * 1000).toISOString(), zone, step < 1 ? 'ms' : step < 60 ? 's' : 'min');
    const date = text.slice(0, 10);
    const first = date !== day;
    day = date;
    if (step >= 86_400) {
      return date;
    }
    return first ? `${date.slice(5)} ${text.slice(11)}` : text.slice(11);
  });
}

const ORDER: readonly Series[] = ['none', 'info', 'warning', 'error'];

/** The theme the document shows, as a value that changes when it does. */
function useThemeKey(): string {
  const [key, setKey] = useState(() => document.documentElement.dataset['theme'] ?? '');
  useEffect(() => {
    const watch = new MutationObserver(() => {
      setKey(document.documentElement.dataset['theme'] ?? '');
    });
    watch.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    return () => {
      watch.disconnect();
    };
  }, []);
  return key;
}

/**
 * Events over time, stacked by severity. A drag selects a stretch of time; so do the arrow keys with the chart
 * in focus (shift extends, Enter applies, Escape drops). The colours are named in the legend and the selection
 * is read out as text, so nothing is said by colour or by position alone.
 */
export default function Timeline({ histogram, from, to, zone, onSelect }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const plot = useRef<uPlot | null>(null);
  const all = useMemo(() => slots(histogram), [histogram]);
  const [selection, setSelection] = useState<Selection | undefined>(undefined);
  const theme = useThemeKey();
  const select = useRef(onSelect);
  useEffect(() => {
    select.current = onSelect;
  }, [onSelect]);

  useEffect(() => {
    const el = host.current;
    if (el === null) {
      return;
    }
    const ink = token('--muted');
    const grid = token('--line');
    const font = `${token('--fs-11')} ${token('--data')}`;
    const axis = { stroke: ink, font, grid: { stroke: grid, width: 1 }, ticks: { stroke: grid, width: 1 } };
    const bars = uPlot.paths.bars?.({ size: [0.9, 64, 1], align: 1 });
    const u = new uPlot(
      {
        width: Math.max(el.clientWidth, 1),
        height: pixels('--h-timeline'),
        tzDate: (seconds) => uPlot.tzDate(new Date(seconds * 1000), zone),
        legend: { show: false },
        cursor: { drag: { x: true, y: false, setScale: false }, points: { show: false }, y: false },
        scales: { x: { time: true, range: () => [new Date(from).getTime() / 1000, new Date(to).getTime() / 1000] }, y: { range: (_u, _min, max) => [0, Math.max(1, max)] } },
        axes: [{ ...axis, space: pixels('--w-tick'), values: (_u, ticks, _axis, _space, step) => tickLabels(ticks, step, zone) }, { ...axis, size: pixels('--w-axis'), values: (_u, ticks) => ticks.map((t) => (Number.isInteger(t) ? number(t) : '')) }],
        series: [
          {},
          ...ORDER.map((name) => {
            const colour = token(TOKEN[name]);
            return { label: SERIES_NAME[name], stroke: colour, fill: colour, width: 0, ...(bars === undefined ? {} : { paths: bars }), points: { show: false } };
          }),
        ],
        hooks: {
          setSelect: [
            (self) => {
              if (self.select.width <= 0) {
                return;
              }
              const range = stretch(all, self.posToVal(self.select.left, 'x') * 1000, self.posToVal(self.select.left + self.select.width, 'x') * 1000);
              self.setSelect({ left: 0, top: 0, width: 0, height: 0 }, false);
              select.current(range.from, range.to);
            },
          ],
        },
      },
      stacked(all),
      el,
    );
    plot.current = u;
    const resize = new ResizeObserver(() => {
      u.setSize({ width: Math.max(el.clientWidth, 1), height: pixels('--h-timeline') });
    });
    resize.observe(el);
    return () => {
      resize.disconnect();
      u.destroy();
      plot.current = null;
    };
  }, [all, from, to, zone, theme]);

  // The selection of the keyboard, drawn with the chart's own selection box.
  useEffect(() => {
    const u = plot.current;
    if (u === null) {
      return;
    }
    const first = selection === undefined ? undefined : all[selection.first];
    const last = selection === undefined ? undefined : all[selection.last];
    if (first === undefined || last === undefined) {
      u.setSelect({ left: 0, top: 0, width: 0, height: 0 }, false);
      return;
    }
    const left = u.valToPos(first.start / 1000, 'x');
    u.setSelect({ left, top: 0, width: Math.max(2, u.valToPos(last.end / 1000, 'x') - left), height: u.bbox.height / uPlot.pxRatio }, false);
  }, [selection, all, theme, from, to, zone]);

  const picked = selection === undefined ? undefined : rangeOf(all, selection);
  const tallest = all.reduce<Slot | undefined>((best, slot) => (best === undefined || slot.total > best.total ? slot : best), undefined);
  const seen = SERIES.filter((name) => all.some((slot) => slot.counts[name] > 0));

  return (
    <div className="timeline">
      {/* The chart is one control for the keyboard: the arrows move a selection across its buckets. */}
      {/* eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions -- a chart that is operated has no native element; the role and the keys are given here */}
      <div
        ref={host}
        className="timeline-plot"
        role="application"
        // eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- the same: the chart takes the focus so the keys reach it
        tabIndex={0}
        aria-label="Timeline of the events. Arrow keys select a stretch of time, shift extends it, Enter searches it, Escape drops it."
        aria-describedby="timeline-text"
        onKeyDown={(event) => {
          if (event.key === 'ArrowLeft' || event.key === 'ArrowRight' || event.key === 'Home' || event.key === 'End') {
            event.preventDefault();
            setSelection(move(selection, event.key, event.shiftKey, all.length));
          } else if (event.key === 'Enter' && picked !== undefined) {
            event.preventDefault();
            setSelection(undefined);
            onSelect(picked.from, picked.to);
          } else if (event.key === 'Escape' && selection !== undefined) {
            event.preventDefault();
            event.stopPropagation();
            setSelection(undefined);
          }
        }}
      />
      <div className="timeline-foot">
        <ul className="legend" aria-label="Severity of the bars">
          {seen.map((name) => (
            <li key={name}>
              <span className={`swatch swatch-${name}`} aria-hidden="true" />
              {SERIES_NAME[name]}
            </li>
          ))}
        </ul>
        <span id="timeline-text" className="sub" role="status">
          {picked !== undefined
            ? `Selected ${instant(picked.from, zone, 's')} to ${instant(picked.to, zone, 's')}, ${count(picked.events, 'event', 'events')}. Enter searches it.`
            : tallest === undefined
              ? 'No events in this range.'
              : `${count(histogram.total, 'event', 'events')} in buckets of ${histogram.span}. Most in the bucket from ${instant(new Date(tallest.start).toISOString(), zone, 's')}: ${number(tallest.total)}.`}
        </span>
      </div>
    </div>
  );
}
