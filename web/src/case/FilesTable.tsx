import { useState } from 'react';
import type { DashboardJobResponse, FileResponse, JobResponse } from '../api/schema.gen';
import { bytes, number, percent } from '../app/format';
import { Button, Tag } from '../ui/controls';
import { Icon } from '../ui/Icon';
import { flagName } from './flags';
import './case.css';

/** States in which something went wrong with the file itself and a person should read why. */
const TROUBLE = new Set<FileResponse['state']>(['failed', 'quarantined', 'unreadable']);

const STATE_TEXT: Record<FileResponse['state'], string> = {
  received: 'Received, waiting to be parsed',
  parsed: 'Parsed',
  failed: 'Parse failed',
  quarantined: 'Quarantined',
  artifact: 'Kept, not a log',
  archived: 'Archived',
  expanded: 'Archive, expanded',
  unreadable: 'Unreadable',
};

function Hash({ value, name }: { value: string; name: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <span className="hash">
      <span className="mono muted" title={value}>
        {value.slice(0, 10)}…{value.slice(-4)}
      </span>
      <Button
        small
        kind="ghost"
        aria-label={`Copy the whole SHA-256 of ${name}`}
        onClick={() => {
          void navigator.clipboard.writeText(value).then(() => {
            setCopied(true);
            window.setTimeout(() => {
              setCopied(false);
            }, 2000);
          });
        }}
      >
        <Icon name={copied ? 'check' : 'copy'} />
      </Button>
      <span className="sr-only" role="status">
        {copied ? 'Copied' : ''}
      </span>
    </span>
  );
}

function State({ file, running, failed }: { file: FileResponse; running: DashboardJobResponse | undefined; failed: JobResponse | undefined }) {
  if (running !== undefined) {
    const share = percent(running.bytes_read, running.bytes_total);
    return (
      <span className="file-state">
        <progress className="meter" value={share} max={100} aria-label={`${file.original_name}, parsed`} />
        <span className="num">
          {share}% · {number(running.rows)} rows
        </span>
      </span>
    );
  }
  const trouble = TROUBLE.has(file.state);
  return (
    <span className={trouble ? 'file-state err-text' : 'file-state'}>
      {trouble ? <Icon name="alert" /> : file.state === 'parsed' ? <Icon name="check" /> : null}
      <span>
        {STATE_TEXT[file.state]}
        {file.state === 'failed' && failed?.message != null ? <span className="file-reason">{failed.message}</span> : null}
        {file.reparse === null ? null : <span className="sub"> · {file.reparse.kind} {file.reparse.state}</span>}
      </span>
    </span>
  );
}

interface Props {
  files: readonly FileResponse[];
  /** The jobs that run, by file. */
  running: ReadonlyMap<number, DashboardJobResponse>;
  /** The newest failed job of a file, for its reason. */
  failed: ReadonlyMap<number, JobResponse>;
}

/** The evidence files of a case, in the order they were recorded. */
export function FilesTable({ files, running, failed }: Props) {
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>File</th>
            <th>Host</th>
            <th>Format</th>
            <th className="right">Size</th>
            <th>SHA-256 at intake</th>
            <th>State</th>
            <th className="right">Rows</th>
            <th className="right">Rejected</th>
            <th>Flags</th>
          </tr>
        </thead>
        <tbody>
          {files.map((file) => (
            <tr key={file.id} className={TROUBLE.has(file.state) ? 'row-err' : file.flags.length > 0 ? 'row-warn' : undefined}>
              <td className="file-name">
                <span className="mono ellipsis" title={file.archive_path ?? file.original_name}>
                  {file.archive_path ?? file.original_name}
                </span>
              </td>
              <td>
                {file.host === null ? (
                  <span className="muted">none</span>
                ) : (
                  <>
                    <span className="mono">{file.host.name}</span> {file.host.inferred ? <Tag>inferred</Tag> : null}
                  </>
                )}
              </td>
              <td>
                {file.effective.source !== '' ? file.effective.source : <span className="muted">{file.detection.kind ?? 'not detected'}</span>}
                {file.detection.layout === null ? '' : ` ${file.detection.layout}`}
                {file.detection.confidence === null ? null : <span className="muted num"> {Math.round(file.detection.confidence * 100)}%</span>}
                {file.override === null ? null : <> <Tag>override</Tag></>}
              </td>
              <td className="right num">{bytes(file.size)}</td>
              <td>
                <Hash value={file.sha256} name={file.original_name} />
              </td>
              <td>
                <State file={file} running={running.get(file.id)} failed={failed.get(file.id)} />
              </td>
              <td className="right num">{number(file.rows)}</td>
              <td className="right num">{number(file.rejects)}</td>
              <td>
                <span className="file-flags">
                  {file.flags.map((flag) => (
                    <Tag key={flag}>{flagName(flag)}</Tag>
                  ))}
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
