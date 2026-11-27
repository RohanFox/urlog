import { describe, expect, it } from 'vitest';
import { classNames, sharedClasses } from '../scripts/check-class-names.mjs';

// The lint step that refuses a class name written in two page stylesheets. It runs on the real sheets in
// `npm run lint`; this holds the check itself.

describe('the class names of a stylesheet', () => {
  it('reads every class of a selector, also inside :has() and :not() and under an at-rule', () => {
    const css = `
      .a > .b:hover, .c::before { color: var(--ink); }
      @media (--fills-window) { .d:has(.e) { height: 100vh; } }
      .f:not(.g) { gap: var(--sp-4); .h { margin: 0; } }
    `;
    expect([...classNames(css)].sort()).toEqual(['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h']);
  });

  it('ignores comments, strings, values and at-rule preludes', () => {
    const css = `
      /* .commented { } */
      @import '../styles/breakpoints.css';
      @font-face { src: url(./font.woff2); }
      [data-x=".quoted"] { opacity: .5; width: 1.5rem; }
      @keyframes pulse { 50.5% { opacity: .4; } }
    `;
    expect([...classNames(css)]).toEqual([]);
  });
});

describe('the check over the page stylesheets', () => {
  it('passes when each page keeps its own names', () => {
    const sheets = [
      { path: 'src/case/case.css', css: '.case-strip { gap: var(--sp-16); } .case-strip .banner { margin: 0; }' },
      { path: 'src/search/search.css', css: '.timeline-region { gap: var(--sp-16); }' },
    ];
    expect(sharedClasses(sheets)).toEqual([]);
  });

  it('names the class and both files when two pages write it', () => {
    const sheets = [
      { path: 'src/case/case.css', css: '.strip { display: flex; gap: var(--sp-16); }' },
      { path: 'src/pages/pages.css', css: '.page { margin: 0; }' },
      { path: 'src/search/search.css', css: '.strip .banner { margin-bottom: var(--sp-4); }' },
    ];
    expect(sharedClasses(sheets)).toEqual([{ name: 'strip', paths: ['src/case/case.css', 'src/search/search.css'] }]);
  });

  it('does not see two meanings of one name inside one sheet', () => {
    // The open row and the open panes once shared a name in one stylesheet. The check cannot tell that apart
    // from a sheet that uses its own class twice, so it passes.
    const sheets = [{ path: 'src/search/search.css', css: '.event-open { background: var(--sel); } .panes.event-open { grid-template-columns: 1fr; }' }];
    expect(sharedClasses(sheets)).toEqual([]);
  });
});
