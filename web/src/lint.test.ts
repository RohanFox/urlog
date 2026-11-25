import { ESLint } from 'eslint';
import { describe, expect, it } from 'vitest';

// The interface builds no markup from a string: text out of a log reaches the document as text nodes. The rule
// that holds every file of web/ to it is part of the lint gate; this holds the rule itself.

const eslint = new ESLint({ cwd: process.cwd() });

async function problems(code: string): Promise<string[]> {
  // Linted under the name of a real file, so the settings of the project apply to it.
  const [result] = await eslint.lintText(code, { filePath: 'src/search/terms.ts' });
  return (result?.messages ?? []).filter((m) => m.ruleId === 'no-restricted-syntax').map((m) => m.message);
}

describe('the rule against markup and code from a string', () => {
  it.each([
    ['the escape hatch of React as a property', 'export const props = { dangerouslySetInnerHTML: { __html: "x" } };'],
    ['the escape hatch under a quoted key', 'export const props = { "dangerouslySetInnerHTML": { __html: "x" } };'],
    ['innerHTML', 'export function f(el: HTMLElement, s: string) { el.innerHTML = s; }'],
    ['outerHTML', 'export function f(el: HTMLElement, s: string) { el.outerHTML = s; }'],
    ['insertAdjacentHTML', 'export function f(el: HTMLElement, s: string) { el.insertAdjacentHTML("beforeend", s); }'],
    ['a contextual fragment', 'export function f(r: Range, s: string) { return r.createContextualFragment(s); }'],
    ['document.write', 'export function f(s: string) { document.write(s); }'],
    ['DOMParser', 'export function f(s: string) { return new DOMParser().parseFromString(s, "text/html"); }'],
    ['eval', 'export function f(s: string) { return eval(s) as unknown; }'],
    ['a function from a string', 'export function f(s: string) { return new Function(s); }'],
  ])('refuses %s', async (_what, code) => {
    expect(await problems(code)).not.toEqual([]);
  }, 30_000);

  it('lets text through', async () => {
    expect(await problems('export function f(el: HTMLElement, s: string) { el.textContent = s; }')).toEqual([]);
  }, 30_000);
});
