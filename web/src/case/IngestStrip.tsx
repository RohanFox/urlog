import { Link } from 'react-router';
import type { DashboardResponse } from '../api/schema.gen';
import { bytes, count, number, percent, utcMinute } from '../app/format';
import { Banner, Tag } from '../ui/controls';
import { format, futureRange, UNDATED, type SearchUrl } from '../search/urlState';
import { flagName, stateName } from './flags';
import './case.css';

/** A search of the whole case with nothing else chosen: the two counts add their range to it. */
const EVERYTHING: SearchUrl = { q: '', from: undefined, to: undefined, tz: undefined, cols: [], sel: undefined };

const STATISTICS: Record<DashboardResponse['statistics']['state'], string> = {
  none: 'Not computed yet',
  ok: 'Current',
  skipped: 'Skipped',
  timed_out: 'Timed out',
  failed: 'Failed',
};

function Statistics({ value }: { value: DashboardResponse['statistics'] }) {
  const stale = value.state === 'skipped' || value.state === 'timed_out' || value.state === 'failed';
  return (
    <>
      <dt>Field statistics</dt>
      <dd>
        <span className={stale ? 'warn-text' : undefined}>{STATISTICS[value.state]}</span>
        {value.pending ? ' · a new run is pending' : ''}
        {value.computed_at === null ? '' : ` · computed ${utcMinute(value.computed_at)}`}
        {value.reason === null ? null : <span className="sub"> · {value.reason}</span>}
      </dd>
    </>
  );
}

interface Props {
  dashboard: DashboardResponse;
  /** Shows the files a flag counts, for the flags the file list can filter by. */
  onFilter: (filter: { state?: string | undefined; unassigned?: boolean | undefined }) => void;
}

/**
 * What the case holds and what the server is doing with it: the span of its events with the events a default
 * search leaves out, the state of its statistics, the queue with the jobs that run, and the flags that want
 * a person to look.
 */
export function IngestStrip({ dashboard, onFilter }: Props) {
  const { span, files, ingest, statistics, hosts, unassigned } = dashboard;
  const flags = Object.entries(files.flags).filter(([, n]) => n > 0);
  const states = Object.entries(files.by_state).filter(([, n]) => n > 0);

  return (
    <section className="section section-pad strip" aria-label="Case overview">
      {ingest.paused === null ? null : (
        <Banner kind="notice" title="Intake is paused">
          {ingest.paused}
        </Banner>
      )}
      <dl className="facts strip-facts">
        <dt>Events</dt>
        <dd>
          <span className="mono">{span.from !== null && span.to !== null ? `${utcMinute(span.from)} to ${utcMinute(span.to)}` : 'No events with a usable time yet'}</span>
          {span.undated_events > 0 ? (
            <>
              {' · '}
              <Link className="warn-text" to={`/cases/${dashboard.case.id}/search${format({ ...EVERYTHING, ...UNDATED })}`}>
                {count(span.undated_events, 'event', 'events')} without a usable time
              </Link>
            </>
          ) : null}
          {span.future_events > 0 ? (
            <>
              {' · '}
              {span.to === null ? (
                <span className="warn-text">{count(span.future_events, 'event', 'events')} dated after their file arrived</span>
              ) : (
                <Link className="warn-text" to={`/cases/${dashboard.case.id}/search${format({ ...EVERYTHING, ...futureRange(span.to) })}`}>
                  {count(span.future_events, 'event', 'events')} dated after their file arrived
                </Link>
              )}
            </>
          ) : null}
        </dd>
        <dt>Files</dt>
        <dd className="strip-tags">
          <span>
            {count(files.total, 'file', 'files')} · {bytes(files.bytes_arrived)} arrived · {number(files.rows)} rows · {number(files.rejects)} rejected lines
          </span>
          {states.map(([state, n]) => (
            <button key={state} type="button" className={state === 'artifact' ? 'chip chip-warn' : 'chip'} title={state === 'artifact' ? 'Show the files that were kept and not parsed, each with its reason' : `Show the ${state} files`} onClick={() => { onFilter({ state }); }}>
              {stateName(state)} <span className="num">{number(n)}</span>
            </button>
          ))}
        </dd>
        <dt>Queue</dt>
        <dd>
          {ingest.queued + ingest.parsing === 0 ? 'Nothing waits' : `${number(ingest.queued)} waiting · ${number(ingest.parsing)} being parsed`}
          {ingest.failed > 0 ? <span className="err-text"> · {count(ingest.failed, 'job', 'jobs')} failed</span> : null}
          {ingest.cancelled > 0 ? ` · ${number(ingest.cancelled)} cancelled` : ''}
        </dd>
        <Statistics value={statistics} />
        {flags.length === 0 ? null : (
          <>
            <dt>To look at</dt>
            <dd className="strip-tags">
              {flags.map(([flag, n]) =>
                flag === 'quarantined' || flag === 'unassigned' ? (
                  <button key={flag} type="button" className="chip chip-warn" title="Show these files" onClick={() => { onFilter(flag === 'quarantined' ? { state: 'quarantined' } : { unassigned: true }); }}>
                    {flagName(flag)} <span className="num">{number(n)}</span>
                  </button>
                ) : (
                  <Tag key={flag}>
                    {flagName(flag)} {number(n)}
                  </Tag>
                ),
              )}
            </dd>
          </>
        )}
      </dl>
      {ingest.current.length === 0 ? null : (
        <ul className="jobs" aria-label="Jobs that run">
          {ingest.current.map((job) => (
            <li key={job.job_id} className="job">
              <span className="sub">
                {job.kind} of file {job.file_id}
              </span>
              <progress className="meter" value={percent(job.bytes_read, job.bytes_total)} max={100} aria-label={`${job.kind} of file ${job.file_id}`} />
              <span className="sub num">
                {percent(job.bytes_read, job.bytes_total)}% · {number(job.rows)} rows · {number(job.rejects)} rejected
              </span>
            </li>
          ))}
        </ul>
      )}
      {hosts.length === 0 && unassigned === null ? null : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th className="host-cell">Host</th>
                <th className="fit">Zone</th>
                <th className="fit right">Files</th>
                <th className="fit right">Rows</th>
                <th className="fit right">Rejected</th>
                <th className="fit">From</th>
                <th className="fit">To</th>
              </tr>
            </thead>
            <tbody>
              {[...hosts, ...(unassigned === null ? [] : [unassigned])].map((host) => (
                <tr key={host.id}>
                  <td className="host-cell mono">
                    <span className="ellipsis cut" title={host.id === 0 ? undefined : host.name}>
                      {host.id === 0 ? 'no host assigned' : host.name}
                    </span>
                  </td>
                  <td className="fit">
                    <span className="mono">{host.timezone}</span> {host.timezone_assumed ? <Tag>assumed</Tag> : null}
                  </td>
                  <td className="fit right num">{number(host.files)}</td>
                  <td className="fit right num">{number(host.rows)}</td>
                  <td className="fit right num">{number(host.rejects)}</td>
                  <td className="fit mono muted">{host.from === null ? '' : utcMinute(host.from)}</td>
                  <td className="fit mono muted">{host.to === null ? '' : utcMinute(host.to)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
