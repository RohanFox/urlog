import { hasOdd, plain } from './inert';
import type { Term, Warning } from './stream';

// How the server read a query, in sentences. The server sends what it did (warnings with a level, the terms it
// searched as text); this module writes it out and looks the query's fields up in what the case holds. Every
// piece of the query that reaches a sentence goes through plain().

/** The part of the answer of the fields route this module reads. */
export interface FieldsOfCase {
  statistics: { state: string; pending: boolean };
  fields: ReadonlyArray<{ name: string; aliases: readonly string[]; events: number | null }>;
  extras_keys: ReadonlyArray<{ key: string; events: number }>;
}

/** What the case holds of a field: events with it, none, not counted yet, or not known while the fields load. */
export type Held = 'held' | 'absent' | 'uncounted' | 'loading';

/** One line of the banner or of the empty panel: the piece of the query it is about, when there is one, and the sentence. */
export interface Reading {
  piece?: string;
  text: string;
}

const PIECE = 120;

/** The tokens the text index cuts: runs of characters that are not ASCII punctuation or whitespace, lowercased. */
export function tokens(text: string): string[] {
  const out: string[] = [];
  let run = '';
  for (const ch of text) {
    const code = ch.codePointAt(0) ?? 0;
    const separator = code < 0x80 && !/[A-Za-z0-9]/.test(ch);
    if (separator) {
      if (run !== '') {
        out.push(run);
        run = '';
      }
    } else {
      run += code < 0x80 ? ch.toLowerCase() : ch;
    }
  }
  if (run !== '') {
    out.push(run);
  }
  return out;
}

function list(words: string[]): string {
  const shown = words.slice(0, 6).map((w) => plain(w, PIECE));
  const more = words.length - shown.length;
  const head = shown.length <= 1 ? shown.join('') : `${shown.slice(0, -1).join(', ')} and ${shown[shown.length - 1] ?? ''}`;
  return more > 0 ? `${head} (and ${more} more)` : head;
}

/** A term in a few words: "the word curl", "the text item/64", "the pattern cur*". */
export function nameOf(term: Term): string {
  const text = plain(term.text, PIECE);
  if (term.kind === 'wildcard') {
    return `the pattern ${text}`;
  }
  const cut = tokens(term.text);
  return term.kind === 'word' && cut.length === 1 && cut[0]?.length === term.text.length ? `the word ${text}` : `the text ${text}`;
}

/** A term as the empty panel says it: what was searched, how, and where. */
export function termReading(term: Term): string {
  const not = term.negated ? 'NOT: ' : '';
  const name = nameOf(term);
  if (term.kind === 'wildcard') {
    return `${not}${name} (* is any text, ? one character), in the message only`;
  }
  const cut = [...new Set(tokens(term.text))];
  if (cut.length === 0) {
    return `${not}${name}, as written, in the message only`;
  }
  if (name.startsWith('the word ')) {
    return `${not}${name}, whole word, in the message only`;
  }
  return `${not}${name} with ${list(cut)} as ${cut.length === 1 ? 'a whole word' : 'whole words'}, in the message only`;
}

function written(term: Term): string {
  const text = plain(term.text, PIECE);
  return term.kind === 'phrase' ? `"${text}"` : text;
}

/** How many terms the reading line names before it counts the rest. */
const LINE_TERMS = 6;

/** How many terms the way to the whole line is written out for. */
const WHOLE_LINE_TERMS = 3;

/**
 * The term as a comparison on the stored line, or undefined when it cannot be written as one: a wildcard value
 * stands without quotes, so it cannot hold a space or a character the query language reads as its own.
 */
function onTheWholeLine(term: Term): string | undefined {
  const core = term.text.replace(/^\*+|\*+$/g, '');
  if (core === '' || core.length > PIECE || /[\s|(),"=<>!]/.test(core) || hasOdd(core)) {
    return undefined;
  }
  return `${term.negated ? 'NOT ' : ''}raw=*${core}*`;
}

/** The end of the reading line: the search of the whole line, with the analyst's own terms where they can be written. */
function wholeLine(terms: readonly Term[]): string {
  const own = terms.slice(0, WHOLE_LINE_TERMS).map(onTheWholeLine);
  return `For the whole line write ${own.length > 0 && own.every((t) => t !== undefined) ? own.join(' ') : 'raw=*text*'}`;
}

/**
 * The muted line under the query: where free text was searched and how, with each term under NOT written out,
 * and how to search the whole line instead. Undefined when the query searched no free text.
 */
export function readingLine(terms: readonly Term[]): string | undefined {
  if (terms.length === 0) {
    return undefined;
  }
  const plainTerms = terms.filter((t) => !t.negated);
  const negated = terms.filter((t) => t.negated);
  const parts: string[] = [];
  if (plainTerms.length === 0) {
    parts.push('Free text is searched in the message, by whole words');
  } else {
    const shown = plainTerms.slice(0, LINE_TERMS).map(written).join(', ');
    const more = plainTerms.length - Math.min(plainTerms.length, LINE_TERMS);
    parts.push(`Free text is searched in the message, by whole words: ${shown}${more > 0 ? ` and ${more} more` : ''}`);
  }
  for (const term of negated.slice(0, LINE_TERMS)) {
    parts.push(`NOT ${written(term)} leaves out the events whose message holds ${nameOf(term)}`);
  }
  if (negated.length > LINE_TERMS) {
    parts.push(`${negated.length - LINE_TERMS} more under NOT`);
  }
  parts.push(wholeLine(terms));
  return parts.join('. ');
}

/** What the case holds of a name the query used: a catalog name, an alias, or a key outside the catalog. */
export function held(name: string, fields: FieldsOfCase | undefined, failed: boolean): Held {
  if (fields === undefined) {
    return failed ? 'uncounted' : 'loading';
  }
  const lower = name.toLowerCase();
  const listed = fields.fields.find((f) => f.name.toLowerCase() === lower || f.aliases.some((a) => a.toLowerCase() === lower));
  const events = listed !== undefined ? listed.events : (fields.extras_keys.find((k) => k.key === name)?.events ?? 0);
  if (events !== null && events > 0) {
    return 'held';
  }
  const counted = fields.statistics.state === 'ok' && !fields.statistics.pending;
  if (counted && events === null) {
    // A field the statistics never count (the line itself, its time): nothing to say about it.
    return 'held';
  }
  return counted ? 'absent' : 'uncounted';
}

function distance(a: string, b: string, limit: number): number {
  if (Math.abs(a.length - b.length) > limit) {
    return limit + 1;
  }
  let before = Array.from({ length: b.length + 1 }, (_, i) => i);
  for (let i = 1; i <= a.length; i++) {
    const row = [i];
    for (let j = 1; j <= b.length; j++) {
      row.push(Math.min((before[j] ?? 0) + 1, (row[j - 1] ?? 0) + 1, (before[j - 1] ?? 0) + (a[i - 1] === b[j - 1] ? 0 : 1)));
    }
    before = row;
  }
  return before[b.length] ?? limit + 1;
}

/**
 * The name the analyst may have meant: a catalog name, an alias or a key of the case one or two edits away
 * (one for a name shorter than five characters), the closest first, then the shortest, then by the alphabet.
 * The candidates come from the catalog and the field listing, never from the text of a log line.
 */
export function nearestName(name: string, fields: FieldsOfCase | undefined): string | undefined {
  if (fields === undefined || name.length < 2) {
    return undefined;
  }
  const limit = name.length < 5 ? 1 : 2;
  const lower = name.toLowerCase();
  const candidates = [...fields.fields.flatMap((f) => [f.name, ...f.aliases]), ...fields.extras_keys.filter((k) => k.events > 0).map((k) => k.key)];
  let best: { name: string; d: number } | undefined;
  for (const candidate of candidates) {
    const d = distance(lower, candidate.toLowerCase(), limit);
    if (d === 0 || d > limit) {
      continue;
    }
    if (best === undefined || d < best.d || (d === best.d && (candidate.length < best.name.length || (candidate.length === best.name.length && candidate < best.name)))) {
      best = { name: candidate, d };
    }
  }
  return best?.name;
}

function pieceOf(query: string, w: { offset: number; length: number }): string {
  return plain(query.slice(w.offset, w.offset + w.length), PIECE);
}

/** The note of cost the server raises for the comparison a name outside the catalog stands in: it begins where the name does. */
function comparisonOf(w: Warning, warnings: readonly Warning[]): Warning | undefined {
  return warnings.find((other) => other.level === 'cost' && other.offset === w.offset && other.length > w.length);
}

/**
 * A name the server does not know, in the words of someone who mistyped a field: whether the case has events
 * with a field of that name, what that did to the comparison, and the name that may have been meant.
 */
function unknownField(query: string, w: Warning, warnings: readonly Warning[], fields: FieldsOfCase | undefined, failed: boolean): { state: Held; reading: Reading } {
  const raw = query.slice(w.offset, w.offset + w.length);
  const name = plain(raw, PIECE);
  const state = held(raw, fields, failed);
  const comparison = comparisonOf(w, warnings);
  let text: string;
  if (state === 'absent') {
    text = `${name} is not a known field, and no event of this case has a field of that name${comparison === undefined ? '' : `, so ${pieceOf(query, comparison)} matched nothing`}.`;
  } else if (state === 'uncounted') {
    text = `${name} is not a known field, and the fields of this case are not counted yet.`;
  } else if (state === 'held') {
    text = `${name} is not a known field; events of this case do have a field of that name.`;
  } else {
    text = `${name} is not a known field.`;
  }
  const near = state === 'held' ? undefined : nearestName(raw, fields);
  return { state, reading: { text: near === undefined ? text : `${text} Did you mean ${plain(near, PIECE)}?` } };
}

/** True for a name outside the catalog that the banner speaks of: no event of the case has it, or none is counted. */
function inBanner(state: Held): boolean {
  return state === 'absent' || state === 'uncounted';
}

/**
 * The lines of the banner: every warning of the level "meaning" of the text that ran. A name outside the
 * catalog that events of the case do have is a key of theirs, not a slip, and stays a note under the box; while
 * the fields of the case are still loading it is left out, so the banner does not come and go.
 */
export function bannerLines(query: string, warnings: readonly Warning[], fields: FieldsOfCase | undefined, failed: boolean): Reading[] {
  const out: Reading[] = [];
  for (const w of warnings) {
    if (w.level !== 'meaning') {
      continue;
    }
    if (w.code === 'unknown_field') {
      const { state, reading } = unknownField(query, w, warnings, fields, failed);
      if (inBanner(state)) {
        out.push(reading);
      }
    } else {
      out.push({ piece: pieceOf(query, w), text: plain(w.message, 600) });
    }
  }
  return out;
}

/**
 * The notes under the query box: the warnings of cost, and a name outside the catalog that events of the case
 * do have, in the words of the server. When the banner says of a name that no event has it, the note that its
 * comparison ran without an index says nothing more and is left out.
 */
export function notesUnderBox(query: string, warnings: readonly Warning[], fields: FieldsOfCase | undefined, failed: boolean): Warning[] {
  const unknown = warnings.filter((w) => w.level === 'meaning' && w.code === 'unknown_field').map((w) => ({ w, state: held(query.slice(w.offset, w.offset + w.length), fields, failed) }));
  const said = new Set(unknown.filter((u) => inBanner(u.state)).map((u) => comparisonOf(u.w, warnings)));
  return warnings.filter((w) => (w.level === 'cost' ? !said.has(w) : unknown.some((u) => u.w === w && u.state === 'held')));
}

/**
 * Every reading of a query that matched nothing, in the order a slip is likely: what may not have been meant,
 * the fields no event of the case has, then each term of free text.
 */
export function panelReadings(query: string, warnings: readonly Warning[], used: readonly string[], terms: readonly Term[], fields: FieldsOfCase | undefined, failed: boolean): Reading[] {
  const out: Reading[] = [];
  const unknown = new Set<string>();
  for (const w of warnings) {
    if (w.level !== 'meaning') {
      continue;
    }
    if (w.code === 'unknown_field') {
      unknown.add(query.slice(w.offset, w.offset + w.length));
      out.push(unknownField(query, w, warnings, fields, failed).reading);
    } else {
      out.push({ piece: pieceOf(query, w), text: plain(w.message, 600) });
    }
  }
  for (const name of used) {
    if (unknown.has(name)) {
      continue;
    }
    const state = held(name, fields, failed);
    if (state === 'absent') {
      out.push({ piece: plain(name, PIECE), text: 'No event of this case has this field.' });
    } else if (state === 'uncounted' && fields !== undefined) {
      out.push({ piece: plain(name, PIECE), text: 'The events of this case with this field are not counted yet.' });
    }
  }
  for (const term of terms) {
    out.push({ text: termReading(term) });
  }
  return out;
}
