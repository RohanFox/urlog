import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

const SRC = join(import.meta.dirname, '..');
const tokens = readFileSync(join(SRC, 'styles', 'tokens.css'), 'utf8');

function block(selector: string): Map<string, string> {
  const start = tokens.indexOf(`${selector} {`);
  expect(start, `${selector} in tokens.css`).toBeGreaterThanOrEqual(0);
  const body = tokens.slice(start, tokens.indexOf('\n}', start));
  return new Map([...body.matchAll(/(--[A-Za-z0-9-]+):\s*([^;]+);/g)].map((m) => [m[1] ?? '', (m[2] ?? '').trim()]));
}

function files(dir: string, ext: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path, ext) : path.endsWith(ext) ? [path] : [];
  });
}

const COLOUR = /^(#[0-9A-Fa-f]{6}|rgba\(.+\)|0 \d+px \d+px rgba\(.+\))$/;

describe('the design tokens', () => {
  const light = block(':root');
  const dark = block("[data-theme='dark']");

  it('give the dark theme a value for every colour of the light one, and nothing else', () => {
    const colours = [...light].filter(([, value]) => COLOUR.test(value)).map(([name]) => name);
    expect(colours.length).toBeGreaterThan(15);
    expect([...dark.keys()].sort()).toEqual(colours.sort());
    for (const [name, value] of dark) {
      expect(value, name).toMatch(COLOUR);
    }
  });

  it('name two font families, both shipped with the app', () => {
    expect(light.get('--ui')).toContain("'IBM Plex Sans'");
    expect(light.get('--data')).toContain("'JetBrains Mono'");
    const families = new Set([...tokens.matchAll(/font-family:\s*'([^']+)'/g)].map((m) => m[1]));
    expect([...families].sort()).toEqual(['IBM Plex Sans', 'JetBrains Mono']);
    for (const m of tokens.matchAll(/url\('\.\.\/(fonts\/[^']+)'\)/g)) {
      expect(statSync(join(SRC, m[1] ?? '')).size, m[1]).toBeGreaterThan(10_000);
    }
  });

  it('are the only place a stylesheet loads anything from, and nothing comes from another host', () => {
    for (const path of [...files(SRC, '.css'), join(SRC, '..', 'index.html')]) {
      const text = readFileSync(path, 'utf8');
      expect(text, path).not.toMatch(/https?:\/\//);
      if (!path.endsWith('tokens.css')) {
        expect(text, path).not.toMatch(/url\(/);
      }
    }
  });

  it('keep the licence of each font beside it', () => {
    expect(readFileSync(join(SRC, 'fonts', 'ibm-plex-sans', 'LICENSE.txt'), 'utf8')).toContain('SIL Open Font License');
    expect(readFileSync(join(SRC, 'fonts', 'jetbrains-mono', 'OFL.txt'), 'utf8')).toContain('SIL Open Font License');
  });

  it('are defined for every name a stylesheet reads', () => {
    const defined = new Set(light.keys());
    for (const path of files(SRC, '.css')) {
      const text = readFileSync(path, 'utf8');
      const local = new Set([...text.matchAll(/(--[A-Za-z0-9-]+):/g)].map((m) => m[1]));
      for (const m of text.matchAll(/var\((--[A-Za-z0-9-]+)\)/g)) {
        expect(defined.has(m[1] ?? '') || local.has(m[1]), `${m[1]} read in ${path}`).toBe(true);
      }
    }
  });
});
