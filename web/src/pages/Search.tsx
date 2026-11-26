import { useQuery } from '@tanstack/react-query';
import { Fragment, lazy, Suspense, useCallback, useEffect, useId, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { ApiError, call } from '../api/http';
import { count, number, roleName, stateName } from '../app/format';
import { Banner, Button, EmptyState, LoadingBlock, Tag } from '../ui/controls';
import { Detail, type FieldInfo } from '../search/Detail';
import { EventTable, indexOf, keyOf } from '../search/EventTable';
import { ExportMenu } from '../search/ExportMenu';
import { FieldRail } from '../search/FieldRail';
import { plain } from '../search/inert';
import { QueryBox } from '../search/QueryBox';
import { BUSY_TRIES, SearchRun, type Refusal, type RunState } from '../search/run';
import { StatsTable } from '../search/StatsTable';
import { addTerm, isWord, term } from '../search/terms';
import { instant, knownZone } from '../search/time';
import { RangeLine, TimeRange } from '../search/TimeRange';
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

function Region({ label, className, stale, incomplete, children }: { label: string; className: string; stale: boolean; incomplete: boolean; children: ReactNode }) {
  return (
    <section className={stale ? `region region-stale ${className}` : `region ${className}`} aria-label={label} aria-busy={stale}>
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

/** Where the folds of the timeline and of the fields are kept: in this browser, not in the address, so a shared address imposes nothing. */
const TIMELINE_FOLD = 'urlog.search.timeline';
const FIELDS_FOLD = 'urlog.search.fields';

function readFold(key: string): boolean {
  try {
    return window.localStorage.getItem(key) === 'folded';
  } catch {
    return false;
  }
}

function useFold(key: string): [boolean, (folded: boolean) => void] {
  const [folded, setFolded] = useState(() => readFold(key));
  const set = useCallback(
    (next: boolean) => {
      setFolded(next);
      try {
        if (next) {
          window.localStorage.setItem(key, 'folded');
        } else {
          window.localStorage.removeItem(key);
        }
      } catch {
        // A browser that keeps nothing still folds for as long as the page is open.
      }
    },
    [key],
  );
  return [folded, set];
}

const KEYS: ReadonlyArray<{ key: string; what: string }> = [
  { key: '/', what: 'to the query' },
  { key: 'j', what: 'open the next event' },
  { key: 'k', what: 'open the event before' },
  { key: 'Enter', what: 'open the event whose time has the focus' },
  { key: 'Esc', what: 'close the open event' },
];

/**
 * The detail's column while no event is open: the keys that move through the events, and what the search on
 * the page is. From 1600 pixels the column is always there, so opening an event changes no width.
 */
function NoEventOpen({ state, url, zone }: { state: RunState | undefined; url: SearchUrl; zone: string }) {
  const heading = useId();
  const q = state?.request.q ?? url.q;
  const meta = state?.meta;
  const total = state?.counts?.total;
  let progress: string;
  if (state === undefined || state.phase === 'waiting' || state.phase === 'streaming') {
    progress = 'running';
  } else if (state.phase === 'busy') {
    progress = 'waiting for the server';
  } else if (state.phase === 'done') {
    progress = state.durationMs === undefined ? 'complete' : `complete, ${number(state.durationMs)} ms`;
  } else if (state.phase === 'cancelled') {
    progress = 'cancelled';
  } else if (state.phase === 'refused') {
    progress = 'refused';
  } else {
    progress = 'incomplete';
  }
  return (
    <aside className="detail detail-empty" aria-labelledby={heading}>
      <h2 id={heading}>No event open</h2>
      <p className="sub">Open an event by its time, or with j.</p>
      <h3 className="detail-empty-sub">Keys</h3>
      <dl className="facts keys">
        {KEYS.map((k) => (
          <Fragment key={k.key}>
            <dt>
              <kbd>{k.key}</kbd>
            </dt>
            <dd>{k.what}</dd>
          </Fragment>
        ))}
      </dl>
      <h3 className="detail-empty-sub">This search</h3>
      <dl className="facts">
        <dt>Query</dt>
        <dd className="mono">{q === '' ? 'every event' : plain(q, 400)}</dd>
        <dt>Range</dt>
        <dd>
          {meta === undefined ? (
            'not answered yet'
          ) : (
            <>
              <span className="mono">{instant(meta.from, zone, 's')}</span> to <span className="mono">{instant(meta.to, zone, 's')}</span> {zone}
            </>
          )}
        </dd>
        <dt>Events</dt>
        <dd>{total === undefined ? (state?.paged === true ? `${number(state.rows.length)} loaded, not counted yet` : 'not counted yet') : `${number(total)} match, ${number(state?.rows.length ?? 0)} loaded`}</dd>
        <dt>Run</dt>
        <dd>{progress}</dd>
      </dl>
    </aside>
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
  const [timelineFolded, setTimelineFolded] = useFold(TIMELINE_FOLD);
  const [fieldsFolded, setFieldsFolded] = useFold(FIELDS_FOLD);
  // From 1024 to 1599 pixels an open event folds the fields to make room for its column, unless the reader
  // opened them again while it is open. Closing the event ends that.
  const [fieldsKept, setFieldsKept] = useState(false);

  const dashboard = useQuery({ queryKey: ['dashboard', caseId], queryFn: ({ signal }) => call('GetCaseDashboard', { params: { caseId }, signal }), enabled: valid });
  const catalog = useQuery({ queryKey: ['fields', caseId], queryFn: ({ signal }) => call('GetCaseFields', { params: { caseId }, signal }), enabled: valid && dashboard.isSuccess, staleTime: 60_000 });
  const fields = useMemo(() => (catalog.data === undefined ? undefined : new Map<string, FieldInfo>(catalog.data.fields.map((f) => [f.name, { type: f.type, sensitive: f.sensitive, zeroIsAbsent: f.zero_is_absent }]))), [catalog.data]);

  // What the query box completes from: the catalog's names and aliases, the other fields that can be written
  // as a word, and the verbs the server names. Without the fields of the case, nothing.
  const words = useMemo(() => {
    const data = catalog.data;
    if (data === undefined) {
      return { fields: [], verbs: [] };
    }
    const names = data.fields.flatMap((f) => [{ name: f.name, detail: f.type }, ...f.aliases.filter(isWord).map((alias) => ({ name: alias, detail: f.name }))]);
    const extras = data.extras_keys.filter((k) => isWord(k.key)).slice(0, 500).map((k) => ({ name: k.key, detail: 'outside the catalog' }));
    return { fields: [...names, ...extras], verbs: data.verbs };
  }, [catalog.data]);
  const filterBy = useCallback((field: string, value: string, exclude: boolean) => {
    const current = parse(new URLSearchParams(window.location.search));
    const q = addTerm(current.q, term(field, value, exclude));
    setDraft(q);
    void navigate({ search: format({ ...current, q, sel: undefined }) });
  }, [navigate]);

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
  // How long a line of this result is, from the first rows the page holds: for the size of an export.
  const averageRaw = useMemo(() => {
    const sample = (rows ?? []).slice(0, 200);
    const i = at('raw');
    if (sample.length === 0 || i < 0) {
      return 0;
    }
    return sample.reduce((n, row) => n + (typeof row[i] === 'string' ? row[i].length : 0), 0) / sample.length;
  }, [rows, at]);
  const selectedKey = url.sel === undefined ? undefined : `${url.sel.file}:${url.sel.offset}`;
  const selectedRow = useMemo(() => (selectedKey === undefined || rows === undefined ? undefined : rows.find((row) => keyOf(row, at) === selectedKey)), [selectedKey, rows, at]);

  const open = useCallback(
    (rowKey: string | undefined) => {
      const m = rowKey === undefined ? null : /^(\d+):(\d+)$/.exec(rowKey);
      go({ sel: m === null ? undefined : { file: Number(m[1]), offset: Number(m[2]) } }, true);
    },
    [go],
  );
  useEffect(() => {
    if (url.sel === undefined) {
      setFieldsKept(false);
    }
  }, [url.sel]);
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

  if (!searchable) {
    return (
      <main className="page">
        <div className="page-pad">
          <h1 className="sr-only">Search</h1>
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
  const applyRange = (from: string | undefined, to: string | undefined) => {
    if (from === url.from && to === url.to) {
      runAgain();
    } else {
      go({ from, to, sel: undefined });
    }
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

  const exportMenu = (
    <ExportMenu
      request={request}
      disabled={state === undefined || state.phase === 'refused' || state.phase === 'waiting' || state.phase === 'busy'}
      rows={state?.counts?.total}
      averageRaw={averageRaw}
      listing={kind === 'events'}
    />
  );
  const rangeLine = <RangeLine from={url.from} to={url.to} zone={zone} meta={state?.meta ?? fallback?.meta} span={span} onRange={applyRange} />;
  const toggle = (
    <Button
      small
      kind="ghost"
      icon={timelineFolded ? 'down' : 'up'}
      aria-expanded={!timelineFolded}
      onClick={() => {
        setTimelineFolded(!timelineFolded);
      }}
    >
      {timelineFolded ? 'Show the timeline' : 'Hide the timeline'}
    </Button>
  );
  const foot = (
    <>
      {state?.phase === 'done' && state.durationMs !== undefined ? `Finished in ${number(state.durationMs)} ms · ` : ''}
      {state?.phase === 'cancelled' ? 'Cancelled · ' : ''}
      {count(dashboard.data.files.total, 'file', 'files')} · {count(dashboard.data.hosts.length, 'host', 'hosts')} · your role: {roleName(c.my_role)}
    </>
  );

  return (
    <main className="page search-page">
      <h1 className="sr-only">Search</h1>
      <div className="search-top">
        <section className="search-controls" aria-label="Search">
          <QueryBox
            draft={draft}
            ran={state?.request.q ?? url.q}
            warnings={state?.meta?.warnings ?? []}
            refusal={ofQuery ? refused : undefined}
            running={live}
            words={words}
            onDraft={setDraft}
            onRun={(text) => {
              submit(text ?? draft);
            }}
            onCancel={() => {
              run?.cancel();
            }}
            focusRef={focusQuery}
            range={
              <TimeRange
                from={url.from}
                to={url.to}
                tz={url.tz}
                refusal={ofRange ? refused : undefined}
                onRange={applyRange}
                onZone={(tz) => {
                  go({ tz, sel: undefined });
                }}
              />
            }
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
          <Region label="Timeline" className="strip" stale={live && oldTimeline} incomplete={ended && state.histogram !== undefined}>
            {histogram !== undefined && histogramOf?.meta !== undefined ? (
              <Suspense
                fallback={
                  <>
                    {timelineFolded ? null : <div className="timeline-skeleton" aria-hidden="true" />}
                    <div className="timeline-foot">
                      {rangeLine}
                      <span className="grow" />
                      {toggle}
                    </div>
                  </>
                }
              >
                <Timeline
                  histogram={histogram}
                  from={histogramOf.meta.from}
                  to={histogramOf.meta.to}
                  zone={zone}
                  collapsed={timelineFolded}
                  range={rangeLine}
                  toggle={toggle}
                  onSelect={(from, to) => {
                    go({ from, to, sel: undefined });
                  }}
                />
              </Suspense>
            ) : (
              <>
                {ended ? (
                  <RegionFailure what="The timeline could not be drawn" state={state} onAgain={runAgain} />
                ) : live && !timelineFolded ? (
                  <div className="timeline-skeleton" role="status" aria-label="Loading the timeline" />
                ) : null}
                <div className="timeline-foot">
                  {rangeLine}
                  <span className="grow" />
                  {toggle}
                </div>
              </>
            )}
          </Region>
        ) : (
          <p className="range-line">{rangeLine}</p>
        )}
      </div>

      {kind === 'events' ? (
        <div className={['panes', fieldsFolded ? 'panes-folded' : '', selectedKey !== undefined && !fieldsKept ? 'panes-for-event' : ''].filter(Boolean).join(' ')}>
          <div className="rail-strip">
            <Button
              kind="ghost"
              icon="right"
              aria-label="Show the fields"
              title="Show the fields"
              onClick={() => {
                setFieldsFolded(false);
                setFieldsKept(selectedKey !== undefined);
              }}
            >
              Fields
            </Button>
          </div>
          <FieldRail
            catalog={catalog.data}
            catalogError={catalog.isError ? (catalog.error instanceof ApiError && !catalog.error.offline ? catalog.error.message : 'The server did not answer.') : undefined}
            request={request}
            counts={state?.counts}
            live={live}
            ended={ended || state?.phase === 'refused'}
            onFilter={filterBy}
            onFold={() => {
              setFieldsFolded(true);
              setFieldsKept(false);
            }}
            foot={foot}
          />
          <Region label="Events" className="pane-events" stale={live && oldEvents} incomplete={ended && state.paged}>
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
                  tools={exportMenu}
                />
              )
            ) : ended ? (
              <RegionFailure what="The events could not be listed" state={state} onAgain={runAgain} />
            ) : live ? (
              <LoadingBlock title={state.phase === 'busy' ? 'Waiting for the server' : 'Searching'} />
            ) : null}
            {ended && state.paged && state.counts === undefined ? <p className="sub section-pad">The events could not be counted: the run did not finish.</p> : null}
          </Region>

          {selectedKey === undefined ? (
            <NoEventOpen state={state} url={url} zone={zone} />
          ) : selectedRow !== undefined && state !== undefined ? (
            <Detail
              key={selectedKey}
              caseId={caseId}
              columns={state.columns}
              row={selectedRow}
              zone={zone}
              fields={fields}
              shown={url.cols}
              onFilter={filterBy}
              onQuery={(q) => {
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
          ) : (
            <NoEventOpen state={state} url={url} zone={zone} />
          )}
        </div>
      ) : (
        <div className="panes panes-one">
          <Region label="Result" className="pane-events" stale={live && oldTable} incomplete={false}>
            {table !== undefined ? (
              <StatsTable table={table} tools={exportMenu} />
            ) : ended ? (
              <RegionFailure what="The result could not be computed" state={state} onAgain={runAgain} />
            ) : live ? (
              <LoadingBlock title={kind === 'timechart' ? 'Computing the timechart' : 'Computing the statistics'} />
            ) : null}
          </Region>
          <p className="sub pane-foot">{foot}</p>
        </div>
      )}
    </main>
  );
}
