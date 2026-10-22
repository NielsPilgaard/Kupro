#!/usr/bin/env node
// Renames the product everywhere in the repo: file and folder names, .NET namespaces and projects, CLI binary,
// MCP name, Keycloak realm and theme, database and volume names, env vars, image names, docs and UI copy.
//
//   node scripts/rename-product.mjs <NewName> [--token-prefix <old>:<new>] [--dry-run] [--resume]
//
//   NewName          PascalCase, e.g. Foresprint. The current name comes from <Name>.slnx; it is replaced in all three
//                    casings (Name / name / NAME).
//   --token-prefix   Also replace the API token prefix, e.g. fs:kp turns fs_pat_ into kp_pat_.
//   --dry-run        Print what would change; touch nothing.
//   --resume         Skip the clean-tree check, to finish a run that stopped part way. If <Name>.slnx was already
//                    moved, pass --from <OldName>.
//
// Safe before go-live only: it renames the database, Keycloak realm and Docker volumes, which a running
// production stack depends on. Close the IDE's C# server and stop `aspire run` and build servers first: a locked
// folder stops the run before any file is edited. Run from a clean working tree so the result is one reviewable diff.

import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';

// Files that record names as history (the name search) keep their wording.
const SKIP = new Set(['scripts/rename-product.mjs', 'docs/naming.md']);

const args = process.argv.slice(2);
const dryRun = args.includes('--dry-run');
const resume = args.includes('--resume');
const valueOf = (flag) => (args.includes(flag) ? args[args.indexOf(flag) + 1] : null);
const tokenPrefix = valueOf('--token-prefix');
const fromName = valueOf('--from');
const valueAt = new Set(['--token-prefix', '--from'].map(f => args.indexOf(f) + 1).filter(i => i > 0));
const name = args.find((a, i) => !a.startsWith('--') && !valueAt.has(i));

if (!name || !/^[A-Z][A-Za-z0-9]+$/.test(name)) {
  console.error('Usage: node scripts/rename-product.mjs <NewName> [--token-prefix <old>:<new>] [--dry-run] [--resume [--from <OldName>]]');
  console.error('NewName must be PascalCase letters/digits, e.g. Foresprint.');
  process.exit(1);
}
const [tokenFrom, tokenTo] = tokenPrefix?.split(':') ?? [];
if (tokenPrefix !== null && !(/^[a-z]{2,4}$/.test(tokenFrom ?? '') && /^[a-z]{2,4}$/.test(tokenTo ?? ''))) {
  console.error('--token-prefix takes <old>:<new>, 2-4 lowercase letters each, e.g. fs:kp.');
  process.exit(1);
}

const git = (...a) => execFileSync('git', a, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
process.chdir(git('rev-parse', '--show-toplevel').trim());

const tracked = () => git('ls-files', '-z').split('\0').filter(Boolean);

// The current name is the solution file's (<Name>.slnx at the repo root), unless --from says otherwise.
const OLD = fromName ?? tracked().find(p => /^[A-Z][A-Za-z0-9]+\.slnx$/.test(p))?.replace('.slnx', '');
if (!OLD || OLD === name) {
  console.error(OLD ? `Already named ${name}. Resuming a stopped run? Pass --from <OldName>.` : 'No <Name>.slnx at the repo root; pass --from <OldName>.');
  process.exit(1);
}

const pairs = [
  [OLD, name],
  [OLD.toLowerCase(), name.toLowerCase()],
  [OLD.toUpperCase(), name.toUpperCase()],
];
const rename = (s) => pairs.reduce((acc, [from, to]) => acc.split(from).join(to), s);
const hasName = (s) => pairs.some(([from]) => s.includes(from));

if (!dryRun && !resume && git('status', '--porcelain').trim()) {
  console.error('Working tree is not clean. Commit or stash first, so the rename is one reviewable diff.');
  console.error('(--resume finishes a run that stopped part way.)');
  process.exit(1);
}

// 1. Paths first, shallowest first, so a folder moves once with everything in it (untracked bin/obj too).
//    A locked folder fails here, before any file is edited.
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
  if (to === from) throw new Error(`Cannot rename ${from}`);
  moves.push([from, to]);
  if (!dryRun) {
    mkdirSync(dirname(to), { recursive: true });
    git('mv', from, to);
  }
  paths = paths.map(p => (p === from || p.startsWith(`${from}/`) ? to + p.slice(from.length) : p));
}

// 2. File contents. Binary files (any NUL byte) are skipped.
let filesChanged = 0;
for (const path of paths) {
  if (SKIP.has(path) || !existsSync(path)) continue;
  const buf = readFileSync(path);
  if (buf.includes(0)) continue;
  const before = buf.toString('utf8');
  let after = rename(before);
  if (tokenFrom) after = after.replace(new RegExp(`\\b${tokenFrom}_`, 'g'), `${tokenTo}_`);
  if (after !== before) {
    filesChanged++;
    if (!dryRun) writeFileSync(path, after);
  }
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
  6. By hand: the name's meaning in docs/branding.md, the decision in docs/naming.md.
  7. Outside the repo: rename the GitHub repo and local folder, update notes/manual-steps.md (domain, LinkedIn).${tokenPrefix ? '' : `
  8. API tokens keep their prefix. Pass --token-prefix <old>:<new> if the new name wants another one.`}`);
