import { hasOdd } from './inert';

// "Only" and "exclude" write a comparison into the query. The value comes out of a log, so it is always quoted
// by the rules of the query language, and a value the language cannot hold in one line is not offered at all.

/** The longest value a click writes into the query. */
export const MAX_TERM_VALUE = 500;

/** A name the lexer reads as one word. */
const WORD = /^[A-Za-z_][A-Za-z0-9_.@:/-]*$/;

/** Why a comparison on this field and value cannot be written, or undefined when it can. */
export function cannotFilter(field: string, value: string): string | undefined {
  if (!WORD.test(field)) {
    return 'The name of this field cannot be written in a query';
  }
  if (value.length > MAX_TERM_VALUE) {
    return `The value is longer than ${MAX_TERM_VALUE} characters`;
  }
  if (hasOdd(value)) {
    return 'The value holds characters a query cannot be typed with';
  }
  return undefined;
}

/** A value as a quoted string of the query language: a quote and a backslash are escaped, nothing else is special. */
export function quote(value: string): string {
  return `"${value.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
}

export function term(field: string, value: string, exclude: boolean): string {
  return `${field}${exclude ? '!=' : '='}${quote(value)}`;
}

/** Where the first command of the query begins: the first pipe outside a quoted string, or the end. */
function firstPipe(query: string): number {
  let quoted = false;
  for (let i = 0; i < query.length; i++) {
    const ch = query[i];
    if (quoted) {
      if (ch === '\\' && (query[i + 1] === '"' || query[i + 1] === '\\')) {
        i += 1;
      } else if (ch === '"') {
        quoted = false;
      }
    } else if (ch === '"') {
      quoted = true;
    } else if (ch === '|') {
      return i;
    }
  }
  return query.length;
}

/** The search part of a query: what stands before its first command. */
export function searchPart(query: string): string {
  return query.slice(0, firstPipe(query)).trim();
}

/** True when the name can stand in a query as it is. */
export function isWord(name: string): boolean {
  return WORD.test(name);
}

/** The query with one more comparison in its search part, before any command. */
export function addTerm(query: string, added: string): string {
  const cut = firstPipe(query);
  const search = query.slice(0, cut).trim();
  const rest = query.slice(cut).trim();
  // An OR at the top of the search would bind looser than the implicit AND of the new term.
  const head = search === '' ? added : /\bOR\b/.test(search) ? `(${search}) ${added}` : `${search} ${added}`;
  return rest === '' ? head : `${head} ${rest}`;
}
