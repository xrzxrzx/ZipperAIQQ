/**
 * AWS Signature V4（VOD 内网上传用）。
 *
 * 为什么需要它：抖音的图片上传走 `vod.bytedanceapi.com`，Authorization 头是
 * `AWS4-HMAC-SHA256 Credential=…/20261120/cn-north-1/vod/aws4_request, SignedHeaders=…, Signature=…`
 * —— 这是标准 SigV4（2026-11-21 真机抓包确认）。复刻它才能在 Node 侧上传，不必碰 DOM。
 *
 * 只实现本项目用到的子集：单次签名、无 chunked、payload 直接 hash。
 */

import { createHash, createHmac } from "node:crypto";

/** STS 临时凭证（来自 `/aweme/v1/web/im/upload/config/v2`）。 */
export interface StsCredentials {
  accessKeyId: string;
  secretAccessKey: string;
  sessionToken: string;
  region: string;
  service: string;
}

const sha256Hex = (data: Buffer | string): string => createHash("sha256").update(data).digest("hex");

const hmac = (key: Buffer | string, data: string): Buffer =>
  createHmac("sha256", key).update(data, "utf8").digest();

/** RFC3986 编码（SigV4 要求）。 */
const uriEncode = (value: string): string =>
  encodeURIComponent(value).replace(/[!'()*]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`);

/**
 * 给一次请求签名。
 *
 * @param method HTTP 方法
 * @param url 完整 URL（签名用它的 path 与 query）
 * @param headers 已有请求头（`host` 会补上）
 * @param body 请求体（空则空串）
 */
export function signV4(
  method: string,
  url: URL,
  headers: Record<string, string>,
  body: Buffer,
  creds: StsCredentials,
  now = new Date(),
  excludeFromCanonicalQuery: string[] = [],
): Record<string, string> {
  const amzDate = `${now.toISOString().replace(/[:-]|\.\d{3}/g, "")}`;
  const dateStamp = amzDate.slice(0, 8);
  const payloadHash = sha256Hex(body);

  const signed: Record<string, string> = {
    ...headers,
    host: url.host,
    "x-amz-content-sha256": payloadHash,
    "x-amz-date": amzDate,
    "x-amz-security-token": creds.sessionToken,
  };

  // canonical query：按 key 排序后重新编码
  const queryPairs = [...url.searchParams.entries()]
    // 有些参数是**签名之后**追加的（真机：`s=` 加上后 SignatureDoesNotMatch），
    // 它们必须出现在最终 URL 里，但不能进 canonical query
    .filter(([k]) => !excludeFromCanonicalQuery.includes(k))
    .map(([k, v]) => [uriEncode(k), uriEncode(v)] as const)
    .sort((a, b) => (a[0] === b[0] ? a[1].localeCompare(b[1]) : a[0].localeCompare(b[0])));
  const canonicalQuery = queryPairs.map(([k, v]) => `${k}=${v}`).join("&");

  const signedHeaderNames = Object.keys(signed)
    .map((k) => k.toLowerCase())
    .sort();
  const canonicalHeaders = signedHeaderNames
    .map((name) => `${name}:${String(signed[name] ?? "").trim()}\n`)
    .join("");
  const signedHeaders = signedHeaderNames.join(";");

  const canonicalRequest = [
    method.toUpperCase(),
    url.pathname || "/",
    canonicalQuery,
    canonicalHeaders,
    signedHeaders,
    payloadHash,
  ].join("\n");

  const scope = `${dateStamp}/${creds.region}/${creds.service}/aws4_request`;
  const stringToSign = ["AWS4-HMAC-SHA256", amzDate, scope, sha256Hex(canonicalRequest)].join("\n");

  const kDate = hmac(`AWS4${creds.secretAccessKey}`, dateStamp);
  const kRegion = hmac(kDate, creds.region);
  const kService = hmac(kRegion, creds.service);
  const kSigning = hmac(kService, "aws4_request");
  const signature = createHmac("sha256", kSigning).update(stringToSign, "utf8").digest("hex");

  return {
    ...signed,
    authorization:
      `AWS4-HMAC-SHA256 Credential=${creds.accessKeyId}/${scope}, ` +
      `SignedHeaders=${signedHeaders}, Signature=${signature}`,
  };
}
