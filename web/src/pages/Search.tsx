import { useQuery } from '@tanstack/react-query';
import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { ApiError, call } from '../api/http';
import { count, number, roleName, stateName } from '../app/format';
import { Banner, Button, EmptyState, LoadingBlock, Tag } from '../ui/controls';
import { Detail, type FieldInfo } from '../search/Detail';
import { EventTable, indexOf, keyOf } from '../search/EventTable';
import { plain } from '../search/inert';
import { QueryBox } from '../search/QueryBox';
import { BUSY_TRIES, SearchRun, type Refusal, type RunState } from '../search/run';
import { StatsTable } from '../search/StatsTable';
import { addTerm, term } from '../search/terms';
import { knownZone } from '../search/time';
import { TimeRange } from '../search/TimeRange';
import { format, MAX_COLUMNS, parse, requestOf, runKey, type SearchUrl } from '../search/urlState';
import { NoSuchCase } from './Standing';
import './pages.css';
import '../search/search.css';

const Timeline = lazy(() => import('../search/Timeline'));

const NOTHING = () => () => undefined;

function useRunState(run: SearchRun | undefined): RunState | undefined {
  return useSyncExternalStore(run?.subscribe ?? NOTHING, () => run?.snapshot());
}

/** A run that drew something: what stays on the page while the next one loads. */
function drew(state: RunState | undefined): boolean {
  return state !== undefined && (state.histogram !== undefined || state.paged || state.table !== undefined);
}

function describe(request: RunState['request']): string {
  const range = request.from === undefined && request.to === undefined ? 'the whole case' : `${request.from ?? 'the start of the case'} to ${request.to ?? 'the end of the case'}`;
  return `${request.q === '' ? 'every event' : plain(request.q, 200)} · ${plain(range, 200)}`;
}

/** What a refusal or a failure is called, by the code the server gave it. */
function failureTitle(refusal: Refusal | undefined, phase: RunState['phase']): string {
  if (phase === 'cut') {
    return 'The connection was lost before the search finished';
  }
  switch (refusal?.code) {
    case 'query_timeout':
      return 'The search passed its time limit';
    case 'busy':
      return phase === 'refused' ? 'The server stayed busy' : 'The event store was busy';
    case 'offline':
      return 'The server did not answer';
    case 'bad_frame':
      return 'The answer of the server could not be read';
    default:
      return 'The search failed';
  }
}

function failureText(refusal: Refusal | undefined, phase: RunState['phase']): string {
  if (phase === 'cut') {
    return 'What arrived is shown and is not the whole answer.';
  }
  const said = refusal === undefined ? '' : `${plain(refusal.message, 600)}${refusal.detail === undefined ? '' : `. ${plain(refusal.detail, 600)}`}`;
  if (refusal?.code === 'query_timeout') {
    return `${said}. Narrow the time range or the query.`;
  }
  if (refusal?.code === 'busy' && phase === 'refused') {
    return `It was asked ${BUSY_TRIES + 1} times. ${said}.`;
  }
  return said;
}

/** Refusals that belong to the text of the query, and are shown at the query. */
const OF_THE_QUERY = new Set(['query_syntax', 'query_rejected']);

function Region({ label, stale, incomplete, children }: { label: string; stale: boolean; incomplete: boolean; children: ReactNode }) {
  return (
    <section className={stale ? 'section region region-stale' : 'section region'} aria-label={label} aria-busy={stale}>
      {incomplete ? (
        <div className="region-mark">
          <Tag>from an incomplete run</Tag>
        </div>
      ) : null}
      {children}
    </section>
  );
}

function RegionFailure({ what, state, onAgain }: { what: string; state: RunState; onAgain: () => void }) {
  return (
    <div className="section-pad">
      <Banner
        kind="error"
        title={what}
        action={
          <Button small icon="retry" onClick={onAgain}>
            Run again
          </Button>
        }
      >
        {failureTitle(state.refusal, state.phase)}. {failureText(state.refusal, state.phase)}
        {state.refusal?.code === undefined || state.phase === 'cut' ? '' : ` (${state.refusal.code})`}
      </Banner>
    </div>
  );
}

function typing(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false;
  }
  return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) || target.classList.contains('query-editor') || target.getAttribute('role') === 'application';
}

/**
 * The search page of a case. Its address is the search; the page runs what the address says, draws each part
 * of the answer as it arrives, and says of every part that did not arrive why not.
 */
export function Search() {
  const { caseId: text = '' } = useParams();
  const valid = /^[0-9]+$/.test(text);
  const caseId = Number(text);
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const url = useMemo(() => parse(params), [params]);
  const key = runKey(caseId, url);
  const [again, setAgain] = useState(0);
  const [draft, setDraft] = useState(url.q);
  const [run, setRun] = useState<SearchRun | undefined>(undefined);
  const [before, setBefore] = useState<RunState | undefined>(undefined);
  const state = useRunState(run);
  const focusQuery = useRef<(() => void) | null>(null);

  const dashboard = useQuery({ queryKey: ['dashboard', caseId], queryFn: ({ signal }) => call('GetCaseDashboard', { params: { caseId }, signal }), enabled: valid });
  const catalog = useQuery({ queryKey: ['fields', caseId], queryFn: ({ signal }) => call('GetCaseFields', { params: { caseId }, signal }), enabled: valid && dashboard.isSuccess, staleTime: 60_000 });
  const fields = useMemo(() => (catalog.data === undefined ? undefined : new Map<string, FieldInfo>(catalog.data.fields.map((f) => [f.name, { type: f.type, sensitive: f.sensitive }]))), [catalog.data]);

  const caseState = dashboard.data?.case.state;
  const searchable = caseState === 'active' || caseState === 'closed';
  const zoneKnown = url.tz === undefined || knownZone(url.tz);
  const zone = url.tz !== undefined && zoneKnown ? url.tz : 'UTC';

  useEffect(() => {
    setDraft(url.q);
  }, [url.q]);

  // What is on the page when a run ends or is replaced stays there until the next one has something to show.
  const lastDrawn = useRef<RunState | undefined>(undefined);
  useEffect(() => {
    if (state !== undefined && drew(state)) {
      lastDrawn.current = state;
    }
  }, [state]);

  // The key names the request: the columns and the open event change the address and ask nothing.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const request = useMemo(() => requestOf(caseId, url), [key]);
  useEffect(() => {
    if (!valid || !searchable) {
      return;
    }
    setBefore(lastDrawn.current);
    const next = new SearchRun(request);
    setRun(next);
    next.start();
    return () => {
      next.cancel();
    };
  }, [valid, searchable, request, again]);

  const go = useCallback(
    (change: Partial<SearchUrl>, replace = false) => {
      const current = parse(new URLSearchParams(window.location.search));
      void navigate({ search: format({ ...current, ...change }) }, { replace });
    },
    [navigate],
  );

  const submit = useCallback(
    (q: string) => {
      const current = parse(new URLSearchParams(window.location.search));
      if (q === current.q) {
        // The same search asked again: a new run, no new entry in the history.
        setAgain((n) => n + 1);
      } else {
        go({ q, sel: undefined });
      }
    },
    [go],
  );

  const live = state !== undefined && (state.phase === 'waiting' || state.phase === 'busy' || state.phase === 'streaming');
  const rows = state?.rows;
  const at = useMemo(() => indexOf(state?.columns ?? []), [state?.columns]);
  const selectedKey = url.sel === undefined ? undefined : `${url.sel.file}:${url.sel.offset}`;
  const selectedRow = useMemo(() => (selectedKey === undefined || rows === undefined ? undefined : rows.find((row) => keyOf(row, at) === selectedKey)), [selectedKey, rows, at]);

  const open = useCallback(
    (rowKey: string | undefined) => {
      const m = rowKey === undefined ? null : /^(\d+):(\d+)$/.exec(rowKey);
      go({ sel: m === null ? undefined : { file: Number(m[1]), offset: Number(m[2]) } }, true);
    },
    [go],
  );
  const more = useCallback(() => {
    run?.more();
  }, [run]);

  // Keys of the page: "/" to the query, j and k through the events, Escape closes the open event.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.metaKey || event.altKey || typing(event.target)) {
        return;
      }
      if (event.key === '/') {
        event.preventDefault();
        focusQuery.current?.();
      } else if ((event.key === 'j' || event.key === 'k') && rows !== undefined && rows.length > 0) {
        event.preventDefault();
        const now = selectedKey === undefined ? -1 : rows.findIndex((row) => keyOf(row, at) === selectedKey);
        const next = rows[Math.min(rows.length - 1, Math.max(0, now + (event.key === 'j' ? 1 : now < 0 ? 1 : -1)))];
        if (next !== undefined) {
          open(keyOf(next, at));
        }
      } else if (event.key === 'Escape' && selectedKey !== undefined) {
        open(undefined);
      }
    };
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('keydown', onKey);
    };
  }, [rows, at, selectedKey, open]);

  if (!valid || (dashboard.error instanceof ApiError && dashboard.error.status === 404) || state?.refusal?.status === 404) {
    return <NoSuchCase />;
  }
  if (dashboard.isPending) {
    return (
      <main className="page">
        <div className="page-pad">
          <section className="section">
            <LoadingBlock title="Loading the case" />
          </section>
        </div>
      </main>
    );
  }
  if (dashboard.data === undefined) {
    return (
      <main className="page">
        <div className="page-pad">
          <Banner
            kind="error"
            title="The case could not be loaded"
            action={
              <Button small icon="retry" onClick={() => void dashboard.refetch()}>
                Try again
              </Button>
            }
          >
            {dashboard.error instanceof ApiError && dashboard.error.offline ? 'The server did not answer.' : 'The server answered with an error.'}
          </Banner>
        </div>
      </main>
    );
  }

  const c = dashboard.data.case;
  const head = (
    <div className="page-head">
      <div>
        <h1>{c.name}</h1>
        <span className="sub">
          <span className="mono">case {c.id}</span> · search
        </span>
      </div>
      <Tag accent={c.state === 'active'}>{stateName(c.state)}</Tag>
    </div>
  );

  if (!searchable) {
    return (
      <main className="page">
        <div className="page-pad">
          {head}
          <Banner kind="notice" title={`This case is ${stateName(c.state).toLowerCase()}`}>
            Only an active or a closed case can be searched.
          </Banner>
        </div>
      </main>
    );
  }

  const runAgain = () => {
    setAgain((n) => n + 1);
  };
  const refused = state?.phase === 'refused' ? state.refusal : undefined;
  const ofQuery = refused !== undefined && OF_THE_QUERY.has(refused.code ?? '');
  const ofRange = refused?.code === 'bad_request';
  const conflict = refused?.status === 409;
  const ended = state !== undefined && (state.phase === 'failed' || state.phase === 'cut');
  // While the new run has nothing for a region, the run before fills it, dimmed.
  const fallback = state === undefined || live || state.phase === 'refused' || state.phase === 'cancelled' ? before : undefined;
  const kind = state?.meta?.result ?? (state?.table !== undefined ? 'stats' : undefined) ?? fallback?.meta?.result ?? 'events';
  const histogram = state?.histogram ?? fallback?.histogram;
  const histogramOf = state?.histogram !== undefined ? state : fallback;
  const listing = state?.paged === true ? state : fallback?.paged === true ? { ...fallback, next: null, capped: false, more: { kind: 'idle' as const } } : undefined;
  const table = state?.table ?? fallback?.table;
  // A part is "from before" for as long as the run on the page has not sent its own; each part by itself.
  const oldTimeline = fallback !== undefined && state?.histogram === undefined && fallback.histogram !== undefined;
  const oldEvents = fallback !== undefined && state?.paged !== true && fallback.paged;
  const oldTable = fallback !== undefined && state?.table === undefined && fallback.table !== undefined;
  const showsOld = state !== undefined && state.phase !== 'cancelled' && (kind === 'events' ? oldTimeline || oldEvents : oldTable);
  const span = state?.meta?.span ?? dashboard.data.span;

  return (
    <main className="page">
      <div className="page-pad search">
        {head}

        <section className="section section-pad search-controls" aria-label="Search">
          <QueryBox
            draft={draft}
            ran={state?.request.q ?? url.q}
            warnings={state?.meta?.warnings ?? []}
            refusal={ofQuery ? refused : undefined}
            running={live}
            onDraft={setDraft}
            onRun={() => {
              submit(draft);
            }}
            onCancel={() => {
              run?.cancel();
            }}
            focusRef={focusQuery}
          />
          <TimeRange
            from={url.from}
            to={url.to}
            tz={url.tz}
            meta={state?.meta}
            span={span}
            refusal={ofRange ? refused : undefined}
            onRange={(from, to) => {
              if (from === url.from && to === url.to) {
                runAgain();
              } else {
                go({ from, to, sel: undefined });
              }
            }}
            onZone={(tz) => {
              go({ tz, sel: undefined });
            }}
          />
          {zoneKnown ? null : (
            <p className="field-error" role="alert">
              This browser does not know the zone {plain(url.tz ?? '', 80)}. Times are shown in UTC.
            </p>
          )}
        </section>

        {state?.phase === 'busy' && state.wait !== undefined ? (
          <Banner
            kind="notice"
            title={`The server is busy, retrying in ${state.wait.seconds} s`}
            action={
              <Button small onClick={() => run?.cancel()}>
                Cancel
              </Button>
            }
          >
            Too many searches are running. This one is sent again by itself (try {state.wait.attempt} of {BUSY_TRIES}).
          </Banner>
        ) : null}
        {conflict ? (
          <Banner kind="notice" title="This case cannot be searched now">
            {plain(refused.message, 600)}
          </Banner>
        ) : refused !== undefined && !ofQuery && !ofRange ? (
          <Banner
            kind="error"
            title={failureTitle(refused, 'refused')}
            action={
              <Button small icon="retry" onClick={runAgain}>
                Try again
              </Button>
            }
          >
            {failureText(refused, 'refused')}
          </Banner>
        ) : null}
        {showsOld && fallback !== undefined ? (
          <p className="sub stale-line" role="status">
            {live ? 'Searching. Below, dimmed: the results of ' : 'Below: the results of the search before, '}
            <span className="mono">{describe(fallback.request)}</span>
          </p>
        ) : null}

        {kind === 'events' ? (
          <>
            <Region label="Timeline" stale={live && oldTimeline} incomplete={ended && state.histogram !== undefined}>
              {histogram !== undefined && histogramOf?.meta !== undefined ? (
                <Suspense fallback={<div className="timeline-skeleton" aria-hidden="true" />}>
                  <Timeline
                    histogram={histogram}
                    from={histogramOf.meta.from}
                    to={histogramOf.meta.to}
                    zone={zone}
                    onSelect={(from, to) => {
                      go({ from, to, sel: undefined });
                    }}
                  />
                </Suspense>
              ) : ended ? (
                <RegionFailure what="The timeline could not be drawn" state={state} onAgain={runAgain} />
              ) : live ? (
                <div className="timeline-skeleton" role="status" aria-label="Loading the timeline" />
              ) : null}
            </Region>

            <div className={selectedRow === undefined && selectedKey === undefined ? 'results' : 'results results-split'}>
              <Region label="Events" stale={live && oldEvents} incomplete={ended && state.paged}>
                {listing !== undefined ? (
                  listing.rows.length === 0 ? (
                    <EmptyState title="No events match">
                      <p className="sub">
                        Searched {describe(listing.request)}. The case holds {count(dashboard.data.files.total, 'file', 'files')}.
                      </p>
                    </EmptyState>
                  ) : (
                    <EventTable
                      run={listing}
                      zone={zone}
                      extra={url.cols}
                      selected={listing === state ? selectedKey : undefined}
                      total={listing === state ? state.counts?.total : fallback?.counts?.total}
                      onOpen={open}
                      onMore={more}
                      onRemoveColumn={(name) => {
                        go({ cols: url.cols.filter((col) => col !== name) }, true);
                      }}
                    />
                  )
                ) : ended ? (
                  <RegionFailure what="The events could not be listed" state={state} onAgain={runAgain} />
                ) : live ? (
                  <LoadingBlock title={state.phase === 'busy' ? 'Waiting for the server' : 'Searching'} />
                ) : null}
                {ended && state.paged && state.counts === undefined ? <p className="sub section-pad">The events could not be counted: the run did not finish.</p> : null}
              </Region>

              {selectedKey === undefined ? null : selectedRow !== undefined && state !== undefined ? (
                <Detail
                  key={selectedKey}
                  caseId={caseId}
                  columns={state.columns}
                  row={selectedRow}
                  zone={zone}
                  fields={fields}
                  shown={url.cols}
                  onFilter={(field, value, exclude) => {
                    const q = addTerm(state.request.q, term(field, value, exclude));
                    setDraft(q);
                    go({ q, sel: undefined });
                  }}
                  onColumn={(field) => {
                    go({ cols: [...url.cols, field].slice(0, MAX_COLUMNS) }, true);
                  }}
                  onClose={() => {
                    open(undefined);
                  }}
                />
              ) : state?.paged === true ? (
                <aside className="detail" aria-label="Event">
                  <Banner
                    kind="notice"
                    title="The selected event is not on the loaded pages"
                    action={
                      <Button small onClick={() => { open(undefined); }}>
                        Close
                      </Button>
                    }
                  >
                    The address names an event this list has not reached, or one the search no longer matches.
                  </Banner>
                </aside>
              ) : null}
            </div>
          </>
        ) : (
          <Region label="Result" stale={live && oldTable} incomplete={false}>
            {table !== undefined ? (
              <StatsTable table={table} />
            ) : ended ? (
              <RegionFailure what="The result could not be computed" state={state} onAgain={runAgain} />
            ) : live ? (
              <LoadingBlock title={kind === 'timechart' ? 'Computing the timechart' : 'Computing the statistics'} />
            ) : null}
          </Region>
        )}

        <p className="sub search-foot">
          {state?.phase === 'done' && state.durationMs !== undefined ? `Finished in ${number(state.durationMs)} ms · ` : ''}
          {state?.phase === 'cancelled' ? 'Cancelled · ' : ''}
          {count(dashboard.data.files.total, 'file', 'files')} · {count(dashboard.data.hosts.length, 'host', 'hosts')} · your role: {roleName(c.my_role)}
        </p>
      </div>
    </main>
  );
}
