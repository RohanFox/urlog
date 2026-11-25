import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

// The contrast of the token pairs the interface draws, in both themes, by the WCAG 2 formula:
// 4.5:1 for text and for icons that stand for text, 3:1 for the boundary of a control and for a graphic that
// carries meaning. The values are read from tokens.css, so a changed token is measured the day it changes.

const tokens = readFileSync(join(import.meta.dirname, 'tokens.css'), 'utf8');

function block(selector: string): Record<string, string> {
  const start = tokens.indexOf(`${selector} {`);
  const body = tokens.slice(start, tokens.indexOf('\n}', start));
  return Object.fromEntries([...body.matchAll(/(--[A-Za-z0-9-]+):\s*([^;]+);/g)].map((m) => [m[1] ?? '', (m[2] ?? '').trim()]));
}

const light = block(':root');
const themes: ReadonlyArray<readonly [string, Record<string, string>]> = [
  ['light', light],
  ['dark', { ...light, ...block("[data-theme='dark']") }],
];

function luminance(hex: string): number {
  expect(hex, 'a colour that takes part in a pair is written as #RRGGBB').toMatch(/^#[0-9A-Fa-f]{6}$/);
  const [r = 0, g = 0, b = 0] = [1, 3, 5].map((i) => {
    const v = parseInt(hex.slice(i, i + 2), 16) / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function ratio(a: string, b: string): number {
  const [hi = 0, lo = 0] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const TEXT = 4.5;
const BOUNDARY = 3;

interface Pair {
  on: readonly string[];
  need: number;
  use: string;
}

/** A foreground token, the backgrounds it is drawn on, and what it draws there. */
const PAIRS: Record<string, readonly Pair[]> = {
  '--ink': [{ on: ['--paper', '--surface', '--sunken', '--hover', '--accentBg', '--errBg', '--warnBg'], need: TEXT, use: 'text' }],
  '--muted': [{ on: ['--paper', '--surface', '--sunken', '--hover'], need: TEXT, use: 'secondary text, placeholders, labels' }],
  '--accent': [
    { on: ['--paper', '--surface', '--hover', '--accentBg'], need: TEXT, use: 'links, the pressed segment, the active tag, the avatar' },
    { on: ['--paper', '--surface', '--sunken'], need: BOUNDARY, use: 'the focus outline and the boundary of a focused input' },
  ],
  '--onAccent': [{ on: ['--accent', '--accentHi'], need: TEXT, use: 'the text of a primary button' }],
  '--err': [
    { on: ['--paper', '--surface', '--errBg'], need: TEXT, use: 'error text and the icon of an error banner' },
    { on: ['--surface', '--sunken', '--errBg'], need: BOUNDARY, use: 'the boundary of an invalid input and of an error banner' },
  ],
  '--warn': [{ on: ['--paper', '--surface', '--warnBg'], need: TEXT, use: 'warning text and icons' }],
  '--onErr': [{ on: ['--err'], need: TEXT, use: 'the text of an armed destructive button' }],
  '--paper': [{ on: ['--ink'], need: TEXT, use: 'the text of a toast' }],
  '--ctlLine': [{ on: ['--paper', '--surface', '--sunken'], need: BOUNDARY, use: 'the boundary of a button, an input, a tag and the segmented control' }],
  '--brand': [{ on: ['--paper', '--surface'], need: BOUNDARY, use: 'the diamond of the logo' }],
  '--barI': [{ on: ['--surface'], need: BOUNDARY, use: 'the info bars of the histogram' }],
};

/**
 * Colour tokens that are never the foreground of text, of an icon, of a control's boundary or of a graphic
 * that carries meaning, and are therefore held to no ratio. Each says why.
 */
const EXEMPT: Record<string, string> = {
  '--line': 'a divider between rows and sections: decoration, the content is told apart by its layout',
  '--line2': 'a divider and the edge of a banner, a menu or a dialog: decoration, never the only boundary of a control',
  '--ring': 'the halo around a focused input: it adds to the accent boundary, which is measured',
  '--scrim': 'the veil behind a dialog',
  '--shadow': 'the shadow of a dialog and of a menu',
};

/** Backgrounds: measured as the second half of every pair above, never drawn on top of anything themselves. */
const BACKGROUNDS = ['--surface', '--sunken', '--hover', '--accentBg', '--accentHi', '--errBg', '--warnBg'];

describe('the contrast of the design tokens', () => {
  for (const [name, theme] of themes) {
    for (const [foreground, pairs] of Object.entries(PAIRS)) {
      for (const pair of pairs) {
        for (const background of pair.on) {
          it(`${name}: ${foreground} on ${background} is at least ${pair.need}:1 (${pair.use})`, () => {
            const measured = ratio(theme[foreground] ?? '', theme[background] ?? '');
            expect(measured, `${theme[foreground]} on ${theme[background]} measures ${measured.toFixed(2)}:1`).toBeGreaterThanOrEqual(pair.need);
          });
        }
      }
    }
  }

  it('places every colour token: in a measured pair, as a background, or exempt with a reason', () => {
    const colours = Object.entries(light)
      .filter(([, value]) => /^#[0-9A-Fa-f]{6}$|rgba\(/.test(value))
      .map(([token]) => token)
      .sort();
    const placed = new Set([...Object.keys(PAIRS), ...Object.keys(EXEMPT), ...BACKGROUNDS]);
    expect(colours.filter((token) => !placed.has(token))).toEqual([]);
    // Nothing is both measured and exempt, and no exemption outlives its token.
    expect(Object.keys(EXEMPT).filter((token) => token in PAIRS || !colours.includes(token))).toEqual([]);
    for (const reason of Object.values(EXEMPT)) {
      expect(reason.length).toBeGreaterThan(10);
    }
  });

  it('draws every series of the timeline in a colour that is measured on the surface it stands on', () => {
    const series = Object.entries(light).filter(([token]) => token.startsWith('--sev-'));
    expect(series.map(([token]) => token).sort()).toEqual(['--sev-error', '--sev-info', '--sev-none', '--sev-warning']);
    for (const [token, value] of series) {
      const of = /^var\((--[A-Za-z0-9-]+)\)$/.exec(value)?.[1] ?? '';
      expect(PAIRS[of]?.some((pair) => pair.on.includes('--surface') && pair.need >= BOUNDARY), `${token} is ${value}`).toBe(true);
    }
  });

  it('measures a background only where something is drawn on it', () => {
    const used = new Set(Object.values(PAIRS).flatMap((pairs) => pairs.flatMap((pair) => pair.on)));
    expect(BACKGROUNDS.filter((token) => !used.has(token))).toEqual([]);
  });
});
