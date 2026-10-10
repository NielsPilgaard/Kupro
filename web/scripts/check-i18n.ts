// Fails when a locale is missing keys the English source has, or has keys English doesn't (ADR-011).
import da from '../src/i18n/locales/da.json';
import en from '../src/i18n/locales/en.json';

type Tree = { [key: string]: string | Tree };

function keys(tree: Tree, prefix = ''): string[] {
  return Object.entries(tree).flatMap(([k, v]) =>
    typeof v === 'string' ? [`${prefix}${k}`] : keys(v, `${prefix}${k}.`),
  );
}

const source = new Set(keys(en));
const locales: Record<string, Tree> = { da };
let failed = false;

for (const [name, tree] of Object.entries(locales)) {
  const actual = new Set(keys(tree));
  const missing = [...source].filter((k) => !actual.has(k));
  const extra = [...actual].filter((k) => !source.has(k));
  if (missing.length || extra.length) {
    failed = true;
    for (const k of missing) console.error(`${name}: missing ${k}`);
    for (const k of extra) console.error(`${name}: extra ${k}`);
  }
}

if (failed) process.exit(1);
console.log(`i18n OK: ${source.size} keys`);
