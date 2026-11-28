// Scaleway: Transactional Email for the domain, and two narrow API keys (TEM for SMTP, Generative APIs for the meeting agent).
import { http, step, log, ok, warn, fail } from "./lib.mjs";

const API = "https://api.scaleway.com";
const TEM = "/transactional-email/v1alpha1/regions/fr-par";

function client(kit) {
    const headers = { "X-Auth-Token": kit.need("SCW_SECRET_KEY") };
    return async (method, url, body) => (await http(method, `${API}${url}`, { headers, body })).data;
}

export async function email(kit, prod) {
    const call = client(kit);
    const domainName = kit.need("DOMAIN");
    const me = await call("GET", `/iam/v1alpha1/api-keys/${kit.need("SCW_ACCESS_KEY")}`);
    const projectId = kit.get("SCW_PROJECT_ID") || me.default_project_id;
    if (!projectId) fail("Set SCW_PROJECT_ID in go-live.env (the key has no default project)");
    const orgId = (await call("GET", `/account/v3/projects/${projectId}`)).organization_id;

    step(`Transactional Email: ${domainName}`);
    let domain = (await call("GET", `${TEM}/domains?project_id=${projectId}&name=${domainName}`)).domains
        .find(d => d.name === domainName && d.status !== "revoked");
    if (!domain) {
        domain = await call("POST", `${TEM}/domains`, { project_id: projectId, domain_name: domainName, accept_tos: true, autoconfig: false });
        log("domain added");
    }
    if (domain.status !== "checked") {
        domain = await call("POST", `${TEM}/domains/${domain.id}/check`, {});
    }
    domain.status === "checked" ? ok("domain verified") : warn(`domain ${domain.status}: add the DNS records (node scripts/go-live/go-live.mjs dns), then run email again`);
    kit.set("SCW_TEM_DOMAIN_ID", domain.id);
    prod.set("SMTP_USER", projectId);

    const sets = await permissionSets(call, orgId);
    await appKey(call, prod, { orgId, projectId, name: "kupro-tem", key: "SMTP_PASSWORD", set: pick(sets, /^TransactionalEmail\w*FullAccess$/) });
    await appKey(call, prod, { orgId, projectId, name: "kupro-genai", key: "SCALEWAY_AI_KEY", set: pick(sets, /^GenerativeApis\w*FullAccess$/) });
    warn("Set a billing alert on this project (manual step E4).");
}

// The DNS records TEM wants, as [name relative to the domain, type, value].
export async function emailRecords(kit) {
    if (!kit.isSet("SCW_TEM_DOMAIN_ID") || !kit.isSet("SCW_SECRET_KEY")) return [];
    const d = await client(kit)("GET", `${TEM}/domains/${kit.get("SCW_TEM_DOMAIN_ID")}`);
    const rel = n => n.replace(new RegExp(`\\.?${d.name.replace(/\./g, "\\.")}\\.?$`), "") || "@";
    const r = d.records ?? {};
    return [
        ["@", "TXT", r.spf?.value ?? `v=spf1 ${d.spf_config} -all`],
        [rel(r.dkim?.name ?? `${d.project_id}._domainkey`), "TXT", r.dkim?.value ?? d.dkim_config],
        ...(r.dmarc ? [[rel(r.dmarc.name), "TXT", r.dmarc.value]] : []),
    ];
}

async function permissionSets(call, orgId) {
    const all = [];
    for (let page = 1; ; page++) {
        const r = await call("GET", `/iam/v1alpha1/permission-sets?organization_id=${orgId}&page_size=100&page=${page}`);
        all.push(...r.permission_sets.map(p => p.name));
        if (all.length >= r.total_count || !r.permission_sets.length) return all;
    }
}

function pick(sets, re) {
    const name = sets.find(s => re.test(s));
    if (!name) fail(`No Scaleway permission set matches ${re}. Known: ${sets.join(", ")}`);
    return name;
}

// An IAM application with one permission set on the project, and an API key for it when the env has none.
async function appKey(call, prod, { orgId, projectId, name, key, set }) {
    step(`Scaleway key ${name} (${set})`);
    let app = (await call("GET", `/iam/v1alpha1/applications?organization_id=${orgId}&name=${name}`)).applications.find(a => a.name === name);
    if (!app) {
        app = await call("POST", "/iam/v1alpha1/applications", { name, organization_id: orgId, description: "Kupro production, created by scripts/go-live" });
        log("application created");
    }
    const policies = (await call("GET", `/iam/v1alpha1/policies?organization_id=${orgId}&application_ids=${app.id}`)).policies;
    if (!policies.length) {
        await call("POST", "/iam/v1alpha1/policies", {
            name, organization_id: orgId, application_id: app.id,
            rules: [{ permission_set_names: [set], project_ids: [projectId] }],
        });
        log("policy created");
    }
    ok(`${name} may only use ${set}`);
    if (prod.isSet(key)) return log(`${key} already in production.env`);
    const k = await call("POST", "/iam/v1alpha1/api-keys", { application_id: app.id, default_project_id: projectId, description: "Kupro production" });
    prod.set(key, k.secret_key);
    ok(`new key → ${key}`);
}
