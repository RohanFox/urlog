import { useState, type ReactNode } from 'react';
import { number } from '../app/format';
import './search.css';

// Text that came out of a log is written by whoever was attacked or attacking. It reaches the page only through
// this module: as text nodes, never as markup, with every character that would act instead of show replaced by
// a marked placeholder. The original string is untouched; copying takes it, not what is drawn.

/** Characters with a name worth showing in the placeholder's title. */
const NAMES: Record<number, string> = {
  0x00: 'NUL',
  0x07: 'BEL',
  0x08: 'BACKSPACE',
  0x0a: 'LINE FEED',
  0x0d: 'CARRIAGE RETURN',
  0x1b: 'ESCAPE',
  0x7f: 'DELETE',
  0xad: 'SOFT HYPHEN',
  0x61c: 'ARABIC LETTER MARK',
  0x200b: 'ZERO WIDTH SPACE',
  0x200c: 'ZERO WIDTH NON-JOINER',
  0x200d: 'ZERO WIDTH JOINER',
  0x200e: 'LEFT-TO-RIGHT MARK',
  0x200f: 'RIGHT-TO-LEFT MARK',
  0x2028: 'LINE SEPARATOR',
  0x2029: 'PARAGRAPH SEPARATOR',
  0x202a: 'LEFT-TO-RIGHT EMBEDDING',
  0x202b: 'RIGHT-TO-LEFT EMBEDDING',
  0x202c: 'POP DIRECTIONAL FORMATTING',
  0x202d: 'LEFT-TO-RIGHT OVERRIDE',
  0x202e: 'RIGHT-TO-LEFT OVERRIDE',
  0x2060: 'WORD JOINER',
  0x2066: 'LEFT-TO-RIGHT ISOLATE',
  0x2067: 'RIGHT-TO-LEFT ISOLATE',
  0x2068: 'FIRST STRONG ISOLATE',
  0x2069: 'POP DIRECTIONAL ISOLATE',
  0xfeff: 'ZERO WIDTH NO-BREAK SPACE',
};

/** Controls (tab aside), deletes, the C1 block, direction marks and overrides, zero-width characters, lone surrogates. */
function acts(code: number): boolean {
  return (
    (code < 0x20 && code !== 0x09) ||
    (code >= 0x7f && code <= 0x9f) ||
    code === 0xad ||
    code === 0x61c ||
    code === 0x180e ||
    (code >= 0x200b && code <= 0x200f) ||
    (code >= 0x2028 && code <= 0x202e) ||
    (code >= 0x2060 && code <= 0x2069) ||
    code === 0xfeff ||
    (code >= 0xd800 && code <= 0xdfff)
  );
}

function hex(code: number): string {
  return `U+${code.toString(16).toUpperCase().padStart(4, '0')}`;
}

export interface Piece {
  /** A run of ordinary text, or the placeholder of one character that is shown instead of applied. */
  text: string;
  odd?: { code: number; title: string };
}

/** What stands in the text for a character that is not drawn as itself: its control picture, or its code point. */
function placeholder(code: number): string {
  if (code < 0x20) {
    return String.fromCharCode(0x2400 + code);
  }
  return code === 0x7f ? '␡' : hex(code);
}

/** Cuts a string into ordinary runs and placeholders. A surrogate pair is ordinary; half of one is not. */
export function pieces(value: string): Piece[] {
  const out: Piece[] = [];
  let run = 0;
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = value.charCodeAt(i + 1);
      if (next >= 0xdc00 && next <= 0xdfff) {
        i += 1;
        continue;
      }
    }
    if (!acts(code)) {
      continue;
    }
    if (i > run) {
      out.push({ text: value.slice(run, i) });
    }
    const name = NAMES[code];
    out.push({ text: placeholder(code), odd: { code, title: name === undefined ? hex(code) : `${hex(code)} ${name}` } });
    run = i + 1;
  }
  if (run < value.length) {
    out.push({ text: value.slice(run) });
  }
  return out;
}

/** True when the string holds a character that is shown as a placeholder. */
export function hasOdd(value: string): boolean {
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (acts(code) && !(code >= 0xd800 && code <= 0xdfff)) {
      return true;
    }
  }
  return false;
}

/** A cut never ends between the halves of a surrogate pair. */
export function cut(value: string, limit: number): string {
  if (value.length <= limit) {
    return value;
  }
  const last = value.charCodeAt(limit - 1);
  return value.slice(0, last >= 0xd800 && last <= 0xdbff ? limit - 1 : limit);
}

function draw(value: string): ReactNode {
  const parts = pieces(value);
  if (parts.length === 1 && parts[0]?.odd === undefined) {
    return value;
  }
  return parts.map((piece, i) =>
    piece.odd === undefined ? (
      piece.text
    ) : (
      <span key={i} className="odd" title={piece.odd.title}>
        {piece.text}
      </span>
    ),
  );
}

/** The longest value a table cell or a title draws. The whole value is in the event's detail. */
export const CELL_CHARS = 400;

/**
 * A value from a log, as text that cannot act. `limit` cuts what is drawn (the mark "…" says so); the rest is
 * reached where the caller says it is.
 */
export function Inert({ value, limit }: { value: string; limit?: number }) {
  const shown = limit === undefined ? value : cut(value, limit);
  return (
    <span className="inert" dir="ltr">
      {draw(shown)}
      {shown.length < value.length ? <span className="odd-more">…</span> : null}
    </span>
  );
}

/** A value for a `title`, an `aria-label` or a message: plain characters only, placeholders written out, cut short. */
export function plain(value: string, limit = CELL_CHARS): string {
  const shown = cut(value, limit);
  const text = pieces(shown)
    .map((piece) => (piece.odd === undefined ? piece.text : `[${piece.text}]`))
    .join('');
  return shown.length < value.length ? `${text}…` : text;
}

/** How much of a long value the detail draws at first, and with each further step. */
export const STEP_CHARS = 20_000;

/**
 * A long value in full, a step at a time: a line of a megabyte is one text node of twenty thousand characters
 * until the reader asks for more, so the page lays out what a person can read and no more.
 */
export function LongText({ value, label }: { value: string; label: string }) {
  const [shown, setShown] = useState(STEP_CHARS);
  const part = cut(value, shown);
  const rest = value.length - part.length;
  return (
    <>
      <pre className="raw inert" dir="ltr" aria-label={label}>
        {draw(part)}
      </pre>
      {rest > 0 ? (
        <p className="sub long-more">
          Showing the first {number(part.length)} of {number(value.length)} characters.{' '}
          <button
            type="button"
            className="linklike"
            onClick={() => {
              setShown(shown + STEP_CHARS);
            }}
          >
            Show {number(Math.min(STEP_CHARS, rest))} more
          </button>{' '}
          <button
            type="button"
            className="linklike"
            onClick={() => {
              setShown(value.length);
            }}
          >
            Show all
          </button>
          . Copy takes the whole value.
        </p>
      ) : null}
    </>
  );
}

/** Copies the value as the server sent it, placeholders or cuts notwithstanding. */
export function CopyButton({ value, label }: { value: string; label: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <>
      <button
        type="button"
        className="btn btn-ghost btn-small"
        aria-label={label}
        onClick={() => {
          void navigator.clipboard.writeText(value).then(() => {
            setCopied(true);
            window.setTimeout(() => {
              setCopied(false);
            }, 2000);
          });
        }}
      >
        {copied ? 'Copied' : 'Copy'}
      </button>
      <span className="sr-only" role="status">
        {copied ? 'Copied' : ''}
      </span>
    </>
  );
}
