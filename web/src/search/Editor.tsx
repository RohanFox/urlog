import { defaultKeymap, history, historyKeymap } from '@codemirror/commands';
import { EditorState, StateEffect, StateField, type Range } from '@codemirror/state';
import { Decoration, EditorView, keymap, placeholder, type DecorationSet } from '@codemirror/view';
import { useEffect, useRef } from 'react';

export interface Mark {
  offset: number;
  length: number;
  kind: 'error' | 'warning';
  message: string;
}

interface Props {
  value: string;
  marks: readonly Mark[];
  label: string;
  describedBy: string | undefined;
  onChange: (value: string) => void;
  onRun: () => void;
  /** Hands out a way to move the focus into the box. */
  focusRef: { current: (() => void) | null };
}

const setMarks = StateEffect.define<readonly Mark[]>();

/** The marks of the server over the text they were made for. Typing moves them with the text until the next answer. */
const markField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(marks, transaction) {
    let next = marks.map(transaction.changes);
    for (const effect of transaction.effects) {
      if (effect.is(setMarks)) {
        const length = transaction.state.doc.length;
        const ranges: Range<Decoration>[] = [];
        for (const mark of [...effect.value].sort((a, b) => a.offset - b.offset)) {
          const from = Math.min(mark.offset, length);
          const to = Math.min(mark.offset + Math.max(mark.length, 0), length);
          // A mark at the end of the text, or over nothing, is drawn over the last character.
          const range = to > from ? { from, to } : length > 0 ? { from: Math.max(0, Math.min(from, length) - 1), to: Math.max(1, Math.min(from, length)) } : undefined;
          if (range !== undefined) {
            ranges.push(Decoration.mark({ class: mark.kind === 'error' ? 'cm-mark-error' : 'cm-mark-warning', attributes: { title: mark.message } }).range(range.from, range.to));
          }
        }
        next = Decoration.set(ranges, true);
      }
    }
    return next;
  },
  provide: (field) => EditorView.decorations.from(field),
});

/** Every value is a token of the page: custom properties reach into the shadow tree, so the themes apply here too. */
const theme = EditorView.theme({
  '&': { color: 'var(--ink)', backgroundColor: 'transparent', fontSize: 'var(--fs-13)' },
  '&.cm-focused': { outline: 'none' },
  '.cm-scroller': { fontFamily: 'var(--data)', lineHeight: 'var(--lh)', maxHeight: 'var(--h-query)', overflow: 'auto' },
  '.cm-content': { padding: 'var(--sp-6) var(--sp-8)', caretColor: 'var(--ink)' },
  '.cm-line': { padding: '0' },
  '.cm-cursor': { borderLeftColor: 'var(--ink)' },
  '.cm-selectionBackground, &.cm-focused > .cm-scroller > .cm-selectionLayer .cm-selectionBackground, ::selection': { backgroundColor: 'var(--accentBg)' },
  '.cm-placeholder': { color: 'var(--muted)' },
  '.cm-mark-error': { textDecoration: 'underline wavy var(--err)', textDecorationSkipInk: 'none', backgroundColor: 'var(--errBg)' },
  '.cm-mark-warning': { textDecoration: 'underline wavy var(--warn)', textDecorationSkipInk: 'none' },
});

/**
 * The text of the query. The editor lives in a shadow tree: there its styles are a constructed sheet, which a
 * strict content policy allows, where in the document itself they would be an inline style element, which it
 * does not. The text is plain: the server, not this box, says what is wrong with it and where.
 */
export default function Editor({ value, marks, label, describedBy, onChange, onRun, focusRef }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const handlers = useRef({ onChange, onRun });
  useEffect(() => {
    handlers.current = { onChange, onRun };
  }, [onChange, onRun]);

  useEffect(() => {
    const el = host.current;
    if (el === null) {
      return;
    }
    const root = el.shadowRoot ?? el.attachShadow({ mode: 'open' });
    const created = new EditorView({
      root,
      parent: root,
      state: EditorState.create({
        doc: value,
        extensions: [
          history(),
          // Enter runs the search; a query is one line that may wrap.
          keymap.of([
            {
              key: 'Enter',
              run: () => {
                handlers.current.onRun();
                return true;
              },
            },
            ...defaultKeymap,
            ...historyKeymap,
          ]),
          EditorState.transactionFilter.of((transaction) => (transaction.newDoc.lines > 1 ? [] : transaction)),
          EditorView.lineWrapping,
          placeholder('Every event. Write a query to narrow: status=404 host=web-01 | stats count by url.path'),
          EditorView.contentAttributes.of({ 'aria-label': label, spellcheck: 'false', autocapitalize: 'off', autocorrect: 'off' }),
          markField,
          theme,
          EditorView.updateListener.of((update) => {
            if (update.docChanged) {
              handlers.current.onChange(update.state.doc.toString());
            }
          }),
        ],
      }),
    });
    view.current = created;
    focusRef.current = () => {
      created.focus();
    };
    return () => {
      focusRef.current = null;
      created.destroy();
      view.current = null;
    };
    // The editor is built once; the effects below carry later values into it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // A value from outside (the address changed) replaces the text; the text typed here is already there.
  useEffect(() => {
    const v = view.current;
    if (v !== null && v.state.doc.toString() !== value) {
      v.dispatch({ changes: { from: 0, to: v.state.doc.length, insert: value.replace(/[\r\n]+/g, ' ') } });
    }
  }, [value]);

  useEffect(() => {
    view.current?.dispatch({ effects: setMarks.of(marks) });
  }, [marks]);

  useEffect(() => {
    const content = view.current?.contentDOM;
    if (content === undefined) {
      return;
    }
    if (describedBy === undefined) {
      content.removeAttribute('aria-describedby');
    } else {
      // The describing element is outside the shadow tree, where an id does not reach: its text is the description.
      content.setAttribute('aria-description', document.getElementById(describedBy)?.textContent ?? '');
    }
  }, [describedBy, marks]);

  return <div ref={host} className="query-editor" />;
}
