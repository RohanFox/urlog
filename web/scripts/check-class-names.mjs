// Every page stylesheet is in the one bundle, so a rule in one page's sheet reaches every page. A class name
// written in the selectors of two page sheets therefore means one thing to one page and something else to the
// other, and the later sheet in the bundle wins where both match. This fails on such a name and names both
// files. The shared sheets (src/styles and src/ui) are left out: a class meant for every page lives there once.
import { readFileSync, readdirSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const SHARED = ['styles', 'ui'];

// The class names in the selectors of one stylesheet. A selector is the text before an opening brace, back to
// the previous brace or semicolon; at-rule preludes (@media and the like) hold no selector and are skipped.
export function classNames(css) {
  const text = css.replace(/\/\*[\s\S]*?\*\//g, '').replace(/"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'/g, '""');
  const names = new Set();
  for (const match of text.matchAll(/([^{};]*)\{/g)) {
    const prelude = match[1].trim();
    if (prelude.startsWith('@')) continue;
    for (const name of prelude.matchAll(/\.(-?[_a-zA-Z][\w-]*)/g)) names.add(name[1]);
  }
  return names;
}

// Each class name found in more than one sheet, with the sheets in the order given.
export function sharedClasses(sheets) {
  const where = new Map();
  for (const { path, css } of sheets) {
    for (const name of classNames(css)) where.set(name, [...(where.get(name) ?? []), path]);
  }
  return [...where]
    .filter(([, paths]) => paths.length > 1)
    .map(([name, paths]) => ({ name, paths }))
    .sort((a, b) => a.name.localeCompare(b.name));
}

function pageSheets(src) {
  return readdirSync(src, { recursive: true })
    .map((file) => file.toString())
    .filter((file) => file.endsWith('.css') && !SHARED.includes(file.split(sep)[0] ?? ''))
    .sort()
    .map((file) => ({ path: relative(process.cwd(), join(src, file)).replaceAll(sep, '/'), css: readFileSync(join(src, file), 'utf8') }));
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const shared = sharedClasses(pageSheets(fileURLToPath(new URL('../src', import.meta.url))));
  for (const { name, paths } of shared) {
    console.error(`.${name} is in the selectors of ${paths.join(' and ')}: give each page its own name, or move the rule to src/ui/ui.css`);
  }
  process.exitCode = shared.length === 0 ? 0 : 1;
}
