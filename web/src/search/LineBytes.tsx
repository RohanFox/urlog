import { useEffect, useRef, useState } from 'react';
import { ApiError } from '../api/http';
import { number } from '../app/format';
import { Button } from '../ui/controls';
import { fetchBytes, HEX_BYTES, hexDump, MAX_JOINED_PARTS, saveBytes, TooManyParts, wholeLine, type EventKey, type LineBytes as Fetched, type LinePart } from './bytes';
import './search.css';

type State = { kind: 'idle' } | { kind: 'loading' } | { kind: 'shown'; line: Fetched } | { kind: 'failed'; reason: string };
type Join = { kind: 'idle' } | { kind: 'joining'; done: number; of: number } | { kind: 'saved'; bytes: number; parts: number } | { kind: 'failed'; reason: string };

function reasonOf(cause: unknown): string {
  if (cause instanceof TooManyParts) {
    return `The line has more than ${MAX_JOINED_PARTS} parts, which is more than the page joins. Show its parts and export them.`;
  }
  if (cause instanceof ApiError) {
    if (cause.status === 429) {
      return 'The server is busy. Try again in a moment.';
    }
    return cause.offline ? 'The server did not answer.' : cause.message;
  }
  return cause instanceof Error ? cause.message : 'The bytes could not be read.';
}

interface Props {
  event: EventKey;
  /** What the server says of the line: false when its text holds a replacement for bytes that are not UTF-8. Undefined when it did not say. */
  validUtf8: boolean | undefined;
  /** The place of this event in a line that was stored as several, when it is a piece of one. */
  part: LinePart | undefined;
  /** Searches for the pieces of the line. */
  onParts: (firstOffset: number) => void;
}

/**
 * The bytes of the line, on request: as hex and as a file. The text above them is the line decoded; where that
 * changed anything the server says so, and these are what the file holds. A piece of a long line says which
 * line it is a piece of, and the whole line can be saved as one file.
 */
export function LineBytes({ event, validUtf8, part, onParts }: Props) {
  const [state, setState] = useState<State>({ kind: 'idle' });
  const [join, setJoin] = useState<Join>({ kind: 'idle' });
  const abort = useRef<AbortController | null>(null);
  useEffect(
    () => () => {
      abort.current?.abort();
    },
    [],
  );

  const signal = () => {
    abort.current?.abort();
    abort.current = new AbortController();
    return abort.current.signal;
  };
  const show = () => {
    setState({ kind: 'loading' });
    fetchBytes(event, signal()).then(
      (line) => {
        setState({ kind: 'shown', line });
      },
      (cause: unknown) => {
        if (!(cause instanceof DOMException && cause.name === 'AbortError')) {
          setState({ kind: 'failed', reason: reasonOf(cause) });
        }
      },
    );
  };
  const saveWhole = (firstOffset: number) => {
    setJoin({ kind: 'joining', done: 0, of: part?.total ?? 0 });
    wholeLine(event, firstOffset, signal(), (done, of) => {
      setJoin({ kind: 'joining', done, of });
    }).then(
      (bytes) => {
        saveBytes(bytes, `case-${event.caseId}-file-${event.fileId}-line-${firstOffset}.bin`);
        setJoin((now) => ({ kind: 'saved', bytes: bytes.length, parts: now.kind === 'joining' ? now.of : 0 }));
      },
      (cause: unknown) => {
        if (!(cause instanceof DOMException && cause.name === 'AbortError')) {
          setJoin({ kind: 'failed', reason: reasonOf(cause) });
        }
      },
    );
  };

  return (
    <div className="line-bytes">
      {validUtf8 === false ? (
        <p className="warn-text detail-note" role="note">
          This line holds bytes that are not valid UTF-8. The text above has a replacement character for each, so neither it nor Copy is the exact line. The bytes are.
        </p>
      ) : null}
      {part === undefined ? null : (
        <p className="detail-note" role="note">
          Part {number(part.part)}
          {part.total === undefined ? '' : ` of ${number(part.total)}`} of one line that begins at offset {number(part.firstOffset)} of this file. A line this long is stored as several events.{' '}
          <button
            type="button"
            className="linklike"
            onClick={() => {
              onParts(part.firstOffset);
            }}
          >
            Show the parts of this line
          </button>{' '}
          <button
            type="button"
            className="linklike"
            disabled={join.kind === 'joining'}
            onClick={() => {
              saveWhole(part.firstOffset);
            }}
          >
            Save the whole line
          </button>
          {join.kind === 'joining' ? (
            <span role="status"> Reading part {number(join.done)}{join.of > 0 ? ` of ${number(join.of)}` : ''}</span>
          ) : join.kind === 'saved' ? (
            <span role="status"> Saved {number(join.bytes)} bytes from {number(join.parts)} parts.</span>
          ) : join.kind === 'failed' ? (
            <span className="err-text" role="alert"> {join.reason}</span>
          ) : null}
        </p>
      )}
      <div className="detail-sub">
        <h3>Bytes</h3>
        <span className="sub">{state.kind === 'shown' ? `${number(state.line.bytes.length)} bytes, as stored` : 'the stored bytes of this line, exactly'}</span>
        <span className="grow" />
        {state.kind === 'shown' ? (
          <Button
            small
            icon="download"
            onClick={() => {
              saveBytes(state.line.bytes, `case-${event.caseId}-file-${event.fileId}-offset-${event.offset}.bin`);
            }}
          >
            Save the bytes
          </Button>
        ) : (
          <Button small busy={state.kind === 'loading'} onClick={show}>
            Show the bytes
          </Button>
        )}
      </div>
      {state.kind === 'failed' ? (
        <p className="err-text detail-note" role="alert">
          The bytes could not be read: {state.reason}
        </p>
      ) : null}
      {state.kind === 'shown' ? (
        <>
          <pre className="raw hex" aria-label="Bytes of the line, as hex">
            {hexDump(state.line.bytes)}
          </pre>
          <p className="sub detail-note">
            {state.line.bytes.length > HEX_BYTES ? `Showing the first ${number(HEX_BYTES)} of ${number(state.line.bytes.length)} bytes; the file holds all of them. ` : ''}
            {state.line.sha256 === undefined ? '' : (
              <>
                SHA-256 <span className="mono">{state.line.sha256}</span>
              </>
            )}
          </p>
        </>
      ) : null}
    </div>
  );
}
