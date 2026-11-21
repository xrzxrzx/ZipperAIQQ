/**
 * VOD 请求签名（AWS SigV4）——**照 旧基座 `douyin-im` 的实现逐行对齐**。
 *
 * 为什么重写：之前我们自签的 Apply 请求总是 `SignatureDoesNotMatch`，因为我们把 `host`
 * 也放进了 SignedHeaders。旧基座（真机验证过能拿到 `Encryption.SecretKey`）只签三个头：
 *
 * - 恒签：`x-amz-date`、`x-amz-security-token`
 * - **只有 POST** 才签 `x-amz-content-sha256`
 * - **不签 `host`**
 *
 * canonical URI 固定 `/`；query 按 key 排序后 RFC3986 编码（`s` 是随机 11 位，
 * 参与签名 —— 旧基座 `randomId(11)`）。
 */

import { createHash, createHmac, randomBytes } from "node:crypto";

/** STS 临时凭证（来自 `/aweme/v1/web/im/upload/config/v2`）。 */
export interface StsCredentials {
  accessKeyId: string;
  secretAccessKey: string;
  sessionToken: string;
}

const VOD_REGION = "cn-north-1";
const VOD_SERVICE = "vod";

const hashHex = (data: Buffer | string): string => createHash("sha256").update(data).digest("hex");

const hmac = (key: Buffer | string, value: string): Buffer =>
  createHmac("sha256", key).update(value).digest();

const rfc3986 = (value: string): string =>
  encodeURIComponent(value).replace(/[!'()*]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`);

/** 排序 + RFC3986 编码的 canonical query。 */
export function canonicalUploadQuery(query: Record<string, string>): string {
  return Object.keys(query)
    .sort()
    .map((key) => `${rfc3986(key)}=${rfc3986(query[key]!)}`)
    .join("&");
}

/** 11 位随机小写字母/数字（`s` 参数）。 */
export function randomUploadId(length = 11): string {
  const alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
  return [...randomBytes(length)].map((byte) => alphabet[byte % alphabet.length]).join("");
}

/** 签名结果：canonical query + 需要带上的头 + Authorization。 */
export interface SignedVodRequest {
  canonicalQuery: string;
  headers: Record<string, string>;
  authorization: string;
}

/** 给一次 VOD 请求签名（与旧基座 `signVodRequest` 完全一致）。 */
export function signVodRequest(options: {
  method: "GET" | "POST";
  query: Record<string, string>;
  body?: Buffer;
  credentials: StsCredentials;
  date?: Date;
}): SignedVodRequest {
  const canonicalQuery = canonicalUploadQuery(options.query);
  const amzDate = (options.date ?? new Date()).toISOString().replace(/[:-]|\.\d{3}/g, "");
  const dateStamp = amzDate.slice(0, 8);
  const payloadHash = hashHex(options.body ?? Buffer.alloc(0));

  const headers: Record<string, string> = {
    "x-amz-date": amzDate,
    "x-amz-security-token": options.credentials.sessionToken,
  };

  if (options.method === "POST") {
    headers["x-amz-content-sha256"] = payloadHash;
  }

  const headerNames = Object.keys(headers).sort();
  const canonicalHeaders = headerNames.map((name) => `${name}:${headers[name]}\n`).join("");
  const signedHeaders = headerNames.join(";");

  const canonicalRequest = [
    options.method,
    "/",
    canonicalQuery,
    canonicalHeaders,
    signedHeaders,
    payloadHash,
  ].join("\n");

  const scope = `${dateStamp}/${VOD_REGION}/${VOD_SERVICE}/aws4_request`;
  const stringToSign = `AWS4-HMAC-SHA256\n${amzDate}\n${scope}\n${hashHex(canonicalRequest)}`;

  let signingKey = hmac(`AWS4${options.credentials.secretAccessKey}`, dateStamp);
  signingKey = hmac(signingKey, VOD_REGION);
  signingKey = hmac(signingKey, VOD_SERVICE);
  signingKey = hmac(signingKey, "aws4_request");
  const signature = hmac(signingKey, stringToSign).toString("hex");

  return {
    canonicalQuery,
    headers,
    authorization:
      `AWS4-HMAC-SHA256 Credential=${options.credentials.accessKeyId}/${scope}, ` +
      `SignedHeaders=${signedHeaders}, Signature=${signature}`,
  };
}
