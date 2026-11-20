import { render } from '@testing-library/react';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { FAVICON, LOADER, MARK, MARK_SMALL } from './geometry';
import { Loader, Lockup, Mark } from './Logo';

const WEB = join(import.meta.dirname, '..', '..');

function points(container: HTMLElement): string[] {
  return [...container.querySelectorAll('polygon')].map((p) => p.getAttribute('points') ?? '');
}

describe('the mark', () => {
  it('is the small cut at 24 px and under, the full mark above', () => {
    expect(points(render(<Mark size={24} />).container)).toEqual([MARK_SMALL.first, MARK_SMALL.second, MARK_SMALL.diamond]);
    expect(points(render(<Mark size={16} />).container)).toEqual([MARK_SMALL.first, MARK_SMALL.second, MARK_SMALL.diamond]);
    expect(points(render(<Mark size={32} />).container)).toEqual([MARK.first, MARK.second, MARK.diamond]);
  });

  it('is decoration without a label and an image with one', () => {
    expect(render(<Mark size={32} />).container.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
    expect(render(<Mark size={32} label="urlog" />).getByRole('img', { name: 'urlog' })).toBeInTheDocument();
  });

  it('carries the tagline only in the full lockup', () => {
    expect(render(<Lockup />).container.querySelector('.brand-tagline')).toBeNull();
    expect(render(<Lockup full />).container.querySelector('.brand-tagline')).not.toBeNull();
  });
});

describe('the drawings outside the components', () => {
  it('the favicon is the small cut on its tile', () => {
    const svg = readFileSync(join(WEB, 'public', 'favicon.svg'), 'utf8');
    for (const part of [MARK_SMALL.first, MARK_SMALL.second, MARK_SMALL.diamond, MARK_SMALL.viewBox, FAVICON.tile, FAVICON.ink, FAVICON.diamond, `rx="${FAVICON.radius}"`]) {
      expect(svg).toContain(part);
    }
  });

  it('the first paint in index.html is the loader the app draws', () => {
    const html = readFileSync(join(WEB, 'index.html'), 'utf8');
    const first = [...html.matchAll(/<polygon class="([^"]+)"[^>]*points="([^"]+)"/g)].map((m) => `${m[1]}|${m[2]}`);
    const drawn = [...render(<Loader size={64} />).container.querySelectorAll('polygon')].map((p) => `${p.getAttribute('class')}|${p.getAttribute('points')}`);
    expect(first).toEqual(drawn);
    expect(html).toContain(`viewBox="${LOADER.viewBox}"`);
    expect(html).not.toMatch(/<style|style=|<script>(?!<)/);
  });
});
