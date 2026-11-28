import { describe, expect, it } from 'vitest';
import { bannerLines, held, nearestName, notesUnderBox, panelReadings, readingLine, termReading, tokens, type FieldsOfCase } from './readings';
import type { Term, Warning } from './stream';

function term(text: string, kind: string, negated = false): Term {
  return { text, kind, negated, offset: 0, length: text.length };
}

function warning(code: string, level: Warning['level'], offset: number, length: number, message: string): Warning {
  return { code, level, offset, length, message };
}

const COUNTED: FieldsOfCase = {
  statistics: { state: 'ok', pending: false },
  fields: [
    { name: 'http.response.status_code', aliases: ['status'], events: 650 },
    { name: 'process.pid', aliases: ['pid'], events: 0 },
    { name: 'raw', aliases: [], events: null },
    { name: 'host.name', aliases: ['host'], events: 650 },
  ],
  extras_keys: [
    { key: 'request_id', events: 12 },
    { key: 'upstream', events: 3 },
  ],
};

const DIRTY: FieldsOfCase = { ...COUNTED, statistics: { state: 'dirty', pending: true } };
const MISSING: FieldsOfCase = { statistics: { state: 'missing', pending: false }, fields: COUNTED.fields.map((f) => ({ ...f, events: null })), extras_keys: [] };

describe('the tokens of a term', () => {
  it('cuts at ASCII punctuation, lowercases ASCII and keeps other letters as they are', () => {
    expect(tokens('/Shop/item/64')).toEqual(['shop', 'item', '64']);
    expect(tokens('10.0.0.5')).toEqual(['10', '0', '0', '5']);
    expect(tokens('-')).toEqual([]);
    expect(tokens('Ünï-X')).toEqual(['Ünï', 'x']);
  });
});

describe('a term in words', () => {
  it('says a bare word is a whole word of the message', () => {
    expect(termReading(term('curl', 'word'))).toBe('the word curl, whole word, in the message only');
  });

  it('says of a phrase and of a word with punctuation which whole words it needs', () => {
    expect(termReading(term('item/64', 'phrase'))).toBe('the text item/64 with item and 64 as whole words, in the message only');
    expect(termReading(term('-enc', 'word'))).toBe('the text -enc with enc as a whole word, in the message only');
    expect(termReading(term('10.0.0.5', 'word'))).toBe('the text 10.0.0.5 with 10, 0 and 5 as whole words, in the message only');
  });

  it('says a pattern and a term without a letter are compared as written', () => {
    expect(termReading(term('cur*', 'wildcard'))).toBe('the pattern cur* (* is any text, ? one character), in the message only');
    expect(termReading(term('-', 'text'))).toBe('the text -, as written, in the message only');
  });

  it('marks a term under NOT, and reads a kind it does not know as text', () => {
    expect(termReading(term('curl', 'word', true))).toBe('NOT: the word curl, whole word, in the message only');
    expect(termReading(term('curl', 'later'))).toBe('the text curl with curl as a whole word, in the message only');
  });

  it('shows what a term holds through the inert writer', () => {
    expect(termReading(term('a‮b', 'word'))).toContain('[U+202E]');
    expect(termReading(term('a‮b', 'word'))).not.toContain('‮');
  });
});

describe('the reading line', () => {
  it('is absent without free text', () => {
    expect(readingLine([])).toBeUndefined();
  });

  it('names the terms, a phrase in its quotes', () => {
    expect(readingLine([term('curl', 'word'), term('item/64', 'phrase')])).toBe('Free text is searched in the message, by whole words: curl, "item/64". For the whole line write raw=*curl* raw=*item/64*');
  });

  it('writes out what a NOT leaves out', () => {
    expect(readingLine([term('curl', 'word', true)])).toBe(
      'Free text is searched in the message, by whole words. NOT curl leaves out the events whose message holds the word curl. For the whole line write NOT raw=*curl*',
    );
    expect(readingLine([term('GET', 'word'), term('item/64', 'phrase', true)])).toBe(
      'Free text is searched in the message, by whole words: GET. NOT "item/64" leaves out the events whose message holds the text item/64. For the whole line write raw=*GET* NOT raw=*item/64*',
    );
  });

  it('ends with the search of the whole line in the term the analyst wrote', () => {
    expect(readingLine([term('curl', 'word')])).toBe('Free text is searched in the message, by whole words: curl. For the whole line write raw=*curl*');
    // A pattern keeps its own wildcards and gains none at its ends twice.
    expect(readingLine([term('cur*', 'wildcard')])).toMatch(/For the whole line write raw=\*cur\*$/);
    expect(readingLine([term('*a?b*', 'wildcard')])).toMatch(/For the whole line write raw=\*a\?b\*$/);
  });

  it('names the form alone when a term cannot stand in a wildcard value', () => {
    for (const text of ['two words', 'a=b', 'say "x"', '(x)', 'a|b', 'a,b', 'a\u202eb', '*']) {
      expect(readingLine([term(text, 'phrase')])).toMatch(/For the whole line write raw=\*text\*$/);
    }
    // One term that cannot be written leaves the form for all: half a query would find other events.
    expect(readingLine([term('curl', 'word'), term('two words', 'phrase')])).toMatch(/For the whole line write raw=\*text\*$/);
  });

  it('writes the whole line out for three terms at most', () => {
    const many = Array.from({ length: 5 }, (_, i) => term(`w${i}`, 'word'));
    expect(readingLine(many)).toMatch(/For the whole line write raw=\*w0\* raw=\*w1\* raw=\*w2\*$/);
  });

  it('counts the terms it does not name', () => {
    const many = Array.from({ length: 9 }, (_, i) => term(`w${i}`, 'word'));
    expect(readingLine(many)).toBe('Free text is searched in the message, by whole words: w0, w1, w2, w3, w4, w5 and 3 more. For the whole line write raw=*w0* raw=*w1* raw=*w2*');
  });
});

describe('what the case holds of a name', () => {
  it('knows a catalog name, an alias and a key outside the catalog', () => {
    expect(held('status', COUNTED, false)).toBe('held');
    expect(held('HTTP.Response.Status_Code', COUNTED, false)).toBe('held');
    expect(held('request_id', COUNTED, false)).toBe('held');
    expect(held('pid', COUNTED, false)).toBe('absent');
    expect(held('statsu', COUNTED, false)).toBe('absent');
  });

  it('says nothing of a field the statistics never count', () => {
    expect(held('raw', COUNTED, false)).toBe('held');
  });

  it('does not say "none" while the statistics are missing or dirty, unless the last run counted events', () => {
    expect(held('statsu', MISSING, false)).toBe('uncounted');
    expect(held('status', MISSING, false)).toBe('uncounted');
    expect(held('statsu', DIRTY, false)).toBe('uncounted');
    expect(held('request_id', DIRTY, false)).toBe('held');
  });

  it('waits for the fields, and calls them uncounted when they could not be listed', () => {
    expect(held('statsu', undefined, false)).toBe('loading');
    expect(held('statsu', undefined, true)).toBe('uncounted');
  });
});

describe('the nearest name', () => {
  it('finds a name one or two edits away, among names, aliases and keys of the case', () => {
    expect(nearestName('statsu', COUNTED)).toBe('status');
    expect(nearestName('request_ip', COUNTED)).toBe('request_id');
    expect(nearestName('hots', COUNTED)).toBeUndefined();
    expect(nearestName('hosts', COUNTED)).toBe('host');
  });

  it('offers nothing for a name that is far from every other, or is one', () => {
    expect(nearestName('zzzzzz', COUNTED)).toBeUndefined();
    expect(nearestName('status', COUNTED)).toBeUndefined();
    expect(nearestName('statsu', undefined)).toBeUndefined();
  });

  it('breaks a tie by the closest, then the shortest, then the alphabet', () => {
    const tied: FieldsOfCase = { ...COUNTED, fields: [{ name: 'abcdef', aliases: ['abcdeg', 'abcde'], events: 1 }], extras_keys: [{ key: 'abcdxx', events: 1 }] };
    // abcdeh: abcdef and abcdeg are one edit away and as long; abcde is one edit away and shorter.
    expect(nearestName('abcdeh', tied)).toBe('abcde');
    const two: FieldsOfCase = { ...COUNTED, fields: [{ name: 'abcdeg', aliases: ['abcdef'], events: 1 }], extras_keys: [] };
    expect(nearestName('abcdeh', two)).toBe('abcdef');
  });

  it('allows one edit only for a short name', () => {
    const short: FieldsOfCase = { ...COUNTED, fields: [{ name: 'pid', aliases: ['uid'], events: 1 }], extras_keys: [] };
    expect(nearestName('pdi', short)).toBeUndefined();
    expect(nearestName('pidd', short)).toBe('pid');
  });

  it('never offers a key outside the catalog that no event has', () => {
    const empty: FieldsOfCase = { ...COUNTED, fields: [], extras_keys: [{ key: 'statsx', events: 0 }] };
    expect(nearestName('statsu', empty)).toBeUndefined();
  });
});

describe('the banner', () => {
  const query = 'status=200 OR statsu=404';
  const unknown = warning('unknown_field', 'meaning', 14, 6, "'statsu' is not in the catalog: searched as extras['statsu']");
  const cost = warning('unindexed', 'cost', 14, 10, 'statsu lives in extras: compared without an index');

  it('names a field no event of the case has in plain words, with what it did to the comparison and the nearest name', () => {
    expect(bannerLines(query, [unknown, cost], COUNTED, false)).toEqual([
      { text: 'statsu is not a known field, and no event of this case has a field of that name, so statsu=404 matched nothing. Did you mean status?' },
    ]);
    // Without a comparison of its own (a by field, a column of table) the sentence stops at the name.
    expect(bannerLines(query, [unknown], COUNTED, false)).toEqual([{ text: 'statsu is not a known field, and no event of this case has a field of that name. Did you mean status?' }]);
    expect(JSON.stringify(bannerLines(query, [unknown, cost], COUNTED, false))).not.toMatch(/catalog|extras/);
  });

  it('leaves the note of cost for the same comparison out from under the box, and no other', () => {
    const other = warning('unindexed', 'cost', 0, 10, 'a wildcard term scans message without the index');
    expect(notesUnderBox(query, [other, unknown, cost], COUNTED, false)).toEqual([other]);
    expect(notesUnderBox(query, [unknown, cost], MISSING, false)).toEqual([]);
  });

  it('keeps both notes under the box, in the words of the server, for a key the case holds', () => {
    const own = warning('unknown_field', 'meaning', 0, 10, "'request_id' is not in the catalog: searched as extras['request_id']");
    const ownCost = warning('unindexed', 'cost', 0, 12, 'request_id lives in extras: compared without an index');
    expect(notesUnderBox('request_id=5', [own, ownCost], COUNTED, false)).toEqual([own, ownCost]);
    // While the fields load the name is in neither place; its note of cost stays.
    expect(notesUnderBox('request_id=5', [own, ownCost], undefined, false)).toEqual([ownCost]);
  });

  it('lists no other reading under the box', () => {
    const minus = warning('minus_term', 'meaning', 0, 4, "'-enc' is searched as text, minus included; to exclude a word write NOT enc");
    expect(notesUnderBox('-enc', [minus], COUNTED, false)).toEqual([]);
  });

  it('is silent for a key the events of the case do have, and while the fields load', () => {
    const own = warning('unknown_field', 'meaning', 0, 10, "'request_id' is not in the catalog: searched as extras['request_id']");
    expect(bannerLines('request_id=5', [own], COUNTED, false)).toEqual([]);
    expect(bannerLines(query, [unknown], undefined, false)).toEqual([]);
  });

  it('says "not counted yet" in place of "no event has it" when the statistics are missing, dirty or unread', () => {
    for (const fields of [MISSING, DIRTY, undefined]) {
      const lines = bannerLines(query, [unknown], fields, true);
      expect(lines).toHaveLength(1);
      // The nearest name comes from the field listing, so there is none when it could not be read.
      expect(lines[0]?.text).toBe(`statsu is not a known field, and the fields of this case are not counted yet.${fields === undefined ? '' : ' Did you mean status?'}`);
    }
  });

  it('carries every other reading with the piece of the query it is about', () => {
    const lines = bannerLines('select LIKE %admin%', [warning('like_term', 'meaning', 7, 4, 'LIKE is searched as a word; a comparison with wildcards is written field=*text*'), warning('percent_term', 'meaning', 12, 7, '% is searched as a character; the wildcard is *: write *admin*')], COUNTED, false);
    expect(lines.map((l) => l.piece)).toEqual(['LIKE', '%admin%']);
  });

  it('writes the piece of the query and the words of the server through the inert writer', () => {
    const lines = bannerLines('-a‮b', [warning('minus_term', 'meaning', 0, 4, "'-a‮b' is searched as text")], COUNTED, false);
    expect(JSON.stringify(lines)).not.toContain('‮');
    expect(lines[0]?.piece).toBe('-a[U+202E]b');
  });
});

describe('the readings of a query that matched nothing', () => {
  it('lists what may not have been meant, then the fields no event has, then each term', () => {
    const lines = panelReadings(
      'statsu=404 pid=1 status=200 NOT curl',
      [warning('unknown_field', 'meaning', 0, 6, "'statsu' is not in the catalog: searched as extras['statsu']"), warning('unindexed', 'cost', 0, 10, 'statsu lives in extras: compared without an index')],
      ['statsu', 'process.pid', 'http.response.status_code'],
      [term('curl', 'word', true)],
      COUNTED,
      false,
    );
    expect(lines).toEqual([
      { text: 'statsu is not a known field, and no event of this case has a field of that name, so statsu=404 matched nothing. Did you mean status?' },
      { piece: 'process.pid', text: 'No event of this case has this field.' },
      { text: 'NOT: the word curl, whole word, in the message only' },
    ]);
  });

  it('says "not counted yet" of a catalog field when the statistics are dirty', () => {
    expect(panelReadings('pid=1', [], ['process.pid'], [], DIRTY, false)).toEqual([{ piece: 'process.pid', text: 'The events of this case with this field are not counted yet.' }]);
  });

  it('lists a key of the case too: with no event matched, every reading is worth a line', () => {
    const own = warning('unknown_field', 'meaning', 0, 10, "'request_id' is not in the catalog: searched as extras['request_id']");
    expect(panelReadings('request_id=5', [own], ['request_id'], [], COUNTED, false)).toEqual([{ text: 'request_id is not a known field; events of this case do have a field of that name.' }]);
  });

  it('is empty for a query of comparisons on fields the case has', () => {
    expect(panelReadings('status=500', [], ['http.response.status_code'], [], COUNTED, false)).toEqual([]);
  });
});
