#!/usr/bin/env node
// Renames the product everywhere in the repo: file and folder names, .NET namespaces and projects, CLI binary,
// MCP name, Keycloak realm and theme, database and volume names, env vars, image names, docs and UI copy.
//
//   node scripts/rename-product.mjs <NewName> [--token-prefix <xx>] [--dry-run]
//
//   NewName          PascalCase, e.g. Foresprint. Replaces Flowstate / flowstate / FLOWSTATE in matching case.
//   --token-prefix   Also replace the API token prefix fs_ (fs_pat_, fs_agent_) with <xx>_.
//   --dry-run        Print what would change; touch nothing.
//
// Safe before go-live only: it renames the database, Keycloak realm and Docker volumes, which a running
// production stack depends on. Stop `aspire run` first (locked bin/obj folders block the folder moves).
// Run from a clean working tree so the result is one reviewable diff. Follow-up steps are printed at the end.

import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';

const OLD = 'Flowstate';
const SELF = 'scripts/rename-product.mjs';

const args = process.argv.slice(2);
const dryRun = args.includes('--dry-run');
const prefixAt = args.indexOf('--token-prefix');
const tokenPrefix = prefixAt >= 0 ? args[prefixAt + 1] : null;
const name = args.find((a, i) => !a.startsWith('--') && (prefixAt < 0 || i !== prefixAt + 1));

if (!name || !/^[A-Z][A-Za-z0-9]+$/.test(name)) {
  console.error('Usage: node scripts/rename-product.mjs <NewName> [--token-prefix <xx>] [--dry-run]');
  console.error('NewName must be PascalCase letters/digits, e.g. Foresprint.');
  process.exit(1);
}
if (tokenPrefix !== null && !/^[a-z]{2,4}$/.test(tokenPrefix ?? '')) {
  console.error('--token-prefix must be 2-4 lowercase letters, e.g. fs.');
  process.exit(1);
}

const pairs = [
  [OLD, name],
  [OLD.toLowerCase(), name.toLowerCase()],
  [OLD.toUpperCase(), name.toUpperCase()],
];
const rename = (s) => pairs.reduce((acc, [from, to]) => acc.split(from).join(to), s);
const hasName = (s) => pairs.some(([from]) => s.includes(from));

const git = (...a) => execFileSync('git', a, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
process.chdir(git('rev-parse', '--show-toplevel').trim());

if (!dryRun && git('status', '--porcelain').trim()) {
  console.error('Working tree is not clean. Commit or stash first, so the rename is one reviewable diff.');
  process.exit(1);
}

const tracked = () => git('ls-files', '-z').split('\0').filter(Boolean);

// 1. File contents. Binary files (any NUL byte) are skipped; this script keeps the old name so it can be rerun.
let filesChanged = 0;
for (const path of tracked()) {
  if (path === SELF || !existsSync(path)) continue;
  const buf = readFileSync(path);
  if (buf.includes(0)) continue;
  const before = buf.toString('utf8');
  let after = rename(before);
  if (tokenPrefix) after = after.replace(/\bfs_/g, `${tokenPrefix}_`);
  if (after !== before) {
    filesChanged++;
    if (!dryRun) writeFileSync(path, after);
  }
}

// 2. Paths, shallowest first, so a folder moves once with everything in it (untracked bin/obj too).
let paths = tracked();
const moves = [];
for (;;) {
  const firstHit = (path) => {
    const parts = path.split('/');
    const i = parts.findIndex(hasName);
    return i < 0 ? null : parts.slice(0, i + 1).join('/');
  };
  const hits = paths.map(firstHit).filter(Boolean);
  if (hits.length === 0) break;
  const from = hits.sort((a, b) => a.split('/').length - b.split('/').length || a.localeCompare(b))[0];
  const to = from.replace(/[^/]+$/, rename);
  moves.push([from, to]);
  if (!dryRun) {
    mkdirSync(dirname(to), { recursive: true });
    git('mv', from, to);
  }
  paths = paths.map(p => (p === from || p.startsWith(`${from}/`) ? to + p.slice(from.length) : p));
}

// 3. Local dev secrets: UserSecretsId contains the name, so copy the AppHost secrets to the new id.
const secretsRoot = process.platform === 'win32'
  ? join(process.env.APPDATA ?? '', 'Microsoft', 'UserSecrets')
  : join(homedir(), '.microsoft', 'usersecrets');
const oldSecrets = join(secretsRoot, `${OLD.toLowerCase()}-apphost`, 'secrets.json');
const newSecrets = join(secretsRoot, `${name.toLowerCase()}-apphost`, 'secrets.json');
const copySecrets = existsSync(oldSecrets) && !existsSync(newSecrets);
if (copySecrets && !dryRun) {
  mkdirSync(dirname(newSecrets), { recursive: true });
  copyFileSync(oldSecrets, newSecrets);
}

console.log(`${dryRun ? 'Would change' : 'Changed'} text in ${filesChanged} files.`);
console.log(`${dryRun ? 'Would move' : 'Moved'} ${moves.length} paths:`);
for (const [from, to] of moves) console.log(`  ${from} -> ${to}`);
if (copySecrets) console.log(`${dryRun ? 'Would copy' : 'Copied'} AppHost user secrets to ${newSecrets}`);
if (dryRun) process.exit(0);

console.log(`
Next:
  1. dotnet build ${name}.slnx        (regenerates openapi/${name.toLowerCase()}.json and the web client)
  2. cd web && bun install            (refreshes bun.lock)
  3. Run /verify, review \`git diff --stat -M\`, commit.
  4. Local dev data starts fresh: Aspire creates new ${name.toLowerCase()}-* volumes and re-imports the realm.
     Remove the old ones when happy: docker volume ls | grep ${OLD.toLowerCase()}
  5. CLI users: config moves with the new name; run \`${name.toLowerCase()} login\` again.
  6. Outside the repo: rename the GitHub repo and local folder, update docs/manual-steps.md (domain, LinkedIn).${tokenPrefix ? '' : `
  7. API tokens still start with fs_. Pass --token-prefix on a rerun if the new name wants another prefix.`}`);
