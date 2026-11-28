// OVHcloud Object Storage: one bucket per purpose, each with its own S3 user that can reach that bucket only.
import { createHash, createHmac } from "node:crypto";
import { http, step, log, ok, warn, fail, sleep } from "./lib.mjs";

const API = "https://eu.api.ovh.com/1.0";

// Rights the token needs, pre-filled on OVH's token page.
export const tokenUrl = () =>
    "https://eu.api.ovh.com/createToken/?" +
    ["GET", "POST", "PUT"].map(m => `${m}=/cloud/project/*`).join("&");

function client(kit) {
    const ak = kit.need("OVH_APPLICATION_KEY"), as = kit.need("OVH_APPLICATION_SECRET"), ck = kit.need("OVH_CONSUMER_KEY");
    let skew;
    return async function call(method, p, body) {
        if (skew === undefined) {
            const { data } = await http("GET", `${API}/auth/time`);
            skew = data - Math.floor(Date.now() / 1000);
        }
        const url = `${API}${p}`, ts = String(Math.floor(Date.now() / 1000) + skew);
        const payload = body === undefined ? "" : JSON.stringify(body);
        const sig = "$1$" + createHash("sha1").update([as, ck, method, url, payload, ts].join("+")).digest("hex");
        const headers = { "X-Ovh-Application": ak, "X-Ovh-Consumer": ck, "X-Ovh-Timestamp": ts, "X-Ovh-Signature": sig };
        return (await http(method, url, { headers, body: payload || undefined })).data;
    };
}

const policy = (bucket, extra = []) => JSON.stringify({
    Statement: [{
        Sid: "bucket-rw",
        Effect: "Allow",
        Action: [
            "s3:GetObject", "s3:PutObject", "s3:DeleteObject", "s3:ListBucket", "s3:GetBucketLocation",
            "s3:ListBucketMultipartUploads", "s3:ListMultipartUploadParts", "s3:AbortMultipartUpload", ...extra,
        ],
        Resource: [`arn:aws:s3:::${bucket}`, `arn:aws:s3:::${bucket}/*`],
    }],
});

export async function storage(kit, prod) {
    const call = client(kit);
    const project = kit.need("OVH_PROJECT_ID");
    const region = kit.get("OVH_REGION") || "GRA";
    const base = `/cloud/project/${project}`;
    if (prod.get("STORAGE_REGION").toUpperCase() !== region.toUpperCase()) {
        fail(`OVH_REGION ${region} differs from STORAGE_REGION ${prod.get("STORAGE_REGION")} in production.env`);
    }
    if (!prod.isSet("KEYS_BUCKET")) prod.set("KEYS_BUCKET", "kupro-keys");

    const targets = [
        // The API's user sets the bucket's CORS once (below); it can already read and write every object in it.
        { bucket: prod.need("STORAGE_BUCKET"), env: prod, key: "STORAGE_ACCESS_KEY", secret: "STORAGE_SECRET_KEY", extra: ["s3:PutBucketCORS", "s3:GetBucketCORS"] },
        // Data Protection keys: versioned, so a bad write can be undone.
        { bucket: prod.need("KEYS_BUCKET"), env: prod, key: "KEYS_ACCESS_KEY", secret: "KEYS_SECRET_KEY", versioning: true },
        { bucket: prod.need("BACKUP_S3_BUCKET"), env: prod, key: "BACKUP_S3_KEY", secret: "BACKUP_S3_KEY_SECRET" },
        { bucket: prod.need("BACKUP_OPS_BUCKET"), env: prod, key: "BACKUP_OPS_ACCESS_KEY", secret: "BACKUP_OPS_SECRET_KEY" },
        // CLI downloads: cli-release.yml uploads with --acl public-read.
        { bucket: "kupro-downloads", env: kit, key: "CLI_STORAGE_ACCESS_KEY", secret: "CLI_STORAGE_SECRET_KEY", extra: ["s3:PutObjectAcl"] },
    ];

    const users = await call("GET", `${base}/user`);
    const buckets = new Set((await call("GET", `${base}/region/${region}/storage`)).map(b => b.name));

    for (const t of targets) {
        step(`Bucket ${t.bucket}`);
        const description = `kupro-s3:${t.bucket}`;
        let user = users.find(u => u.description === description && u.status !== "deleted");
        if (!user) {
            user = await call("POST", `${base}/user`, { description, role: "objectstore_operator" });
            log(`created S3 user ${user.username}`);
        }
        for (let i = 0; user.status !== "ok"; i++) {
            if (i > 30) fail(`S3 user ${user.id} still ${user.status} after 2.5 minutes`);
            await sleep(5000);
            user = await call("GET", `${base}/user/${user.id}`);
        }
        await call("POST", `${base}/user/${user.id}/policy`, { policy: policy(t.bucket, t.extra) });
        ok(`user ${user.username} limited to ${t.bucket}`);

        if (!buckets.has(t.bucket)) {
            await call("POST", `${base}/region/${region}/storage`, {
                name: t.bucket,
                ownerId: user.id,
                encryption: { sseAlgorithm: "AES256" },
                ...(t.versioning && { versioning: { status: "enabled" } }),
            });
            ok(`created (encrypted${t.versioning ? ", versioned" : ""})`);
        } else ok("exists");

        if (t.env.isSet(t.key) && t.env.isSet(t.secret)) {
            log(`keys already in ${t.env === prod ? "production.env" : "go-live.env"}`);
        } else {
            const cred = await call("POST", `${base}/user/${user.id}/s3Credentials`);
            const secret = cred.secret ?? (await call("POST", `${base}/user/${user.id}/s3Credentials/${cred.access}/secret`)).secret;
            t.env.set(t.key, cred.access);
            t.env.set(t.secret, secret);
            ok(`new keys → ${t.key}, ${t.secret}`);
        }
    }

    step("CORS on the attachments bucket");
    const origin = prod.need("API_PUBLIC_URL");
    await putCors(prod.need("STORAGE_URL"), prod.need("STORAGE_REGION"), prod.need("STORAGE_BUCKET"),
        prod.need("STORAGE_ACCESS_KEY"), prod.need("STORAGE_SECRET_KEY"), origin);
    ok(`browsers may PUT from ${origin}`);
    warn("Store the S3 keys in your password manager (the backup ones are needed to restore after losing the VPS).");
}

// PutBucketCors, signed with AWS Signature V4 (no AWS CLI needed).
async function putCors(endpoint, region, bucket, accessKey, secretKey, origin) {
    const xml = `<CORSConfiguration><CORSRule><AllowedOrigin>${origin}</AllowedOrigin><AllowedMethod>PUT</AllowedMethod>` +
        `<AllowedHeader>*</AllowedHeader><MaxAgeSeconds>3600</MaxAgeSeconds></CORSRule></CORSConfiguration>`;
    const host = new URL(endpoint).host;
    const amzDate = new Date().toISOString().replace(/[-:]|\.\d{3}/g, "");
    const day = amzDate.slice(0, 8);
    const sha = s => createHash("sha256").update(s).digest("hex");
    const hmac = (k, s) => createHmac("sha256", k).update(s).digest();
    const headers = {
        "content-md5": createHash("md5").update(xml).digest("base64"),
        host,
        "x-amz-content-sha256": sha(xml),
        "x-amz-date": amzDate,
    };
    const signed = Object.keys(headers).join(";");
    const canonical = ["PUT", `/${bucket}`, "cors=", ...Object.entries(headers).map(([k, v]) => `${k}:${v}`), "", signed, headers["x-amz-content-sha256"]].join("\n");
    const scope = `${day}/${region}/s3/aws4_request`;
    const toSign = ["AWS4-HMAC-SHA256", amzDate, scope, sha(canonical)].join("\n");
    const key = ["s3", "aws4_request"].reduce(hmac, hmac(hmac(`AWS4${secretKey}`, day), region));
    const signature = createHmac("sha256", key).update(toSign).digest("hex");
    const { host: _, ...sent } = headers;
    await http("PUT", `https://${host}/${bucket}?cors`, {
        headers: { ...sent, "Content-Type": "application/xml", Authorization: `AWS4-HMAC-SHA256 Credential=${accessKey}/${scope}, SignedHeaders=${signed}, Signature=${signature}` },
        body: xml,
    });
}
