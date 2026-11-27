import { useState } from 'react';
import type { DashboardJobResponse, FileResponse, JobResponse } from '../api/schema.gen';
import { bytes, number, percent } from '../app/format';
import { Button, Tag } from '../ui/controls';
import { Icon } from '../ui/Icon';
import { flagName, formatReason } from './flags';
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
      <span className="mono muted hash-short" title={value}>
        {value.slice(0, 10)}…{value.slice(-4)}
      </span>
      <span className="mono muted hash-full">{value}</span>
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

function State({ file, running, failed, asText }: { file: FileResponse; running: DashboardJobResponse | undefined; failed: JobResponse | undefined; asText: AsText | undefined }) {
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
        {file.state === 'artifact' ? (
          <>
            <span className="file-reason">{formatReason(file.detection.format)}</span>
            {asText === undefined ? null : (
              <span className="file-reason">
                <Button small busy={asText.pending === file.id} disabled={asText.pending !== undefined} aria-label={`Parse ${file.original_name} as text`} onClick={() => { asText.run(file); }}>
                  Parse as text
                </Button>
                {asText.failed?.id === file.id ? <span className="err-text"> {asText.failed.reason}</span> : null}
              </span>
            )}
          </>
        ) : null}
        {file.reparse === null ? null : <span className="sub"> · {file.reparse.kind} {file.reparse.state}</span>}
      </span>
    </span>
  );
}

/** A file's flags. They stand in their own column, and under the file's name where that column is left out. */
function Flags({ flags }: { flags: FileResponse['flags'] }) {
  return (
    <span className="file-flags">
      {flags.map((flag) => (
        <Tag key={flag}>{flagName(flag)}</Tag>
      ))}
    </span>
  );
}

/** The action that sends a file kept as "not a log" to the parser as text: one call, a person's decision. */
export interface AsText {
  run: (file: FileResponse) => void;
  /** The file whose request is on its way. */
  pending: number | undefined;
  failed: { id: number; reason: string } | undefined;
}

interface Props {
  /** Absent for a reader who may not change the case, and for a case that takes no changes. */
  asText?: AsText | undefined;
  files: readonly FileResponse[];
  /** The jobs that run, by file. */
  running: ReadonlyMap<number, DashboardJobResponse>;
  /** The newest failed job of a file, for its reason. */
  failed: ReadonlyMap<number, JobResponse>;
}

/** The evidence files of a case, in the order they were recorded. */
export function FilesTable({ files, running, failed, asText }: Props) {
  return (
    <div className="table-wrap">
      <table className="table files-table">
        <thead>
          <tr>
            <th className="c-name">File</th>
            <th className="fit">Host</th>
            <th className="fit">Format</th>
            <th className="fit right">Size</th>
            <th className="fit">SHA-256 at intake</th>
            <th className="fit">State</th>
            <th className="fit right">Rows</th>
            <th className="fit right">Rejected</th>
            <th className="c-flags">Flags</th>
          </tr>
        </thead>
        <tbody>
          {files.map((file) => (
            <tr key={file.id} className={TROUBLE.has(file.state) ? 'row-err' : file.flags.length > 0 ? 'row-warn' : undefined}>
              <td className="file-name c-name">
                <span className="mono ellipsis cut" title={file.archive_path ?? file.original_name}>
                  {file.archive_path ?? file.original_name}
                </span>
                <Flags flags={file.flags} />
              </td>
              <td className="fit">
                {file.host === null ? (
                  <span className="muted">none</span>
                ) : (
                  <>
                    <span className="mono">{file.host.name}</span> {file.host.inferred ? <Tag>inferred</Tag> : null}
                  </>
                )}
              </td>
              <td className="fit">
                {file.effective.source !== '' ? file.effective.source : <span className="muted">{file.detection.kind ?? 'not detected'}</span>}
                {file.detection.layout === null ? '' : ` ${file.detection.layout}`}
                {file.detection.confidence === null ? null : <span className="muted num"> {Math.round(file.detection.confidence * 100)}%</span>}
                {file.override === null ? null : <> <Tag>override</Tag></>}
              </td>
              <td className="fit right num">{bytes(file.size)}</td>
              <td className="fit">
                <Hash value={file.sha256} name={file.original_name} />
              </td>
              <td className="fit">
                <State file={file} running={running.get(file.id)} failed={failed.get(file.id)} asText={asText} />
              </td>
              <td className="fit right num">{number(file.rows)}</td>
              <td className="fit right num">{number(file.rejects)}</td>
              <td className="c-flags">
                <Flags flags={file.flags} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
