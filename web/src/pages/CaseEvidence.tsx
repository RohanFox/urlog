import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { useParams } from 'react-router';
import { ApiError, call } from '../api/http';
import type { DashboardJobResponse, FileResponse, JobResponse } from '../api/schema.gen';
import { roleName, stateName } from '../app/format';
import { useUser } from '../app/session';
import { FilesTable } from '../case/FilesTable';
import { IngestStrip } from '../case/IngestStrip';
import { Banner, Button, EmptyState, LoadingBlock, SkeletonRows, Tag } from '../ui/controls';
import { FINAL } from '../upload/machine';
import { uploads, useUploads } from '../upload/queue';
import { DropZone, UploadList } from '../upload/UploadPanel';
import { NoSuchCase } from './Standing';
import './pages.css';

/** While something moves on the server the page asks again this often. */
const REFRESH_MS = 2000;
const PAGE = 100;
const STATES: ReadonlyArray<FileResponse['state']> = ['received', 'parsed', 'failed', 'quarantined', 'artifact', 'expanded', 'archived', 'unreadable'];

interface Filter {
  state?: string | undefined;
  unassigned?: boolean | undefined;
  q?: string | undefined;
  /** One file, after "show the file" on an upload that was already in the case. */
  id?: number | undefined;
}

/**
 * The evidence of a case: what it holds, what the server is doing with it, the uploads of this tab and the
 * list of files with every state and flag the server reports.
 */
export function CaseEvidence() {
  const { caseId: text = '' } = useParams();
  const valid = /^[0-9]+$/.test(text);
  const caseId = Number(text);
  const user = useUser();
  const queries = useQueryClient();
  const mine = useUploads(caseId);
  const [filter, setFilter] = useState<Filter>({});
  const uploading = mine.some((u) => !FINAL.has(u.phase) && u.phase !== 'interrupted' && u.phase !== 'paused');

  const dashboard = useQuery({
    queryKey: ['dashboard', caseId],
    queryFn: ({ signal }) => call('GetCaseDashboard', { params: { caseId }, signal }),
    enabled: valid,
    // Asked again while a job waits or runs, a run of the statistics is pending, or an upload of this tab is at work.
    refetchInterval: (query) => {
      const data = query.state.data;
      const moving = data !== undefined && (data.ingest.queued + data.ingest.parsing > 0 || data.statistics.pending);
      return moving || uploading ? REFRESH_MS : false;
    },
  });

  const files = useInfiniteQuery({
    queryKey: ['files', caseId, filter],
    initialPageParam: 0,
    queryFn: ({ pageParam, signal }) =>
      call('ListCaseFiles', {
        params: { caseId },
        signal,
        query: {
          after: filter.id === undefined ? String(pageParam) : String(filter.id - 1),
          limit: filter.id === undefined ? String(PAGE) : '1',
          state: filter.state,
          unassigned: filter.unassigned === true ? 'true' : undefined,
          q: filter.q === undefined || filter.q === '' ? undefined : filter.q,
        },
      }),
    getNextPageParam: (last) => (filter.id === undefined ? (last.next_after ?? undefined) : undefined),
    enabled: valid && dashboard.isSuccess,
  });

  // The reason of a failed parse is on its job, not on the file.
  const failedCount = dashboard.data?.ingest.failed ?? 0;
  const failedJobs = useQuery({
    queryKey: ['jobs', caseId, 'failed', failedCount],
    queryFn: ({ signal }) => call('ListCaseJobs', { params: { caseId }, signal, query: { state: 'failed', limit: '500' } }),
    enabled: valid && failedCount > 0,
  });

  // The file list follows the case: it is read again when the counts of the dashboard change, not on a timer.
  const signature = dashboard.data === undefined ? '' : JSON.stringify([dashboard.data.files.total, dashboard.data.files.by_state, dashboard.data.files.rows, dashboard.data.ingest.done]);
  useEffect(() => {
    if (signature !== '') {
      void queries.invalidateQueries({ queryKey: ['files', caseId] });
    }
  }, [signature, caseId, queries]);

  // After a reload, and after a sign-in that follows the end of a session: the uploads this tab remembers.
  useEffect(() => {
    if (valid) {
      uploads.restore(caseId, user.name);
    }
  }, [valid, caseId, user.name]);

  // A recorded upload is a file of the list from here on; its row leaves once the list has been asked again.
  const recorded = mine.filter((u) => u.phase === 'done').map((u) => u.key).join(',');
  useEffect(() => {
    if (recorded === '') {
      return;
    }
    void Promise.all([queries.invalidateQueries({ queryKey: ['dashboard', caseId] }), queries.invalidateQueries({ queryKey: ['files', caseId] })]).then(() => {
      for (const key of recorded.split(',')) {
        void uploads.dismiss(key);
      }
    });
  }, [recorded, caseId, queries]);

  const running = useMemo(() => new Map<number, DashboardJobResponse>((dashboard.data?.ingest.current ?? []).map((job) => [job.file_id, job])), [dashboard.data]);
  const failed = useMemo(() => {
    const newest = new Map<number, JobResponse>();
    for (const job of failedJobs.data?.jobs ?? []) {
      if (!newest.has(job.file_id)) {
        newest.set(job.file_id, job);
      }
    }
    return newest;
  }, [failedJobs.data]);

  // The server answers a case that does not exist and a case that is not shared with the caller alike.
  if (!valid || (dashboard.error instanceof ApiError && dashboard.error.status === 404)) {
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

  const data = dashboard.data;
  const c = data.case;
  const mayUpload = c.my_role !== 'ro';
  const active = c.state === 'active';
  const list = files.data?.pages.flatMap((page) => page.files) ?? [];
  const filtered = filter.state !== undefined || filter.unassigned === true || (filter.q ?? '') !== '' || filter.id !== undefined;
  const empty = data.files.total === 0;

  return (
    <main className="page">
      <div className="page-pad">
        <div className="page-head">
          <div>
            <h1>{c.name}</h1>
            <span className="sub">
              <span className="mono">case {c.id}</span> · owner <span className="mono">{c.owner}</span> · your role: {roleName(c.my_role)}
            </span>
          </div>
          <Tag accent={active}>{stateName(c.state)}</Tag>
        </div>

        {dashboard.isError ? (
          <Banner kind="error" title="The case could not be read again">
            What is shown is the last answer. The page keeps asking.
          </Banner>
        ) : null}
        {c.task === null ? null : (
          <Banner kind="notice" title={`A ${c.task.kind} of this case is ${c.task.state}`}>
            {c.task.message ?? 'Files and uploads wait until it has ended.'}
          </Banner>
        )}

        {!active ? (
          <Banner kind="notice" title={`This case is ${stateName(c.state).toLowerCase()}`}>
            Only an active case takes files. What it holds can still be read.
          </Banner>
        ) : !mayUpload ? (
          <Banner kind="notice" title="Your role in this case is read-only">
            Uploading needs read-write. The owner of the case can change your role.
          </Banner>
        ) : (
          <DropZone caseId={caseId} hosts={data.hosts.map((h) => h.name)} first={empty && mine.length === 0} />
        )}

        <UploadList
          list={mine}
          onShowFile={(id) => {
            setFilter({ id });
          }}
        />

        {empty ? null : <IngestStrip dashboard={data} onFilter={setFilter} />}

        {empty ? null : (
          <section className="section" aria-label="Evidence files">
            <div className="section-head">
              <h2>Evidence files</h2>
              <span className="sub">Read-only after intake, oldest first. The hash at intake is what a report cites.</span>
              <span className="grow" />
              <label className="sr-only" htmlFor="file-q">
                Part of a file name
              </label>
              <input
                id="file-q"
                className="input files-q"
                placeholder="Part of a name"
                value={filter.q ?? ''}
                onChange={(event) => {
                  setFilter({ ...filter, id: undefined, q: event.target.value });
                }}
              />
              <label className="sr-only" htmlFor="file-state">
                State
              </label>
              <select
                id="file-state"
                className="input"
                value={filter.state ?? ''}
                onChange={(event) => {
                  setFilter({ ...filter, id: undefined, state: event.target.value === '' ? undefined : event.target.value });
                }}
              >
                <option value="">Every state</option>
                {STATES.map((state) => (
                  <option key={state} value={state}>
                    {state}
                  </option>
                ))}
              </select>
              {filtered ? (
                <Button small icon="close" onClick={() => { setFilter({}); }}>
                  Clear the filter
                </Button>
              ) : null}
            </div>
            {files.isPending ? (
              <div className="table-wrap">
                <table className="table">
                  <tbody>
                    <SkeletonRows rows={4} columns={9} />
                  </tbody>
                </table>
              </div>
            ) : files.isError ? (
              <div className="section-pad">
                <Banner
                  kind="error"
                  title="The files could not be listed"
                  action={
                    <Button small icon="retry" onClick={() => void files.refetch()}>
                      Try again
                    </Button>
                  }
                >
                  {files.error instanceof ApiError && files.error.status === 400 ? files.error.message : 'The server did not answer the list.'}
                </Banner>
              </div>
            ) : list.length === 0 ? (
              <EmptyState title="No file matches the filter">
                <Button onClick={() => { setFilter({}); }}>Show every file</Button>
              </EmptyState>
            ) : (
              <>
                <FilesTable files={list} running={running} failed={failed} />
                {files.hasNextPage ? (
                  <div className="section-foot">
                    <span className="sub">
                      Showing {list.length} of {filtered ? 'the files that match' : data.files.total.toLocaleString('en-US')}
                    </span>
                    <Button small busy={files.isFetchingNextPage} onClick={() => void files.fetchNextPage()}>
                      Load {PAGE} more
                    </Button>
                  </div>
                ) : null}
              </>
            )}
          </section>
        )}
      </div>
    </main>
  );
}
