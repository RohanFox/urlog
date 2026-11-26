import { useEffect, useMemo, useState, useSyncExternalStore, type ReactNode } from 'react';
import type { FieldsResponse } from '../api/schema.gen';
import { number } from '../app/format';
import { Banner, Button } from '../ui/controls';
import { Inert, plain } from './inert';
import { BUSY_TRIES, SearchRun, type SearchRequest } from './run';
import type { Counts, TopValue } from './stream';
import { cannotFilter, isWord, searchPart } from './terms';
import './search.css';

/** Fields that are the identity of an event, not something to narrow by. */
const APART = new Set(['file.id', 'file.offset', 'host.id']);

/** How many of the fields the catalog does not list are shown before the reader asks for more. */
const OTHER_STEP = 30;

const STATISTICS: Record<string, string> = { none: 'not computed yet', skipped: 'skipped', timed_out: 'timed out', failed: 'failed' };

interface Listed {
  name: string;
  events: number;
  /** About how many distinct values, when the statistics know. */
  distinct: number | null;
  /** The values of the whole case, when the statistics hold them. */
  top: TopValue[] | null;
  sensitive: boolean;
  extra: boolean;
}

interface Props {
  /** The fields of the case. Undefined while they load. */
  catalog: FieldsResponse | undefined;
  catalogError: string | undefined;
  /** The search on the page: its request, its counts once they arrived, and how it stands. */
  request: SearchRequest;
  counts: Counts | undefined;
  live: boolean;
  ended: boolean;
  onFilter: (field: string, value: string, exclude: boolean) => void;
  /** A line at the foot of the list, kept in view: how the run went, and the case's files, hosts and role. */
  foot?: ReactNode;
  /** Folds the list to a narrow strip. Absent: the list cannot be folded. */
  onFold?: () => void;
}

function Values({ field, values, onFilter }: { field: string; values: readonly TopValue[]; onFilter: Props['onFilter'] }) {
  if (values.length === 0) {
    return <p className="sub rail-note">No event of this search has a value here.</p>;
  }
  return (
    <ul className="rail-values">
      {values.map((v) => {
        const why = cannotFilter(field, v.value);
        const label = `${plain(field, 60)} ${plain(v.value, 60)}`;
        return (
          <li key={v.value}>
            <span className="mono rail-value">
              <Inert value={v.value} limit={200} />
            </span>
            <span className="num muted">{number(v.count)}</span>
            <Button small kind="ghost" disabled={why !== undefined} title={why ?? 'Narrow the search to this value'} aria-label={`Only ${label}`} onClick={() => { onFilter(field, v.value, false); }}>
              Only
            </Button>
            <Button small kind="ghost" disabled={why !== undefined} title={why ?? 'Leave this value out of the search'} aria-label={`Exclude ${label}`} onClick={() => { onFilter(field, v.value, true); }}>
              Exclude
            </Button>
          </li>
        );
      })}
    </ul>
  );
}

const NOTHING = () => () => undefined;

/**
 * The values of a field the statistics keep no top values for: a small search of its own, the page's search
 * with a count by the field. It takes one of the user's query slots while it runs, and waits like any search
 * when the server is busy.
 */
function AskedValues({ field, request, onFilter }: { field: string; request: SearchRequest; onFilter: Props['onFilter'] }) {
  const [run, setRun] = useState<SearchRun | undefined>(undefined);
  const [again, setAgain] = useState(0);
  const key = JSON.stringify([request.caseId, searchPart(request.q), request.from ?? null, request.to ?? null, request.tz ?? null, field]);
  useEffect(() => {
    const search = searchPart(request.q);
    const next = new SearchRun({ ...request, q: `${search}${search === '' ? '' : ' '}| stats count by ${field} | sort -count | head 10` });
    setRun(next);
    next.start();
    return () => {
      next.cancel();
    };
    // The key is the request and the field.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, again]);
  const state = useSyncExternalStore(run?.subscribe ?? NOTHING, () => run?.snapshot());

  if (state === undefined || state.phase === 'waiting' || state.phase === 'streaming') {
    return (
      <p className="sub rail-note" role="status">
        Counting the values
      </p>
    );
  }
  if (state.phase === 'busy') {
    return (
      <p className="sub rail-note" role="status">
        The server is busy, retrying in {state.wait?.seconds ?? 1} s (try {state.wait?.attempt ?? 1} of {BUSY_TRIES})
      </p>
    );
  }
  if (state.table === undefined) {
    return (
      <p className="rail-note" role="alert">
        <span className="err-text">The values could not be counted{state.refusal === undefined ? '' : `: ${plain(state.refusal.message, 300)}`}. </span>
        <button
          type="button"
          className="linklike"
          onClick={() => {
            setAgain(again + 1);
          }}
        >
          Try again
        </button>
      </p>
    );
  }
  // The first column is the field, the second its count; a row without a value is no value of the field.
  const values = state.table.rows.filter((row) => (row[0] ?? '') !== '').map((row) => ({ value: row[0] ?? '', count: Number(row[1] ?? 0) }));
  return <Values field={field} values={values} onFilter={onFilter} />;
}

function Field({ field, request, counts, live, ended, onFilter }: { field: Listed } & Pick<Props, 'request' | 'counts' | 'live' | 'ended' | 'onFilter'>) {
  const [open, setOpen] = useState(false);
  const counted = counts?.fields.find((f) => f.name === field.name)?.top;
  // The search narrows nothing: the server says the values of the case's statistics are this search's.
  const ofCase = counts?.source === 'field_stats' && field.top !== null && field.top.length > 0 ? field.top : undefined;
  const values = counted ?? ofCase;
  const askable = isWord(field.name);

  let body = null;
  if (open) {
    if (values !== undefined) {
      body = <Values field={field.name} values={values} onFilter={onFilter} />;
    } else if (counts === undefined && live) {
      body = (
        <p className="sub rail-note" role="status">
          Counting
        </p>
      );
    } else if (counts === undefined && ended) {
      body = <p className="sub rail-note">The values could not be counted: the search did not finish.</p>;
    } else if (counts !== undefined && askable) {
      body = <AskedValues field={field.name} request={request} onFilter={onFilter} />;
    } else if (counts !== undefined) {
      body = <p className="sub rail-note">The name of this field cannot be written in a query, so its values cannot be asked for.</p>;
    }
  }

  return (
    <li className="rail-field">
      <button
        type="button"
        className="rail-name"
        aria-expanded={open}
        onClick={() => {
          setOpen(!open);
        }}
      >
        <span className="mono ellipsis">
          <Inert value={field.name} limit={80} />
        </span>
        <span className="num muted">{number(field.events)}</span>
      </button>
      {open ? (
        <div className="rail-body">
          <p className="sub rail-note">
            In {number(field.events)} events of the case
            {field.distinct === null ? '' : `, about ${number(field.distinct)} values`}
            {field.sensitive ? ' · sensitive' : ''}
          </p>
          {body}
        </div>
      ) : null}
    </li>
  );
}

/**
 * The fields of the case, each with the values this search has most of. Counts are of the case; the values
 * under a field are of the search on the page. What the statistics do not hold is asked for when a field is
 * opened, and what cannot be asked for says why.
 */
export function FieldRail({ catalog, catalogError, request, counts, live, ended, onFilter, foot, onFold }: Props) {
  const [filter, setFilter] = useState('');
  const [others, setOthers] = useState(OTHER_STEP);
  const fields = useMemo<Listed[]>(
    () => (catalog?.fields ?? []).filter((f) => f.events !== null && f.events > 0 && !APART.has(f.name)).map((f) => ({ name: f.name, events: f.events ?? 0, distinct: f.distinct_estimate, top: f.top, sensitive: f.sensitive, extra: false })),
    [catalog],
  );
  const extras = useMemo<Listed[]>(() => (catalog?.extras_keys ?? []).map((k) => ({ name: k.key, events: k.events, distinct: null, top: null, sensitive: false, extra: true })), [catalog]);
  const needle = filter.trim().toLowerCase();
  const match = (f: Listed) => needle === '' || f.name.toLowerCase().includes(needle);
  const shownFields = fields.filter(match);
  const matchingExtras = extras.filter(match);
  const shownExtras = matchingExtras.slice(0, others);
  const statistics = catalog?.statistics;

  return (
    <aside className="rail" aria-label="Fields">
      <div className="rail-head">
        <div className="rail-title">
          <h2>Fields</h2>
          {onFold === undefined ? null : <Button small kind="ghost" icon="left" aria-label="Hide the fields" title="Hide the fields" onClick={onFold} />}
        </div>
        <label className="sr-only" htmlFor="rail-filter">
          Part of a field name
        </label>
        <input
          id="rail-filter"
          className="input"
          placeholder="Part of a field name"
          value={filter}
          onChange={(event) => {
            setFilter(event.target.value);
          }}
        />
      </div>
      {catalogError !== undefined ? (
        <div className="rail-pad">
          <Banner kind="error" title="The fields could not be listed">
            {plain(catalogError, 300)}
          </Banner>
          {counts !== undefined && counts.fields.length > 0 ? <p className="sub rail-note">The values the search counted are shown under their names.</p> : null}
        </div>
      ) : catalog === undefined ? (
        <ul className="rail-list" aria-hidden="true">
          {['skel-w60', 'skel-w80', 'skel-w50', 'skel-w70', 'skel-w40'].map((w) => (
            <li key={w} className="rail-skeleton">
              <span className={`skel ${w}`} />
            </li>
          ))}
        </ul>
      ) : null}
      {statistics !== undefined && (statistics.state !== 'ok' || statistics.pending) ? (
        <p className="rail-note warn-text" role="status">
          {statistics.state === 'ok' ? 'The field statistics are being computed again; counts are of the last run.' : `Field statistics: ${STATISTICS[statistics.state] ?? statistics.state}${statistics.pending ? ', a new run is pending' : ''}.`}
          {statistics.reason === null ? '' : ` ${plain(statistics.reason, 200)}`}
        </p>
      ) : null}
      {catalog !== undefined && fields.length === 0 && extras.length === 0 ? <p className="sub rail-note">No field statistics yet: the fields of this case have not been counted.</p> : null}
      <ul className="rail-list">
        {(catalog === undefined ? (counts?.fields ?? []).map<Listed>((f) => ({ name: f.name, events: f.top.reduce((n, v) => n + v.count, 0), distinct: null, top: null, sensitive: false, extra: false })) : shownFields).map((field) => (
          <Field key={field.name} field={field} request={request} counts={counts} live={live} ended={ended} onFilter={onFilter} />
        ))}
      </ul>
      {shownExtras.length > 0 ? (
        <>
          <h3 className="rail-sub">Fields outside the catalog</h3>
          <ul className="rail-list">
            {shownExtras.map((field) => (
              <Field key={`x:${field.name}`} field={field} request={request} counts={counts} live={live} ended={ended} onFilter={onFilter} />
            ))}
          </ul>
          {matchingExtras.length > shownExtras.length ? (
            <p className="sub rail-note">
              Showing {number(shownExtras.length)} of {number(matchingExtras.length)}.{' '}
              <button
                type="button"
                className="linklike"
                onClick={() => {
                  setOthers(others + OTHER_STEP);
                }}
              >
                Show {number(Math.min(OTHER_STEP, matchingExtras.length - shownExtras.length))} more
              </button>
            </p>
          ) : null}
        </>
      ) : null}
      {catalog !== undefined && needle !== '' && shownFields.length === 0 && matchingExtras.length === 0 ? <p className="sub rail-note">No field has that in its name.</p> : null}
      {foot === undefined ? null : <div className="rail-foot sub">{foot}</div>}
    </aside>
  );
}
