// Dokploy: panel domain, the kupro project and compose app, its environment and its domains.
import { readFileSync } from "node:fs";
import path from "node:path";
import { ROOT, http, step, log, ok, warn, fail, capture } from "./lib.mjs";

// Variables that may stay empty: CD sets the images; the rest switch optional features off.
const MAY_BE_EMPTY = /^(\w+_IMAGE|ELMAHIO_\w+|TRELLO_API_KEY|SCALEWAY_AI_KEY|MEMBERS_GUESTS_PER_SEAT)$/;

function client(kit) {
    const base = kit.need("DOKPLOY_URL").replace(/\/$/, "");
    // The API key and every production secret go through this client: HTTPS, or plain HTTP only through an SSH tunnel.
    const { protocol, hostname } = new URL(base);
    if (protocol !== "https:" && !["localhost", "127.0.0.1", "[::1]"].includes(hostname)) {
        fail("DOKPLOY_URL must be https, or http://localhost:3000 through 'ssh -L 3000:127.0.0.1:3000 <vps>'");
    }
    const headers = { "x-api-key": kit.need("DOKPLOY_API_KEY") };
    return {
        get: async (proc, query = {}) => (await http("GET", `${base}/api/${proc}?${new URLSearchParams(query)}`, { headers })).data,
        post: async (proc, body) => (await http("POST", `${base}/api/${proc}`, { headers, body })).data,
    };
}

const parseEnv = text => new Map((text || "").split(/\r?\n/).filter(l => /^[A-Z0-9_]+=/.test(l))
    .map(l => { const i = l.indexOf("="); return [l.slice(0, i), l.slice(i + 1)]; }));

// ${VAR} without a default in the compose file ($${VAR} is a literal for the container).
const composeVars = () => [...new Set([...readFileSync(path.join(ROOT, "deploy/docker-compose.yaml"), "utf8")
    .matchAll(/(?<!\$)\$\{([A-Z0-9_]+)\}/g)].map(m => m[1]))];

export async function dokploy(kit, prod) {
    const api = client(kit);
    const domain = kit.need("DOMAIN");

    if (kit.isSet("DOKPLOY_PANEL_HOST")) {
        step(`Dokploy panel on ${kit.get("DOKPLOY_PANEL_HOST")}`);
        const host = kit.get("DOKPLOY_PANEL_HOST");
        if (kit.get("DOKPLOY_URL") === `https://${host}`) ok("already");
        else {
            await api.post("settings.assignDomainServer", { host, https: true, certificateType: "letsencrypt", letsEncryptEmail: kit.need("LETSENCRYPT_EMAIL") });
            ok("assigned");
            log(`Once https://${host} loads, set DOKPLOY_URL=https://${host} in go-live.env and run 'dokploy' again.`);
            warn("Then close the panel's IP port: ssh to the VPS and run: docker service update --publish-rm 3000 dokploy");
            // Secrets go out only over the panel's own HTTPS, never through the bootstrap tunnel.
            return;
        }
    }

    step("Project and compose app");
    const projects = await api.get("project.all");
    const project = projects.find(p => p.name === "kupro");
    let envId = project?.environments.find(e => e.isDefault)?.environmentId ?? project?.environments[0]?.environmentId;
    if (!project) {
        const created = await api.post("project.create", { name: "kupro", description: "Kupro production" });
        envId = created.environment.environmentId;
        log("project created");
    }
    let composeId = kit.get("DOKPLOY_COMPOSE_ID") ||
        project?.environments.flatMap(e => e.compose ?? []).find(c => c.name === "kupro")?.composeId;
    if (!composeId) {
        composeId = (await api.post("compose.create", { name: "kupro", appName: "kupro", environmentId: envId, composeType: "docker-compose" })).composeId;
        log("compose app created");
    }
    kit.set("DOKPLOY_COMPOSE_ID", composeId);

    const remote = capture("git", ["-C", ROOT, "remote", "get-url", "origin"]).out
        .replace(/^git@github\.com:/, "https://github.com/").replace(/(\.git)?$/, ".git");
    // Autodeploy off: CD deploys, after E2E.
    await api.post("compose.update", {
        composeId, sourceType: "git", customGitUrl: remote, customGitBranch: "main",
        composePath: "./deploy/docker-compose.yaml", autoDeploy: false,
    });
    ok(`source ${remote} main, ./deploy/docker-compose.yaml, autodeploy off`);

    step("Environment");
    const todo = prod.entries().filter(([, v]) => v === "TODO").map(([k]) => k);
    if (todo.length) fail(`Still TODO in production.env: ${todo.join(", ")}`);
    // Keep the image tags CD set where production.env leaves them empty; every other value comes from the file.
    const env = parseEnv((await api.get("compose.one", { composeId })).env);
    for (const [k, v] of prod.entries()) if (v !== "" || !/_IMAGE$/.test(k) || !env.has(k)) env.set(k, v);
    const missing = composeVars().filter(k => !env.has(k) || (env.get(k) === "" && !MAY_BE_EMPTY.test(k)));
    if (missing.length) fail(`The compose file needs these, missing or empty in production.env: ${missing.join(", ")}`);
    const empty = [...env].filter(([k, v]) => v === "" && !/_IMAGE$/.test(k)).map(([k]) => k);
    if (empty.length) warn(`Empty (feature off): ${empty.join(", ")}`);
    await api.post("compose.saveEnvironment", { composeId, env: [...env].map(([k, v]) => `${k}=${v}`).join("\n") });
    ok(`${env.size} variables saved`);

    step("Domains");
    const host = k => new URL(prod.need(k)).host;
    const wanted = [
        [domain, "site", 80],
        // Served by the site too; a redirect to the apex is a Dokploy middleware, see the runbook.
        [`www.${domain}`, "site", 80],
        [host("API_PUBLIC_URL"), "web", 80],
        [host("KEYCLOAK_PUBLIC_URL"), "keycloak", 8080],
        [host("GRAFANA_PUBLIC_URL"), "grafana", 3000],
    ];
    const existing = new Set((await api.get("domain.byComposeId", { composeId })).map(d => d.host));
    for (const [h, serviceName, port] of wanted) {
        if (!existing.has(h)) {
            await api.post("domain.create", { host: h, path: "/", port, https: true, certificateType: "letsencrypt", composeId, serviceName, domainType: "compose" });
        }
        ok(`${h} → ${serviceName}:${port}`);
    }
    log(`Compose id ${composeId} is in go-live.env; 'github' sends it to GitHub.`);
}
