/**
 * 图片上传（**纯 API**）——实现与旧基座 `douyin-im` 的 `services/im/upload.js` 逐段对齐。
 *
 * 四步（真机验证过能拿到加密结果 `Encryption{Uri,SecretKey}`）：
 *
 * ```
 * ① GET  /aweme/v1/web/im/upload/config/v2   → STS 临时凭证（必须页面同源 fetch，否则被 WAF 判 blocked）
 * ② GET  vod.bytedanceapi.com?Action=ApplyUploadInner&…&s=<随机11位>
 * ③ POST https://<host>/upload/v1/<StoreUri>   Authorization / Content-CRC32 / X-Storage-U / UploadHeader
 * ④ POST vod.bytedanceapi.com?Action=CommitUploadInner
 *         body {SessionKey, Functions:[{name:"Encryption", input:{Config:{copies:"cipher_v2"}, PolicyParams:{…}}}]}
 *         → Result.Results[0].Encryption{Uri, SecretKey, SourceMd5, Extra{img_size,img_width,img_height}}
 * ```
 *
 * 关键点（之前踩过的坑）：
 * - `s` 是**随机 11 位**（旧基座 `randomId(11)`），参与签名；写死常量会 `SignatureDoesNotMatch`
 * - 签名**只签** `x-amz-date`/`x-amz-security-token`（POST 再加 `x-amz-content-sha256`），**不签 host**
 * - **`Functions` 里的 Encryption 配置**才是平台返回 `skey` 的开关；少了它 Commit 只回 Uri
 */

import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import type { BrowserContext, Page } from "playwright";
import type { BrowserLogger } from "./browser.js";
import { OutboundError } from "./protocol.js";
import { randomUploadId, signVodRequest, type StsCredentials } from "./sigv4.js";

/** 上传结果（发消息时用）。 */
export interface UploadedImage {
  /** `Encryption.Uri`，即 content 里的 `resource_url.oid`。 */
  oid: string;
  /** `Encryption.SecretKey`，即 `resource_url.skey`。 */
  skey: string;
  /** 明文 md5（content.md5 / resource_url.md5 都用它）。 */
  md5: string;
  /** 明文字节数。 */
  size: number;
  width: number;
  height: number;
}

const CONFIG_PATH = "/aweme/v1/web/im/upload/config/v2";
const VOD_URL = "https://vod.bytedanceapi.com/";

/** 图片上传的加密/转码配置（照抄旧基座 `uploadProcessFunctions('image', format)`）。 */
export function uploadProcessFunctions(imageFormat: string): unknown[] {
  return [
    {
      name: "Encryption",
      input: {
        Config: { copies: "cipher_v2" },
        PolicyParams:
          imageFormat === "gif"
            ? { "policy-set": "still", "still-width": "480", "still-height": "480" }
            : { "policy-set": "check,thumb,medium,large" },
      },
    },
  ];
}

/** 图片格式嗅探（决定 `aweType` 与加密参数）。 */
export function sniffImageFormat(data: Buffer): "png" | "jpeg" | "gif" | "unknown" {
  if (data.length >= 24 && data.subarray(1, 4).toString() === "PNG") {
    return "png";
  }

  if (data.length > 4 && data[0] === 0xff && data[1] === 0xd8) {
    return "jpeg";
  }

  if (data.length >= 10 && ["GIF87a", "GIF89a"].includes(data.subarray(0, 6).toString())) {
    return "gif";
  }

  return "unknown";
}

/** 宽高（PNG / JPEG / GIF）。 */
export function readImageSize(data: Buffer): { width: number; height: number } {
  if (data.length >= 24 && data.subarray(1, 4).toString() === "PNG") {
    return { width: data.readUInt32BE(16), height: data.readUInt32BE(20) };
  }

  if (data.length > 4 && data[0] === 0xff && data[1] === 0xd8) {
    let pos = 2;
    while (pos + 9 < data.length) {
      if (data[pos] !== 0xff) {
        pos += 1;
        continue;
      }

      const marker = data[pos + 1] ?? 0;
      if (marker >= 0xc0 && marker <= 0xc3) {
        return { width: data.readUInt16BE(pos + 7), height: data.readUInt16BE(pos + 5) };
      }

      const size = data.readUInt16BE(pos + 2);
      if (size < 2) {
        break;
      }

      pos += 2 + size;
    }
  }

  if (data.length >= 10 && ["GIF87a", "GIF89a"].includes(data.subarray(0, 6).toString())) {
    return { width: data.readUInt16LE(6), height: data.readUInt16LE(8) };
  }

  throw new OutboundError("unknown", "读不出图片尺寸（支持 PNG/JPEG/GIF）");
}

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n += 1) {
    let c = n;
    for (let k = 0; k < 8; k += 1) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }

    table[n] = c >>> 0;
  }

  return table;
})();

/** CRC32（TOS 上传头 `Content-CRC32`）。 */
export function crc32Hex(data: Buffer): string {
  let crc = 0xffffffff;
  for (const byte of data) {
    crc = (CRC_TABLE[(crc ^ byte) & 0xff] ?? 0) ^ (crc >>> 8);
  }

  return ((crc ^ 0xffffffff) >>> 0).toString(16).padStart(8, "0");
}

/** 上传地址（Apply 的两种形态：直传 UploadAddress / 内网 UploadNodes）。 */
interface UploadAddress {
  storeUri: string;
  authorization: string;
  host: string;
  sessionKey: string;
  uploadHeaders: Record<string, string>;
}

/** 图片上传器（STS + SigV4 + TOS + Commit 加密）。 */
export class DouyinImageUploader {
  private readonly log: BrowserLogger;
  private readonly selfUid: () => string | null;
  private creds: (StsCredentials & { spaceName: string; expireAt: number }) | null = null;

  constructor(options: { log: BrowserLogger; selfUid?: () => string | null }) {
    this.log = options.log;
    this.selfUid = options.selfUid ?? (() => null);
  }

  /** 上传一张本地图片，返回可直接用于 `message_type=27` 的 oid/skey。 */
  async upload(context: BrowserContext, page: Page, filePath: string): Promise<UploadedImage> {
    const data = await readFile(filePath);
    const format = sniffImageFormat(data);
    if (format === "unknown") {
      throw new OutboundError("unknown", "不支持的图片格式（PNG/JPEG/GIF）");
    }

    const { width, height } = readImageSize(data);
    const creds = await this.ensureCredentials(page);

    const address = await this.apply(context, creds, data.length);
    await this.putObject(context, address, data);
    const committed = await this.commit(context, creds, address.sessionKey, format);

    return {
      oid: committed.uri,
      skey: committed.secretKey,
      md5: createHash("md5").update(data).digest("hex"),
      size: committed.imageSize ?? data.length,
      width: committed.imageWidth ?? width,
      height: committed.imageHeight ?? height,
    };
  }

  /** ② ApplyUploadInner（`s` 随机，参与签名）。 */
  private async apply(
    context: BrowserContext,
    creds: StsCredentials & { spaceName: string },
    fileSize: number,
  ): Promise<UploadAddress> {
    const query: Record<string, string> = {
      Action: "ApplyUploadInner",
      Version: "2020-11-19",
      SpaceName: creds.spaceName,
      FileType: "image",
      IsInner: "1",
      NeedFallback: "true",
      FileSize: String(fileSize),
      s: randomUploadId(11),
    };

    const json = (await this.signedVodJson(context, "GET", query, undefined, creds)) as {
      Result?: {
        UploadAddress?: {
          StoreInfos?: { StoreUri?: string; Auth?: string }[];
          UploadHosts?: string[];
          SessionKey?: string;
        };
        InnerUploadAddress?: {
          UploadNodes?: {
            UploadHost?: string;
            StoreInfos?: { StoreUri?: string; Auth?: string }[];
            SessionKey?: string;
            UploadHeader?: Record<string, string>;
          }[];
        };
      };
    };

    const direct = json.Result?.UploadAddress;
    const nodes = json.Result?.InnerUploadAddress?.UploadNodes;

    if (!nodes?.length && direct?.StoreInfos?.length && direct.UploadHosts?.length) {
      const store = direct.StoreInfos[0]!;
      return {
        storeUri: store.StoreUri ?? "",
        authorization: store.Auth ?? "",
        host: direct.UploadHosts[0]!,
        sessionKey: direct.SessionKey ?? "",
        uploadHeaders: {},
      };
    }

    const node = nodes?.[0];
    const store = node?.StoreInfos?.[0];
    const address: UploadAddress = {
      storeUri: store?.StoreUri ?? "",
      authorization: store?.Auth ?? "",
      host: node?.UploadHost ?? "",
      sessionKey: node?.SessionKey ?? "",
      uploadHeaders: Object.fromEntries(
        Object.entries(node?.UploadHeader ?? {}).map(([key, value]) => [key, String(value)]),
      ),
    };

    if (!address.storeUri || !address.authorization || !address.host || !address.sessionKey) {
      throw new OutboundError("unknown", "VOD ApplyUploadInner 没给出上传地址（StoreUri/Auth/Host/SessionKey）");
    }

    return address;
  }

  /** ③ 传文件字节到 TOS。 */
  private async putObject(
    context: BrowserContext,
    address: UploadAddress,
    data: Buffer,
  ): Promise<void> {
    const uid = this.selfUid();
    const headers: Record<string, string> = {
      authorization: address.authorization,
      "content-type": "application/octet-stream",
      "content-crc32": crc32Hex(data),
      ...(uid ? { "x-storage-u": uid } : {}),
      ...address.uploadHeaders,
    };

    const response = await context.request.post(`https://${address.host}/upload/v1/${address.storeUri}`, {
      headers,
      data,
      timeout: 60_000,
    });

    if (!response.ok()) {
      throw new OutboundError("unknown", `TOS 上传失败：HTTP ${response.status()}`);
    }

    const json = (await response.json().catch(() => ({}))) as { code?: number };
    if (Number(json.code) !== 2000) {
      throw new OutboundError("unknown", `TOS 上传被拒：code=${json.code ?? "?"}`);
    }
  }

  /** ④ CommitUploadInner（带 Functions 加密配置）。 */
  private async commit(
    context: BrowserContext,
    creds: StsCredentials & { spaceName: string },
    sessionKey: string,
    imageFormat: string,
  ): Promise<{ uri: string; secretKey: string; imageSize?: number; imageWidth?: number; imageHeight?: number }> {
    const body = Buffer.from(
      JSON.stringify({
        SessionKey: sessionKey,
        Functions: uploadProcessFunctions(imageFormat),
      }),
      "utf8",
    );

    const json = (await this.signedVodJson(
      context,
      "POST",
      { Action: "CommitUploadInner", Version: "2020-11-19", SpaceName: creds.spaceName },
      body,
      creds,
    )) as {
      Result?: {
        Results?: {
          Encryption?: {
            Uri?: string;
            SecretKey?: string;
            SourceMd5?: string;
            Extra?: Record<string, string | number>;
          };
        }[];
      };
    };

    const encryption = json.Result?.Results?.[0]?.Encryption;
    if (!encryption?.Uri || !encryption.SecretKey) {
      throw new OutboundError("unknown", "VOD CommitUploadInner 没回加密信息（Uri/SecretKey）");
    }

    const extra = encryption.Extra ?? {};
    const num = (value: unknown): number | undefined => {
      const parsed = Number(value);
      return Number.isFinite(parsed) && parsed > 0 ? parsed : undefined;
    };

    this.log.info(`图片上传完成：oid=${encryption.Uri.slice(0, 24)}… skey=${encryption.SecretKey.length} 字符`);

    return {
      uri: encryption.Uri,
      secretKey: encryption.SecretKey,
      imageSize: num(extra["img_size"]),
      imageWidth: num(extra["img_width"]),
      imageHeight: num(extra["img_height"]),
    };
  }

  /** 签 + 发一次 VOD 请求，并检查平台错误码。 */
  private async signedVodJson(
    context: BrowserContext,
    method: "GET" | "POST",
    query: Record<string, string>,
    body: Buffer | undefined,
    creds: StsCredentials,
  ): Promise<unknown> {
    const signed = signVodRequest({ method, query, ...(body ? { body } : {}), credentials: creds });
    const url = `${VOD_URL}?${signed.canonicalQuery}`;

    const response =
      method === "GET"
        ? await context.request.get(url, {
            headers: { ...signed.headers, authorization: signed.authorization },
            timeout: 30_000,
          })
        : await context.request.post(url, {
            headers: {
              ...signed.headers,
              authorization: signed.authorization,
              "content-type": "text/plain;charset=UTF-8",
            },
            data: body,
            timeout: 30_000,
          });

    if (!response.ok()) {
      throw new OutboundError("unknown", `VOD ${query.Action} 失败：HTTP ${response.status()}`);
    }

    const json = (await response.json().catch(() => ({}))) as {
      ResponseMetadata?: { Error?: { Code?: string; CodeN?: number; Message?: string } };
    };

    const error = json.ResponseMetadata?.Error;
    if (error?.Code || error?.CodeN) {
      throw new OutboundError(
        "unknown",
        `VOD ${query.Action} 失败：${error.Code ?? error.CodeN} ${error.Message ?? ""}`.trim(),
      );
    }

    return json;
  }

  /** 取（并缓存）STS 凭证（必须页面同源 fetch，否则被 WAF 回 "blocked"）。 */
  private async ensureCredentials(
    page: Page,
  ): Promise<StsCredentials & { spaceName: string; expireAt: number }> {
    const now = Date.now();
    if (this.creds && this.creds.expireAt * 1000 > now + 60_000) {
      return this.creds;
    }

    const url = new URL(`https://www.douyin.com${CONFIG_PATH}`);
    const params: Record<string, string> = {
      device_platform: "webapp",
      aid: "6383",
      channel: "channel_pc_web",
      update_version_code: "170400",
      pc_client_type: "1",
      pc_libra_divert: "Windows",
      support_h265: "0",
      support_dash: "0",
      cpu_core_num: "4",
      version_code: "170400",
      version_name: "17.4.0",
      cookie_enabled: "true",
      screen_width: "1400",
      screen_height: "950",
      browser_language: "zh-CN",
      browser_platform: "Win32",
      browser_name: "Mozilla",
      browser_online: "true",
      engine_name: "Blink",
      engine_version: "159.0.0.0",
    };
    for (const [key, value] of Object.entries(params)) {
      url.searchParams.set(key, value);
    }

    const raw = await page.evaluate(async (target) => {
      try {
        const response = await fetch(target, { credentials: "include" });
        return { status: response.status, text: await response.text() };
      } catch (error) {
        return { status: 0, text: String(error) };
      }
    }, url.toString());

    if (raw.status !== 200) {
      throw new OutboundError("unknown", `upload/config/v2 失败：HTTP ${raw.status} ${raw.text.slice(0, 40)}`);
    }

    let json: {
      public_image_config?: {
        space_name?: string;
        expire_at?: number;
        access_key_id?: string;
        secret_access_key?: string;
        session_token?: string;
      };
    };
    try {
      json = JSON.parse(raw.text) as typeof json;
    } catch {
      throw new OutboundError("unknown", `upload/config/v2 返回非 JSON：${raw.text.slice(0, 40)}`);
    }

    const cfg = json.public_image_config;
    if (!cfg?.access_key_id || !cfg.secret_access_key || !cfg.session_token) {
      throw new OutboundError("unknown", "upload/config/v2 没给 STS 凭证");
    }

    this.creds = {
      accessKeyId: cfg.access_key_id,
      secretAccessKey: cfg.secret_access_key,
      sessionToken: cfg.session_token,
      spaceName: cfg.space_name ?? "zhenzhen",
      expireAt: cfg.expire_at ?? Math.floor(Date.now() / 1000) + 3600,
    };

    return this.creds;
  }
}
