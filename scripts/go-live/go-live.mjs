#!/usr/bin/env node
// Go-live kit: scripts the production setup in docs/runbooks/deploy.md. Every command is safe to rerun.
// Reads credentials from notes/go-live.env and fills notes/production.env (both gitignored).
// Order: init → vps → storage → email → dns → dokploy → github → (first deploy) → keycloak → check.
import { existsSync, writeFileSync } from "node:fs";
import { Resolver } from "node:dns/promises";
import { randomBytes } from "node:crypto";
import path from "node:path";
import { ROOT, KIT_ENV, kitEnv, prodEnv, http, run, capture, step, log, ok, warn, fail } from "./lib.mjs";
import { storage, tokenUrl } from "./ovh.mjs";
import { email, emailRecords } from "./scaleway.mjs";
import { dokploy } from "./dokploy.mjs";

const HELP = `Usage: node scripts/go-live/go-live.mjs <command>

  init        Create notes/go-live.env and say where each credential comes from
  vps         Harden the VPS, install Dokploy, create the data volumes (over SSH)
  storage     OVHcloud: 5 buckets, one S3 user each, CORS; keys into production.env
  email       Scaleway: TEM domain + SMTP key, Generative APIs key; into production.env
  dns         The records to add at your DNS host, and which ones already resolve
  dokploy     Panel domain, compose app, environment, domains (needs DOKPLOY_URL + DOKPLOY_API_KEY)
  github      production environment (main only) + Dokploy secrets, CLI download secrets
  keycloak    Create your permanent Keycloak admin (2FA on first sign-in)
              --delete-bootstrap  then delete the bootstrap admin, once yours has 2FA
  check       What is still TODO, DNS, HTTPS endpoints; --test-email sends Grafana's test alert`;

const TEMPLATE = `# Credentials for scripts/go-live. Gitignored. Move the keys to your password manager after go-live, then delete this file.
DOMAIN=kupro.app
VPS_IP=TODO
# The SSH login, e.g. ubuntu@1.2.3.4. Log in with your key once before 'vps'.
VPS_SSH=TODO

# OVHcloud API token: open the URL 'init' prints, sign in, validity Unlimited (or a few days).
OVH_APPLICATION_KEY=TODO
OVH_APPLICATION_SECRET=TODO
OVH_CONSUMER_KEY=TODO
# Public Cloud → your project → the id under its name (Object Storage lives in a Public Cloud project).
OVH_PROJECT_ID=TODO
OVH_REGION=GRA

# Scaleway: console → IAM → API keys → Generate, for yourself, default project = the one for Kupro.
SCW_ACCESS_KEY=TODO
SCW_SECRET_KEY=TODO

# Dokploy: after 'vps', open http://<VPS_IP>:3000, create the admin, then Settings → Profile → API keys.
DOKPLOY_URL=TODO
DOKPLOY_API_KEY=TODO
DOKPLOY_PANEL_HOST=deploy.kupro.app
LETSENCRYPT_EMAIL=TODO

# Your permanent Keycloak admin (master realm).
KC_ADMIN_USER=TODO
KC_ADMIN_EMAIL=TODO

# Written by the kit:
DOKPLOY_COMPOSE_ID=
SCW_TEM_DOMAIN_ID=
CLI_STORAGE_ACCESS_KEY=
CLI_STORAGE_SECRET_KEY=
`;

function init() {
    if (existsSync(KIT_ENV)) ok("notes/go-live.env exists");
    else { writeFileSync(KIT_ENV, TEMPLATE, { mode: 0o600 }); ok("created notes/go-live.env"); }
    prodEnv();
    log("Fill in the TODOs in notes/go-live.env. Where the values come from:");
    log(`  OVH token (pre-filled rights): ${tokenUrl()}`);
    log("  Scaleway key: https://console.scaleway.com/iam/api-keys");
    log("Then run: vps, storage, email, dns, dokploy, github.");
}

function vps() {
    const kit = kitEnv(), prod = prodEnv();
    const target = kit.need("VPS_SSH");
    step(`VPS ${target}`);
    const files = ["harden.sh", "dokploy.sh"].map(f => path.join(ROOT, "infrastructure/vps", f));
    if (run("scp", [...files, `${target}:`]) !== 0) fail("scp failed");
    // harden.sh turns password login off once it sees this key login.
    if (run("ssh", ["-t", target, "sudo bash harden.sh && sudo bash dokploy.sh"]) !== 0) fail("VPS setup failed; fix and rerun 'vps'");
    prod.set("BACKUP_CONSOLE_SSH", `ssh -L 9090:127.0.0.1:9090 ${target}`);
    ok("BACKUP_CONSOLE_SSH set");
    log(`Next: open http://${kit.get("VPS_IP")}:3000 within minutes and create the Dokploy admin (the first visitor gets it).`);
}

async function dns() {
    const kit = kitEnv();
    const domain = kit.need("DOMAIN"), ip = kit.need("VPS_IP");
    const records = [["@", "A", ip], ["*", "A", ip], ...(await emailRecords(kit))];
    step(`DNS for ${domain}`);
    log("At one.com: Advanced settings → DNS settings → DNS records → create each missing record.");
    log("Hostname '@' is the domain itself; leave the hostname empty if the form has no '@'. '*' covers app, auth, grafana, www, deploy.");
    const r = new Resolver();
    r.setServers(["1.1.1.1", "9.9.9.9"]);
    const fqdn = n => (n === "@" ? domain : `${n}.${domain}`);
    let missing = 0;
    for (const [name, type, value] of records) {
        let found = false;
        try {
            if (type === "A") found = (await r.resolve4(name === "*" ? `app.${domain}` : fqdn(name))).includes(value);
            else {
                const txt = (await r.resolveTxt(fqdn(name))).map(c => c.join(""));
                found = value.startsWith("v=spf1")
                    ? txt.some(t => t.startsWith("v=spf1") && t.includes(value.match(/include:\S+/)?.[0] ?? value))
                    : txt.includes(value);
                if (!found && value.startsWith("v=spf1") && txt.some(t => t.startsWith("v=spf1"))) {
                    warn("An SPF record exists already: add the include to it instead of a second SPF record.");
                }
            }
        } catch { /* not there yet */ }
        if (!found) missing++;
        console.log(`   ${found ? "\x1b[32m✓\x1b[0m" : "\x1b[33m·\x1b[0m"} ${type.padEnd(4)} ${name.padEnd(28)} ${value}`);
    }
    if (!records.some(([, t]) => t === "TXT")) warn("Run 'email' first to see the mail records too.");
    missing ? warn(`${missing} missing. DNS changes can take up to an hour; rerun 'dns' to check.`) : ok("all records resolve");
    return missing;
}

function github() {
    const kit = kitEnv();
    const repo = capture("gh", ["repo", "view", "--json", "nameWithOwner", "-q", ".nameWithOwner"]).out || fail("gh is not signed in");
    const url = kit.need("DOKPLOY_URL");
    if (!url.startsWith("https://")) fail("DOKPLOY_URL must be https (the panel domain), or the API key crosses the internet in clear text");
    step(`GitHub ${repo}`);
    const api = (method, p, body) => capture("gh", ["api", "-X", method, `repos/${repo}/${p}`, ...(body ? ["--input", "-"] : [])], { input: body && JSON.stringify(body) });
    const env = api("PUT", "environments/production", { deployment_branch_policy: { protected_branches: false, custom_branch_policies: true } });
    if (env.code !== 0) fail(env.err);
    const policies = JSON.parse(api("GET", "environments/production/deployment-branch-policies").out || "{}").branch_policies ?? [];
    if (!policies.some(p => p.name === "main")) api("POST", "environments/production/deployment-branch-policies", { name: "main", type: "branch" });
    ok("environment production, deploys from main only");
    const secret = (name, value, scope) => {
        if (capture("gh", ["secret", "set", name, "--repo", repo, ...scope], { input: value }).code !== 0) fail(`gh secret set ${name}`);
        ok(`secret ${name}${scope.length ? " (production)" : ""}`);
    };
    for (const k of ["DOKPLOY_URL", "DOKPLOY_API_KEY", "DOKPLOY_COMPOSE_ID"]) secret(k, kit.need(k).replace(/\/$/, ""), ["--env", "production"]);
    if (kit.isSet("CLI_STORAGE_ACCESS_KEY")) for (const k of ["CLI_STORAGE_ACCESS_KEY", "CLI_STORAGE_SECRET_KEY"]) secret(k, kit.get(k), []);
    log("CD is on. Push to main, or run cd.yml by hand, for the first deploy.");
}

async function keycloak(deleteBootstrap) {
    const kit = kitEnv(), prod = prodEnv();
    const base = prod.need("KEYCLOAK_PUBLIC_URL");
    const token = (await http("POST", `${base}/realms/master/protocol/openid-connect/token`, {
        form: { grant_type: "password", client_id: "admin-cli", username: "admin", password: prod.need("KEYCLOAK_PASSWORD") },
        allow: [401],
    }));
    if (token.status === 401) return ok("the bootstrap admin is gone already");
    const headers = { Authorization: `Bearer ${token.data.access_token}` };
    const kc = async (method, p, body, allow) => (await http(method, `${base}/admin/realms/master${p}`, { headers, body, allow })).data;
    const username = kit.need("KC_ADMIN_USER");
    const find = async () => (await kc("GET", `/users?exact=true&username=${encodeURIComponent(username)}`))[0];

    step(`Keycloak admin ${username}`);
    let user = await find();
    if (deleteBootstrap) {
        if (!user) fail(`${username} does not exist; run 'keycloak' first`);
        const creds = await kc("GET", `/users/${user.id}/credentials`);
        if (!creds.some(c => c.type === "otp") || user.requiredActions?.length) {
            fail(`${username} has not finished setting up 2FA and a password. Sign in at ${base}/admin first.`);
        }
        const bootstrap = await kc("GET", "/users?exact=true&username=admin");
        if (bootstrap[0]) await kc("DELETE", `/users/${bootstrap[0].id}`);
        return ok("bootstrap admin deleted. KEYCLOAK_PASSWORD no longer signs in anywhere.");
    }
    if (user) return ok(`exists. After signing in with 2FA, run: keycloak --delete-bootstrap`);
    const temp = randomBytes(12).toString("base64url");
    await kc("POST", "/users", {
        username, email: kit.need("KC_ADMIN_EMAIL"), emailVerified: true, enabled: true,
        requiredActions: ["CONFIGURE_TOTP", "UPDATE_PASSWORD"],
        credentials: [{ type: "password", value: temp, temporary: true }],
    });
    user = await find();
    const role = await kc("GET", "/roles/admin");
    await kc("POST", `/users/${user.id}/role-mappings/realm`, [role]);
    ok(`created with the admin role. One-time password: ${temp}`);
    log(`Sign in at ${base}/admin: set a password and scan the 2FA code. Then run: keycloak --delete-bootstrap`);
}

async function check(testEmail) {
    const kit = kitEnv(), prod = prodEnv();
    step("Still to fill in");
    const todo = [...prod.entries(), ...kit.lines.filter(l => /^[A-Z0-9_]+=/.test(l)).map(l => l.split("="))]
        .filter(([, v]) => v === "TODO").map(([k]) => k);
    todo.length ? warn(todo.join(", ")) : ok("nothing");
    await dns();

    step("Endpoints");
    const site = `https://${kit.need("DOMAIN")}`;
    const targets = [
        ["site", site],
        ["app status", `${prod.need("API_PUBLIC_URL")}/api/v1/status`],
        ["Keycloak realm", `${prod.need("KEYCLOAK_PUBLIC_URL")}/realms/kupro/.well-known/openid-configuration`],
        ["Grafana", `${prod.need("GRAFANA_PUBLIC_URL")}/api/health`],
    ];
    if (kit.get("DOKPLOY_URL").startsWith("https://")) targets.push(["Dokploy panel", kit.get("DOKPLOY_URL")]);
    for (const [name, url] of targets) {
        try {
            const r = await http("GET", url, { allow: [301, 302, 401, 403, 404, 500, 502, 503, 504] });
            const good = r.status < 400 || (name === "Dokploy panel" && r.status < 500);
            const extra = name === "app status" && good ? ` ${r.data?.version ?? ""}` : "";
            good ? ok(`${name} ${r.status}${extra}`) : warn(`${name} ${r.status} ${url}`);
        } catch (e) { warn(`${name}: ${e.cause?.code ?? e.message} ${url}`); }
    }

    if (testEmail) {
        step("Grafana test alert");
        const g = prod.need("GRAFANA_PUBLIC_URL");
        const auth = { Authorization: `Basic ${Buffer.from(`admin:${kit.get("GRAFANA_PASSWORD") || prod.need("GRAFANA_ADMIN_PASSWORD")}`).toString("base64")}` };
        const points = (await http("GET", `${g}/api/v1/provisioning/contact-points?name=kupro-email`, { headers: auth })).data;
        if (!points.length) fail("contact point kupro-email not found");
        await http("POST", `${g}/api/alertmanager/grafana/config/api/v1/receivers/test`, {
            headers: auth,
            body: { receivers: [{ name: "kupro-email", grafana_managed_receiver_configs: points.map(p => ({ uid: p.uid, name: p.name, type: p.type, settings: p.settings, disableResolveMessage: false })) }] },
        });
        ok(`sent to ${prod.get("ALERT_EMAIL")}; check the inbox`);
    }
    log("Still by hand: elmah.io heartbeats, PingPuffin check, the smoke test (runbook steps 8–10).");
}

const [cmd, ...args] = process.argv.slice(2);
const commands = {
    init,
    vps,
    storage: () => storage(kitEnv(), prodEnv()),
    email: () => email(kitEnv(), prodEnv()),
    dns,
    dokploy: () => dokploy(kitEnv(), prodEnv()),
    github,
    keycloak: () => keycloak(args.includes("--delete-bootstrap")),
    check: () => check(args.includes("--test-email")),
};
if (!commands[cmd]) { console.log(HELP); process.exit(cmd ? 1 : 0); }
try { await commands[cmd](); } catch (e) { fail(e.message); }
