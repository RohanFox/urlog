import { lazy, Suspense, useId, useMemo, type RefObject } from 'react';
import { number } from '../app/format';
import { Button } from '../ui/controls';
import type { Mark } from './Editor';
import { plain } from './inert';
import type { Refusal } from './run';
import type { Warning } from './stream';
import './search.css';

const Editor = lazy(() => import('./Editor'));

/** The longest query the server reads. A longer one is counted here before it is sent. */
export const MAX_QUERY = 8000;

interface Props {
  draft: string;
  /** The text the marks below were made for. They are shown only while the draft is still that text. */
  ran: string;
  warnings: readonly Warning[];
  /** A refusal of the query itself: its syntax, or what the server would not run. */
  refusal: Refusal | undefined;
  running: boolean;
  onDraft: (text: string) => void;
  onRun: () => void;
  onCancel: () => void;
  focusRef: RefObject<(() => void) | null>;
}

/** The query, the button that runs it, and what the server said about the text that ran. */
export function QueryBox({ draft, ran, warnings, refusal, running, onDraft, onRun, onCancel, focusRef }: Props) {
  const notes = useId();
  const current = draft === ran;
  const tooLong = draft.length > MAX_QUERY;

  const marks = useMemo<Mark[]>(() => {
    if (!current) {
      return [];
    }
    const out: Mark[] = warnings.map((w) => ({ offset: w.offset, length: w.length, kind: 'warning', message: plain(w.message) }));
    if (refusal?.code === 'query_syntax') {
      // An error without a place (a text that is too long) is an error of the whole text.
      out.push({ offset: refusal.offset ?? 0, length: refusal.length ?? (refusal.offset === undefined ? ran.length : 1), kind: 'error', message: plain(refusal.message) });
    }
    return out;
  }, [current, warnings, refusal, ran]);

  const said = current && refusal !== undefined;
  return (
    <div className="query">
      <div className="query-row">
        <div className={said || tooLong ? 'query-box query-box-bad' : 'query-box'}>
          <Suspense fallback={<div className="query-editor query-editor-loading" aria-hidden="true" />}>
            <Editor value={draft} marks={marks} label="Query" describedBy={said || tooLong || marks.length > 0 ? notes : undefined} onChange={onDraft} onRun={onRun} focusRef={focusRef} />
          </Suspense>
        </div>
        {running ? (
          <Button onClick={onCancel} icon="close">
            Cancel
          </Button>
        ) : (
          <Button kind="primary" onClick={onRun} disabled={tooLong}>
            Search
          </Button>
        )}
      </div>
      <div className={running ? 'query-bar query-bar-on' : 'query-bar'} aria-hidden="true" />
      <div id={notes} className="query-notes">
        {tooLong ? (
          <p className="field-error" role="alert">
            The query is {number(draft.length)} characters long. The server reads at most {number(MAX_QUERY)}.
          </p>
        ) : null}
        {said ? (
          <p className="field-error" role="alert">
            {refusal.code === 'query_syntax' && refusal.offset !== undefined ? `At character ${number(refusal.offset + 1)}: ` : ''}
            {plain(refusal.message, 2000)}
            {refusal.detail === undefined ? '' : `. ${plain(refusal.detail, 2000)}`}
          </p>
        ) : null}
        {current && warnings.length > 0 ? (
          <ul className="query-warnings">
            {warnings.map((w, i) => (
              <li key={i} className="warn-text">
                At character {number(w.offset + 1)}: {plain(w.message, 2000)}
              </li>
            ))}
          </ul>
        ) : null}
      </div>
    </div>
  );
}
