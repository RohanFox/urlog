import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { CopyButton, cut, hasOdd, Inert, LongText, pieces, plain, STEP_CHARS } from './inert';

// Lines of a log are written by whoever was attacked or attacking. Each of these is drawn as the text it is.
const HOSTILE = [
  '<script>window.pwned = 1</script>',
  '<img src=x onerror="window.pwned = 1">',
  '<a href="javascript:window.pwned=1">click</a>',
  '<svg onload=window.pwned=1>',
  '"><iframe srcdoc="<script>window.pwned=1</script>">',
  '&lt;b&gt;already escaped&lt;/b&gt; &amp; &#x3c;script&#x3e;',
  '{{constructor.constructor("window.pwned=1")()}}',
  'https://example.test/?q=<script>window.pwned=1</script>',
];

describe('text out of a log', () => {
  it.each(HOSTILE)('draws %s as text, character for character, and builds no element from it', (value) => {
    const { container } = render(<Inert value={value} />);
    expect(container.textContent).toBe(value);
    // The one element is the span that holds the text.
    expect(container.querySelectorAll('script, img, a, svg, iframe, b')).toHaveLength(0);
    expect(container.querySelectorAll('*')).toHaveLength(1);
    expect((window as unknown as { pwned?: number }).pwned).toBeUndefined();
  });

  it('makes no link of an address', () => {
    const { container } = render(<Inert value="see http://evil.example/ and javascript:alert(1)" />);
    expect(container.querySelector('a')).toBeNull();
  });

  it('shows a control character as its picture and never passes it on', () => {
    const { container } = render(<Inert value={'a\u0000b\u001b[31mred\u0007'} />);
    const marks = [...container.querySelectorAll('.odd')];
    expect(marks.map((m) => m.textContent)).toEqual(['␀', '␛', '␇']);
    expect(marks.map((m) => m.getAttribute('title'))).toEqual(['U+0000 NUL', 'U+001B ESCAPE', 'U+0007 BEL']);
    expect(container.textContent).toBe('a␀b␛[31mred␇');
    expect(container.textContent).not.toContain('\u001b');
  });

  it('shows an override of the direction as its code point, so the text reads in the order it was written', () => {
    const value = 'invoice‮fdp.exe';
    const { container } = render(<Inert value={value} />);
    expect(container.textContent).toBe('invoiceU+202Efdp.exe');
    expect(container.querySelector('.odd')?.getAttribute('title')).toBe('U+202E RIGHT-TO-LEFT OVERRIDE');
    expect(container.textContent).not.toContain('‮');
    expect(container.querySelector('.inert')?.getAttribute('dir')).toBe('ltr');
  });

  it.each([
    ['​', 'U+200B'],
    ['‍', 'U+200D'],
    ['⁦', 'U+2066'],
    ['⁩', 'U+2069'],
    ['﻿', 'U+FEFF'],
    ['­', 'U+00AD'],
    [' ', 'U+2028'],
    ['\u0085', 'U+0085'],
    ['\u007F', '␡'],
    ['\n', '␊'],
    ['\r', '␍'],
  ])('shows the invisible character %j as %s', (char, shown) => {
    expect(pieces(`a${char}b`).map((p) => p.text)).toEqual(['a', shown, 'b']);
    expect(hasOdd(`a${char}b`)).toBe(true);
  });

  it('leaves ordinary text alone: a tab, other scripts, an emoji as one character', () => {
    for (const value of ['plain', 'tab\there', 'ţară șir', 'שלום', 'مرحبا', 'log 🔥 line']) {
      expect(pieces(value)).toEqual([{ text: value }]);
      expect(hasOdd(value)).toBe(false);
    }
  });

  it('shows half of a surrogate pair as its code point', () => {
    expect(pieces('a\uD83Db').map((p) => p.text)).toEqual(['a', 'U+D83D', 'b']);
  });

  it('cuts a long value for a cell, says so, and never between the halves of one character', () => {
    render(<Inert value={'x'.repeat(1000)} limit={400} />);
    expect(screen.getByText('…')).toBeInTheDocument();
    expect(cut('ab🔥cd', 3)).toBe('ab');
    expect(cut('ab🔥cd', 4)).toBe('ab🔥');
    expect(cut('short', 400)).toBe('short');
  });

  it('writes a value for a title or a message as plain characters, placeholders spelled out', () => {
    expect(plain('a\u0000b‮c')).toBe('a[␀]b[U+202E]c');
    expect(plain('x'.repeat(500), 10)).toBe(`${'x'.repeat(10)}…`);
  });
});

describe('a very long line', () => {
  const line = 'A'.repeat(1024 * 1024);

  it('is drawn twenty thousand characters at a time, with the size said and a way to the rest', async () => {
    const user = userEvent.setup();
    const started = performance.now();
    const { container } = render(<LongText value={line} label="Raw line" />);
    expect(performance.now() - started).toBeLessThan(1000);

    const raw = container.querySelector('pre');
    expect(raw?.textContent).toHaveLength(STEP_CHARS);
    expect(screen.getByText(/Showing the first 20,000 of 1,048,576 characters/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Show 20,000 more' }));
    expect(raw?.textContent).toHaveLength(2 * STEP_CHARS);

    await user.click(screen.getByRole('button', { name: 'Show all' }));
    expect(raw?.textContent).toHaveLength(line.length);
    expect(screen.queryByRole('button', { name: 'Show all' })).toBeNull();
  });

  it('is copied whole, as the server sent it, whatever is drawn', async () => {
    const user = userEvent.setup();
    const original = `start\u0000‮${line}`;
    const writeText = vi.fn(() => Promise.resolve());
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    render(<CopyButton value={original} label="Copy the whole raw line" />);

    await user.click(screen.getByRole('button', { name: 'Copy the whole raw line' }));
    expect(writeText).toHaveBeenCalledWith(original);
    expect(await screen.findByRole('status')).toHaveTextContent('Copied');
    vi.unstubAllGlobals();
  });
});
