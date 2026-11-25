import type { DashboardResponse } from '../api/schema.gen';
import { bytes, count, number, percent, utcMinute } from '../app/format';
import { Banner, Tag } from '../ui/controls';
import { flagName } from './flags';
import './case.css';

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
          {span.undated_events > 0 ? <span className="warn-text"> · {count(span.undated_events, 'event', 'events')} without a usable time</span> : null}
          {span.future_events > 0 ? <span className="warn-text"> · {count(span.future_events, 'event', 'events')} dated after their file arrived</span> : null}
        </dd>
        <dt>Files</dt>
        <dd className="strip-tags">
          <span>
            {count(files.total, 'file', 'files')} · {bytes(files.bytes_arrived)} arrived · {number(files.rows)} rows · {number(files.rejects)} rejected lines
          </span>
          {states.map(([state, n]) => (
            <button key={state} type="button" className="chip" title={`Show the ${state} files`} onClick={() => { onFilter({ state }); }}>
              {state.replace('_', ' ')} <span className="num">{number(n)}</span>
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
                <th>Host</th>
                <th>Zone</th>
                <th className="right">Files</th>
                <th className="right">Rows</th>
                <th className="right">Rejected</th>
                <th>From</th>
                <th>To</th>
              </tr>
            </thead>
            <tbody>
              {[...hosts, ...(unassigned === null ? [] : [unassigned])].map((host) => (
                <tr key={host.id}>
                  <td className="mono">{host.id === 0 ? 'no host assigned' : host.name}</td>
                  <td>
                    <span className="mono">{host.timezone}</span> {host.timezone_assumed ? <Tag>assumed</Tag> : null}
                  </td>
                  <td className="right num">{number(host.files)}</td>
                  <td className="right num">{number(host.rows)}</td>
                  <td className="right num">{number(host.rejects)}</td>
                  <td className="mono muted">{host.from === null ? '' : utcMinute(host.from)}</td>
                  <td className="mono muted">{host.to === null ? '' : utcMinute(host.to)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
