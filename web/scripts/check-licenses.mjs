// Every package the lockfile installs carries a licence from the list below. A package with another licence,
// or with none stated, fails the check, so a dependency cannot bring one in unnoticed.
import { readFileSync } from 'node:fs';

const ALLOWED = new Set([
  'MIT',
  'MIT-0',
  'ISC',
  'Apache-2.0',
  'BSD-2-Clause',
  'BSD-3-Clause',
  '0BSD',
  'BlueOak-1.0.0',
  'CC0-1.0',
  'CC-BY-4.0',
  'Python-2.0',
  // Build time only: the argument parser under the stylesheet linter's configuration loader. Nothing of it ships.
  'PSF-2.0',
  // Build time only, and file-level copyleft: the CSS minifier of the bundler, run unmodified. Nothing of it ships.
  'MPL-2.0',
]);

function allowed(expression) {
  // "(MIT OR CC0-1.0)": one allowed choice is enough. "MIT AND ISC": every part must be allowed.
  const text = expression.replace(/[()]/g, '').trim();
  if (text.includes(' OR ')) {
    return text.split(' OR ').some(allowed);
  }
  if (text.includes(' AND ')) {
    return text.split(' AND ').every(allowed);
  }
  return ALLOWED.has(text);
}

const lock = JSON.parse(readFileSync(new URL('../package-lock.json', import.meta.url), 'utf8'));
const refused = [];
let checked = 0;
for (const [path, entry] of Object.entries(lock.packages)) {
  if (path === '') {
    continue;
  }
  checked++;
  let licence = entry.license;
  if (typeof licence !== 'string') {
    try {
      const manifest = JSON.parse(readFileSync(new URL(`../${path}/package.json`, import.meta.url), 'utf8'));
      licence = typeof manifest.license === 'string' ? manifest.license : manifest.license?.type;
    } catch {
      licence = undefined;
    }
  }
  if (typeof licence !== 'string' || !allowed(licence)) {
    refused.push(`${path}: ${licence ?? 'no licence stated'}`);
  }
}

if (refused.length > 0) {
  console.error(`licences: ${refused.length} of ${checked} packages are not on the allowed list:`);
  for (const line of refused) {
    console.error(`  ${line}`);
  }
  process.exit(1);
}
console.log(`licences: ${checked} packages, all on the allowed list`);
