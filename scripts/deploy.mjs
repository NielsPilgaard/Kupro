#!/usr/bin/env node
// Deploys a Dokploy compose stack with specific image tags.
//
// Required env vars:
//   DOKPLOY_API_KEY     — API token from Dokploy dashboard → Settings → API
//   DOKPLOY_URL         — e.g. https://dokploy.yourvps.com
//   DOKPLOY_COMPOSE_ID  — compose ID visible in the Dokploy dashboard URL
//   IMAGE_TAG           — e.g. sha-abc1234
//   IMAGE_PREFIX        — e.g. ghcr.io/owner/flowstate (images are <prefix>-api, -web and -keycloak)
//
// The Dokploy deployment log (docker pull/compose output) is streamed into this
// process's output. On failure its error lines become GitHub annotations and the
// log tail goes to the job summary, since Dokploy's errorMessage alone is just
// "Docker command failed".

import { appendFileSync } from "node:fs";

const { DOKPLOY_API_KEY, DOKPLOY_URL, DOKPLOY_COMPOSE_ID, IMAGE_TAG, IMAGE_PREFIX, GITHUB_ACTIONS, GITHUB_STEP_SUMMARY } = process.env;

if (!DOKPLOY_API_KEY) throw new Error("DOKPLOY_API_KEY is not set");
if (!DOKPLOY_URL) throw new Error("DOKPLOY_URL is not set");
if (!DOKPLOY_COMPOSE_ID) throw new Error("DOKPLOY_COMPOSE_ID is not set");
if (!IMAGE_TAG) throw new Error("IMAGE_TAG is not set");
if (!IMAGE_PREFIX) throw new Error("IMAGE_PREFIX is not set");

const headers = { "x-api-key": DOKPLOY_API_KEY, "Content-Type": "application/json" };
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function post(path, body) {
    const res = await fetch(`${DOKPLOY_URL}/api/${path}`, {
        method: "POST",
        headers,
        body: JSON.stringify(body),
    });
    if (!res.ok) throw new Error(`POST ${path} failed: ${res.status} ${await res.text()}`);
    return res;
}

async function get(path, query) {
    const qs = new URLSearchParams(query);
    const res = await fetch(`${DOKPLOY_URL}/api/${path}?${qs}`, { headers });
    if (!res.ok) throw new Error(`GET ${path} failed: ${res.status} ${await res.text()}`);
    return res.json();
}

// Newest first.
const listDeployments = () => get("deployment.allByCompose", { composeId: DOKPLOY_COMPOSE_ID });

async function saveEnvironment(compose) {
    // Everything else (secrets, URLs) is set by hand in Dokploy (docs/runbooks/deploy.md) and kept as is.
    const overrides = {
        API_IMAGE: `${IMAGE_PREFIX}-api:${IMAGE_TAG}`,
        MIGRATIONS_IMAGE: `${IMAGE_PREFIX}-api:${IMAGE_TAG}`,
        WEB_IMAGE: `${IMAGE_PREFIX}-web:${IMAGE_TAG}`,
        KEYCLOAK_IMAGE: `${IMAGE_PREFIX}-keycloak:${IMAGE_TAG}`,
    };

    // Replace matching lines in-place to preserve blank lines and ordering.
    const lines = (compose.env ?? "").split("\n");
    const seen = new Set();
    const updated = lines.map(line => {
        const eq = line.indexOf("=");
        if (eq === -1) return line;
        const key = line.slice(0, eq);
        if (key in overrides) { seen.add(key); return `${key}=${overrides[key]}`; }
        return line;
    });
    for (const [key, val] of Object.entries(overrides)) {
        if (!seen.has(key)) updated.push(`${key}=${val}`);
    }
    const env = updated.join("\n");

    await post("compose.saveEnvironment", { composeId: DOKPLOY_COMPOSE_ID, env });
    console.log(`Environment updated to ${IMAGE_TAG}`);
}

async function redeploy() {
    await post("compose.redeploy", {
        composeId: DOKPLOY_COMPOSE_ID,
        title: `Deploy ${IMAGE_TAG}`,
    });
    console.log("Redeploy triggered");
}

// compose.redeploy only queues the job, so right afterwards the newest deployment can
// still be the previous one. Wait for a deployment we haven't seen before.
async function waitForNewDeployment(previousId) {
    for (let i = 0; i < 60; i++) {
        const [latest] = await listDeployments();
        if (latest && latest.deploymentId !== previousId) return latest;
        await sleep(2000);
    }
    throw new Error("Dokploy did not start a new deployment within 2 minutes");
}

// Dokploy serves deployment logs only over its websocket (tail -f of the log file).
// Best effort: if it fails, the deploy still works, we just have less to show.
function streamLog(logPath, serverId) {
    const lines = [];
    let partial = "";
    const url = new URL("/listen-deployment", DOKPLOY_URL);
    url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
    url.searchParams.set("logPath", logPath);
    if (serverId) url.searchParams.set("serverId", serverId);

    let ws;
    try {
        // `headers` is a Node (undici) extension to the WebSocket constructor.
        ws = new WebSocket(url, { headers: { "x-api-key": DOKPLOY_API_KEY } });
    } catch (err) {
        console.log(`(Could not stream Dokploy log: ${err.message})`);
        return { lines, close: async () => {} };
    }

    if (GITHUB_ACTIONS) console.log("::group::Dokploy deployment log");
    ws.addEventListener("message", async event => {
        const text = typeof event.data === "string" ? event.data : await new Response(event.data).text();
        const chunk = (partial + text).split("\n");
        partial = chunk.pop();
        for (const line of chunk) {
            lines.push(line);
            console.log(line);
        }
    });
    ws.addEventListener("error", () => console.log("(Dokploy log stream error — see the Dokploy dashboard for the full log)"));

    return {
        lines,
        close: async () => {
            await sleep(2000); // let the tail flush the last lines
            if (partial) { lines.push(partial); console.log(partial); }
            ws.close();
            if (GITHUB_ACTIONS) console.log("::endgroup::");
        },
    };
}

async function waitForCompletion(deploymentId) {
    const deadline = Date.now() + 25 * 60 * 1000;
    let lastStatus;
    while (Date.now() < deadline) {
        const deployment = (await listDeployments()).find(d => d.deploymentId === deploymentId);
        if (!deployment) throw new Error(`Deployment ${deploymentId} disappeared`);
        if (deployment.status !== lastStatus) {
            console.log(`  status: ${deployment.status}`);
            lastStatus = deployment.status;
        }
        if (["done", "error", "cancelled"].includes(deployment.status)) return deployment;
        await sleep(5000);
    }
    throw new Error("Deployment did not finish within 25 minutes");
}

// Annotation values must have %, CR and LF escaped.
const escapeAnnotation = s => s.replaceAll("%", "%25").replaceAll("\r", "%0D").replaceAll("\n", "%0A");

function reportFailure(deployment, logLines) {
    const message = deployment.status === "cancelled"
        ? "Deployment was cancelled"
        : `Deployment failed: ${deployment.errorMessage ?? "unknown error"}`;
    const errorLines = [...new Set(logLines.filter(l => /error|failed|not found|denied/i.test(l)).map(l => l.trim()))].slice(-10);

    if (GITHUB_ACTIONS) {
        const details = errorLines.length ? errorLines.join("\n") : "No error lines captured from the Dokploy log.";
        console.log(`::error title=${escapeAnnotation(message)}::${escapeAnnotation(details)}`);
    } else {
        console.error(message);
        for (const line of errorLines) console.error(`  ${line}`);
    }

    if (GITHUB_STEP_SUMMARY) {
        const tail = logLines.slice(-80).join("\n") || "(log not available — check the Dokploy dashboard)";
        appendFileSync(GITHUB_STEP_SUMMARY, [
            `## ❌ Deploy of \`${IMAGE_TAG}\` failed`,
            "",
            `**${message}**`,
            "",
            "<details open><summary>Dokploy log (last 80 lines)</summary>",
            "",
            "```",
            tail,
            "```",
            "",
            "</details>",
            "",
        ].join("\n"));
    }
}

const compose = await get("compose.one", { composeId: DOKPLOY_COMPOSE_ID });
const [previous] = await listDeployments();

await saveEnvironment(compose);
await redeploy();
const started = await waitForNewDeployment(previous?.deploymentId);
console.log(`Deployment ${started.deploymentId} started, polling status...`);

const log = started.logPath ? streamLog(started.logPath, compose.serverId) : { lines: [], close: async () => {} };
let finished;
try {
    finished = await waitForCompletion(started.deploymentId);
} finally {
    await log.close();
}

if (finished.status !== "done") {
    reportFailure(finished, log.lines);
    process.exit(1);
}

// cd.yml then waits until /api/v1/status reports this commit.
console.log(`Dokploy deployed ${IMAGE_TAG}`);
