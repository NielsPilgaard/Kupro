// Shared helpers for the go-live kit: the two env files, logging, HTTP and shell calls.
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
export const KIT_ENV = path.join(ROOT, "notes/go-live.env");
export const PROD_ENV = path.join(ROOT, "notes/production.env");
export const MARKER = "# ---- paste from here ----";

export const step = s => console.log(`\n\x1b[1m== ${s}\x1b[0m`);
export const log = s => console.log(`   ${s}`);
export const ok = s => console.log(`   \x1b[32m✓\x1b[0m ${s}`);
export const warn = s => console.log(`   \x1b[33m!\x1b[0m ${s}`);
export const fail = s => { console.error(`\x1b[31mERROR: ${s}\x1b[0m`); process.exit(1); };

// A KEY=VALUE file that keeps its comments and order when a value changes.
export class EnvFile {
    constructor(file) {
        this.file = file;
        this.lines = existsSync(file) ? readFileSync(file, "utf8").split(/\r?\n/) : [];
    }
    #index(key) { return this.lines.findIndex(l => l.startsWith(`${key}=`)); }
    has(key) { return this.#index(key) >= 0; }
    get(key) {
        const i = this.#index(key);
        return i < 0 ? "" : this.lines[i].slice(key.length + 1).trim();
    }
    // Empty, TODO or a <placeholder> counts as unset.
    isSet(key) { const v = this.get(key); return v !== "" && v !== "TODO" && !/^<.*>$/.test(v); }
    need(key) { if (!this.isSet(key)) fail(`${key} is not set in ${path.relative(ROOT, this.file)}`); return this.get(key); }
    set(key, value) {
        const i = this.#index(key);
        if (i >= 0) this.lines[i] = `${key}=${value}`;
        else {
            while (this.lines.length && this.lines.at(-1) === "") this.lines.pop();
            this.lines.push(`${key}=${value}`, "");
        }
        writeFileSync(this.file, this.lines.join("\n"), { mode: 0o600 });
    }
    // The KEY=VALUE pairs below the paste marker (production.env) or all of them.
    entries() {
        const start = this.lines.indexOf(MARKER) + 1;
        return this.lines.slice(start)
            .filter(l => /^[A-Z0-9_]+=/.test(l))
            .map(l => { const i = l.indexOf("="); return [l.slice(0, i), l.slice(i + 1).trim()]; });
    }
}

export const kitEnv = () => {
    if (!existsSync(KIT_ENV)) fail("notes/go-live.env is missing. Run: node scripts/go-live/go-live.mjs init");
    return new EnvFile(KIT_ENV);
};
export const prodEnv = () => {
    if (!existsSync(PROD_ENV)) fail("notes/production.env is missing. Ask an agent to generate it (it holds the generated secrets).");
    return new EnvFile(PROD_ENV);
};

export async function http(method, url, { headers = {}, body, form, allow = [] } = {}) {
    const init = { method, headers: { ...headers } };
    if (form) {
        init.body = new URLSearchParams(form);
    } else if (body !== undefined) {
        init.body = typeof body === "string" ? body : JSON.stringify(body);
        init.headers["Content-Type"] ??= "application/json";
    }
    const res = await fetch(url, init);
    const text = await res.text();
    if (!res.ok && !allow.includes(res.status)) {
        throw new Error(`${method} ${url.replace(/\?.*/, "")} → ${res.status} ${text.slice(0, 500)}`);
    }
    let data = text;
    try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
    return { status: res.status, data };
}

// Runs a program with the terminal attached (ssh prompts work). Returns the exit code.
export function run(cmd, args, { input } = {}) {
    const r = spawnSync(cmd, args, { stdio: input === undefined ? "inherit" : ["pipe", "inherit", "inherit"], input, shell: false });
    if (r.error) fail(`${cmd}: ${r.error.message}`);
    return r.status;
}

export function capture(cmd, args, { input } = {}) {
    const r = spawnSync(cmd, args, { encoding: "utf8", input });
    if (r.error) fail(`${cmd}: ${r.error.message}`);
    return { code: r.status, out: (r.stdout || "").trim(), err: (r.stderr || "").trim() };
}

export const sleep = ms => new Promise(r => setTimeout(r, ms));
