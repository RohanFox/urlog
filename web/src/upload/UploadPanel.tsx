import { useId, useRef, useState, type DragEvent } from 'react';
import { bytes, percent } from '../app/format';
import { Loader } from '../brand/Logo';
import { Button } from '../ui/controls';
import { Icon } from '../ui/Icon';
import { FINAL, type UploadView } from './machine';
import { uploads, UPLOADS_AT_ONCE } from './queue';
import '../case/case.css';
import './upload.css';

interface DropZoneProps {
  caseId: number;
  hosts: readonly string[];
  /** The case holds no file yet: the zone is the page's main content and says what happens to a file. */
  first: boolean;
}

/** Where files enter a case. Dropped or chosen, each becomes an upload of its own. */
export function DropZone({ caseId, hosts, first }: DropZoneProps) {
  const [host, setHost] = useState('');
  const [over, setOver] = useState(false);
  const [refused, setRefused] = useState<string>();
  const input = useRef<HTMLInputElement>(null);
  const hostId = useId();

  const take = (files: readonly File[]) => {
    setRefused(undefined);
    if (files.length > 0) {
      uploads.add(caseId, files, host === '' ? undefined : host);
    }
  };

  const drop = (event: DragEvent) => {
    event.preventDefault();
    setOver(false);
    // A folder arrives as an entry without a type and without bytes a page may read as one file.
    const items = [...event.dataTransfer.items];
    if (items.some((item) => item.webkitGetAsEntry()?.isDirectory === true)) {
      setRefused('A folder cannot be dropped here. Drop its files, or one archive of it.');
      return;
    }
    take([...event.dataTransfer.files]);
  };

  return (
    // Dropping is a pointer's way in; the button beside it does the same from the keyboard.
    // eslint-disable-next-line jsx-a11y/no-noninteractive-element-interactions
    <section
      className={['drop', first ? 'drop-first' : '', over ? 'drop-over' : ''].filter(Boolean).join(' ')}
      aria-label="Add evidence"
      onDragOver={(event) => {
        event.preventDefault();
        setOver(true);
      }}
      onDragLeave={() => {
        setOver(false);
      }}
      onDrop={drop}
    >
      <Icon name="upload" />
      <div className="drop-text">
        <strong>{first ? 'Add the first evidence files' : 'Drop files to add evidence'}</strong>
        {first ? (
          <p className="muted">
            Drop log files or archives here. Each file is hashed with SHA-256 on arrival, stored read-only, then parsed. An upload goes on from where it stopped if the connection drops.
          </p>
        ) : null}
        {refused === undefined ? null : (
          <p className="drop-refused" role="status">
            {refused}
          </p>
        )}
      </div>
      <input
        ref={input}
        className="sr-only"
        type="file"
        multiple
        tabIndex={-1}
        aria-label="Files to add"
        onChange={(event) => {
          take([...(event.target.files ?? [])]);
          event.target.value = '';
        }}
      />
      <Button kind={first ? 'primary' : 'default'} small={!first} icon="file" onClick={() => input.current?.click()}>
        Choose files
      </Button>
      <span className="grow" />
      <label className="sub" htmlFor={hostId}>
        Host for these files
      </label>
      <select
        id={hostId}
        className="input"
        value={host}
        onChange={(event) => {
          setHost(event.target.value);
        }}
      >
        <option value="">Detect from the file</option>
        {hosts.map((name) => (
          <option key={name} value={name}>
            {name}
          </option>
        ))}
      </select>
    </section>
  );
}

const PHASE_TEXT: Record<UploadView['phase'], string> = {
  waiting: `Waiting: ${UPLOADS_AT_ONCE} uploads run at a time`,
  starting: 'Starting',
  receiving: 'Uploading',
  paused: 'Paused',
  retrying: 'Connection lost. Asking the server where to go on',
  verifying: 'Verifying: every byte is stored; the server is hashing and recording the file',
  interrupted: 'Interrupted by a reload of the page. Choose the same file again to go on from here',
  done: 'Recorded',
  duplicate: 'Already in this case',
  rejected: 'Not taken. Nothing was stored',
  failed: 'Failed',
  refused: 'Refused',
};

function UploadRow({ upload, onShowFile }: { upload: UploadView; onShowFile: (fileId: number) => void }) {
  const input = useRef<HTMLInputElement>(null);
  const [wrongFile, setWrongFile] = useState(false);
  const { phase } = upload;
  const busy = phase === 'starting' || phase === 'retrying' || phase === 'verifying';
  const bad = phase === 'failed';
  const share = percent(upload.sent, upload.size);

  return (
    <li className={bad ? 'upload upload-bad' : 'upload'}>
      <div className="upload-head">
        <span className="mono ellipsis" title={upload.name}>
          {upload.name}
        </span>
        <span className="sub num">
          {phase === 'receiving' || phase === 'paused' || phase === 'retrying' || phase === 'interrupted' ? `${bytes(upload.sent)} of ${bytes(upload.size)} · ${share}%` : bytes(upload.size)}
        </span>
        <span className="grow" />
        {phase === 'receiving' || phase === 'starting' || phase === 'retrying' || phase === 'waiting' ? (
          <Button small kind="ghost" icon="pause" onClick={() => { uploads.pause(upload.key); }}>
            Pause
          </Button>
        ) : null}
        {phase === 'paused' ? (
          <Button small icon="play" onClick={() => { uploads.resume(upload.key); }}>
            Resume
          </Button>
        ) : null}
        {phase === 'interrupted' ? (
          <>
            <input
              ref={input}
              className="sr-only"
              type="file"
              tabIndex={-1}
              aria-label={`The file ${upload.name} again`}
              onChange={(event) => {
                const file = event.target.files?.[0];
                event.target.value = '';
                if (file === undefined) {
                  return;
                }
                const before = uploads.views().find((u) => u.key === upload.key)?.phase;
                uploads.add(upload.caseId, [file], upload.host);
                // The file is taken only when it is the one that was being sent; another one becomes its own upload.
                setWrongFile(before === 'interrupted' && uploads.views().find((u) => u.key === upload.key)?.phase === 'interrupted');
              }}
            />
            <Button small icon="file" onClick={() => input.current?.click()}>
              Choose the file again
            </Button>
          </>
        ) : null}
        {(phase === 'failed' || phase === 'refused') && upload.message !== undefined ? (
          <Button small icon="retry" onClick={() => { uploads.again(upload.key); }}>
            Start again
          </Button>
        ) : null}
        {phase === 'duplicate' && upload.fileId !== undefined ? (
          <Button small onClick={() => { onShowFile(upload.fileId ?? 0); }}>
            Show the file
          </Button>
        ) : null}
        {phase === 'verifying' ? null : (
          <Button small kind="ghost" icon="close" aria-label={FINAL.has(phase) ? `Clear ${upload.name}` : `Cancel ${upload.name}`} onClick={() => void uploads.dismiss(upload.key)}>
            {FINAL.has(phase) ? 'Clear' : 'Cancel'}
          </Button>
        )}
      </div>
      {phase === 'receiving' || phase === 'paused' || phase === 'retrying' || phase === 'interrupted' ? (
        <progress className="meter" value={share} max={100} aria-label={`${upload.name}, uploaded`} />
      ) : null}
      <p className="upload-state" role="status">
        {busy ? <Loader size={16} /> : null}
        <span>
          {upload.sessionEnded ? 'Paused: your session ended. Sign in and it goes on' : PHASE_TEXT[phase]}
          {upload.message === undefined ? '' : `: ${upload.message}`}
          {wrongFile ? '. That was another file; it was added as an upload of its own.' : ''}
        </span>
      </p>
    </li>
  );
}

/** The uploads of this tab for a case, each with what it is doing and what the user can do about it. */
export function UploadList({ list, onShowFile }: { list: readonly UploadView[]; onShowFile: (fileId: number) => void }) {
  if (list.length === 0) {
    return null;
  }
  return (
    <section className="section" aria-label="Uploads">
      <ul className="uploads">
        {list.map((upload) => (
          <UploadRow key={upload.key} upload={upload} onShowFile={onShowFile} />
        ))}
      </ul>
    </section>
  );
}
