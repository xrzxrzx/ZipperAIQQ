/**
 * 图片上传（**纯 API**，不碰 DOM）。
 *
 * 链路（2026-11-21 真机抓包 + 逐条复刻）：
 *
 * ```
 * ① GET  www.douyin.com/aweme/v1/web/im/upload/config/v2
 *         → public_image_config{space_name, access_key_id, secret_access_key, session_token}
 * ② GET  vod.bytedanceapi.com/?Action=ApplyUploadInner&Version=2020-11-19&SpaceName=…
 *         &FileType=image&IsInner=1&NeedFallback=true&FileSize=<size>     （SigV4 签名）
 *         → Result.UploadAddress.StoreInfos[0]{StoreUri, Auth, UploadID} + Result.SessionKey
 * ③ POST https://<tos host>/upload/v1/<StoreUri>   Authorization: <Auth>   传文件字节
 * ④ POST vod.bytedanceapi.com/?Action=CommitUploadInner&Version=2020-11-19&SpaceName=…
 *         body {"SessionKey": "…"}                                  （SigV4 签名）
 *         → Result.Results[0]{Uri, SourceMd5, Extra…} + SecretKey(=skey)
 * ```
 *
 * 产出直接喂给出站：`content` 里的 `resource_url{oid,skey,data_size,md5}`。
 */

import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import type { BrowserContext, Page } from "playwright";
import type { BrowserLogger } from "./browser.js";
import { OutboundError } from "./protocol.js";
import { signV4, type StsCredentials } from "./sigv4.js";

/** 上传结果（发消息时用）。 */
export interface UploadedImage {
  /** `StoreUri`，即 content 里的 `resource_url.oid`。 */
  oid: string;
  /** `SecretKey`，即 `resource_url.skey`。 */
  skey: string;
  /** 文件 md5（hex）。 */
  md5: string;
  /** 字节数。 */
  size: number;
  width: number;
  height: number;
}

const CONFIG_PATH = "/aweme/v1/web/im/upload/config/v2";
const VOD_HOST = "https://vod.bytedanceapi.com/";

/** CRC32（TOS 上传要求 `content-crc32`，缺了平台不认这份文件）。 */
const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let i = 0; i < 256; i += 1) {
    let c = i;
    for (let k = 0; k < 8; k += 1) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }

    table[i] = c >>> 0;
  }

  return table;
})();

export function crc32Hex(data: Buffer): string {
  let crc = 0xffffffff;
  for (const byte of data) {
    crc = (crc >>> 8) ^ CRC_TABLE[(crc ^ byte) & 0xff]!;
  }

  return ((crc ^ 0xffffffff) >>> 0).toString(16).padStart(8, "0");
}

/** 从 PNG/JPEG 头里读宽高（避免为了尺寸再引依赖）。 */
export function readImageSize(data: Buffer): { width: number; height: number } {
  // PNG：IHDR 在 16..24
  if (data.length > 24 && data[0] === 0x89 && data[1] === 0x50) {
    return { width: data.readUInt32BE(16), height: data.readUInt32BE(20) };
  }

  // JPEG：扫 SOF 段
  if (data.length > 4 && data[0] === 0xff && data[1] === 0xd8) {
    let offset = 2;
    while (offset + 9 < data.length) {
      if (data[offset] !== 0xff) {
        offset += 1;
        continue;
      }

      const marker = data[offset + 1] ?? 0;
      const length = data.readUInt16BE(offset + 2);
      if (marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc) {
        return { height: data.readUInt16BE(offset + 5), width: data.readUInt16BE(offset + 7) };
      }

      offset += 2 + length;
    }
  }

  throw new OutboundError("unknown", "读不出图片尺寸（只支持 PNG/JPEG）");
}

/** 图片上传器（STS + SigV4 + TOS）。 */
export class DouyinImageUploader {
  private readonly log: BrowserLogger;
  private creds: (StsCredentials & { spaceName: string; expireAt: number }) | null = null;

  private readonly selfUid: () => string | null;

  constructor(options: { log: BrowserLogger; selfUid?: () => string | null }) {
    this.log = options.log;
    this.selfUid = options.selfUid ?? (() => null);
  }

  /**
   * 上传一张本地图片。
   *
   * @param context 浏览器 context（复用 cookie，且不受页面 CORS 限制）
   */
  async upload(context: BrowserContext, page: Page, filePath: string): Promise<UploadedImage> {
    const data = await readFile(filePath);
    const { width, height } = readImageSize(data);
    const creds = await this.ensureCredentials(page);

    // ② ApplyUploadInner
    const applyUrl = new URL(VOD_HOST);
    applyUrl.searchParams.set("Action", "ApplyUploadInner");
    applyUrl.searchParams.set("Version", "2020-11-19");
    applyUrl.searchParams.set("SpaceName", creds.spaceName);
    applyUrl.searchParams.set("FileType", "image");
    applyUrl.searchParams.set("IsInner", "1");
    applyUrl.searchParams.set("NeedFallback", "true");
    applyUrl.searchParams.set("FileSize", String(data.length));
    // 抓包里真实请求带的常量参数：平台用它决定是否走服务端加密
    // （缺了它 Commit 只回 Uri/UriStatus，没有 Encryption → 发出去的图对方渲染不出来）
    applyUrl.searchParams.set("s", "sfhwtpaupd");

    const applyHeaders = signV4("GET", applyUrl, { accept: "application/json" }, Buffer.alloc(0), creds);
    const applyRes = await context.request.fetch(applyUrl.toString(), {
      method: "GET",
      headers: applyHeaders,
      timeout: 30_000,
    });
    if (!applyRes.ok()) {
      throw new OutboundError("unknown", `ApplyUploadInner 失败：HTTP ${applyRes.status()}`);
    }

    const applyText = await applyRes.text();
    let applyJson: {
      Result?: {
        SessionKey?: string;
        UploadAddress?: { StoreInfos?: { StoreUri?: string; Auth?: string; UploadID?: string; Host?: string }[] };
      };
    };
    try {
      applyJson = JSON.parse(applyText) as typeof applyJson;
    } catch {
      throw new OutboundError("unknown", `ApplyUploadInner 返回非 JSON：${applyText.slice(0, 120)}`);
    }

    const result = applyJson.Result as
      | (typeof applyJson.Result & {
          InnerUploadAddress?: { SessionKey?: string; StoreInfos?: { StoreUri?: string; Auth?: string; UploadID?: string; Host?: string }[] };
          SDKParam?: unknown;
        })
      | undefined;
    const store = result?.UploadAddress?.StoreInfos?.[0] ?? result?.InnerUploadAddress?.StoreInfos?.[0];
    // SessionKey 有时在 Result 顶层，有时塞在 SDKParam 里（真机两种都见过）
    // 真机命中：SessionKey 在 `Result.UploadAddress.SessionKey`
    let sessionKey =
      (result?.UploadAddress as { SessionKey?: string } | undefined)?.SessionKey ??
      result?.SessionKey ??
      result?.InnerUploadAddress?.SessionKey;
    if (!sessionKey && result?.SDKParam) {
      const sdk = result.SDKParam;
      if (typeof sdk === "string") {
        const matched = /"SessionKey"\s*:\s*"([^"]+)"/.exec(sdk);
        sessionKey = matched?.[1];
      } else if (sdk && typeof sdk === "object") {
        const record = sdk as Record<string, unknown>;
        for (const key of Object.keys(record)) {
          if (/session/i.test(key) && typeof record[key] === "string") {
            sessionKey = record[key] as string;
            break;
          }
        }
      }
    }
    if (!store?.StoreUri || !store.Auth || !sessionKey) {
      throw new OutboundError(
        "unknown",
        `ApplyUploadInner 缺字段；sdkParamKeys=${Object.keys((result?.SDKParam ?? {}) as object).join(",")} uploadAddrKeys=${Object.keys((result?.UploadAddress ?? {}) as object).join(",")}`,
      );
    }

    // ③ 传文件到 TOS（Host 优先用响应给的，其次用固定域名族）
    const tosHost = store.Host ? `https://${store.Host}` : "https://tos-d-cm-lf.douyin.com";
    const uploadUrl = `${tosHost}/upload/v1/${store.StoreUri}`;
    const uploadRes = await context.request.post(uploadUrl, {
      headers: {
        authorization: store.Auth,
        "content-type": "application/octet-stream",
        // 这三项是抓包里的真实请求头：缺 content-crc32 时平台不认文件，
        // Commit 就不会返回 Encryption → 发出去的图是空的（2026-11-22 真机定位）
        "content-crc32": crc32Hex(data),
        "content-disposition": 'attachment; filename="undefined"',
        ...(this.selfUid() ? { "x-storage-u": this.selfUid() as string } : {}),
      },
      data: data,
      timeout: 60_000,
    });
    if (!uploadRes.ok()) {
      throw new OutboundError("unknown", `TOS 上传失败：HTTP ${uploadRes.status()}`);
    }

    // ④ CommitUploadInner
    const commitUrl = new URL(VOD_HOST);
    commitUrl.searchParams.set("Action", "CommitUploadInner");
    commitUrl.searchParams.set("Version", "2020-11-19");
    commitUrl.searchParams.set("SpaceName", creds.spaceName);

    const commitBody = Buffer.from(JSON.stringify({ SessionKey: sessionKey }), "utf8");
    const commitHeaders = signV4(
      "POST",
      commitUrl,
      { "content-type": "text/plain;charset=UTF-8" },
      commitBody,
      creds,
    );
    const commitRes = await context.request.post(commitUrl.toString(), {
      headers: commitHeaders,
      data: commitBody,
      timeout: 30_000,
    });
    if (!commitRes.ok()) {
      throw new OutboundError("unknown", `CommitUploadInner 失败：HTTP ${commitRes.status()}`);
    }

    const commitText = await commitRes.text();
    const commitJson = JSON.parse(commitText) as {
      Result?: {
        Results?: {
          Uri?: string;
          SourceMd5?: string;
          Encryption?: { Uri?: string; SecretKey?: string; encryption_md5?: string; encryption_size?: number };
        }[];
        SecretKey?: string;
      };
      SecretKey?: string;
    };
    const committed = commitJson.Result?.Results?.[0];
    // 真机命中：发消息用的 oid/skey 是**加密后的 Uri / SecretKey**（Encryption 子对象）
    const encryption = committed?.Encryption;
    const oid = encryption?.Uri ?? committed?.Uri;
    // 有时平台不做服务端加密：这时没有 Encryption 子对象，也没有 skey（空串发出去试试）
    const skey = encryption?.SecretKey ?? commitJson.SecretKey ?? commitJson.Result?.SecretKey ?? "";
    if (!oid) {
      throw new OutboundError(
        "unknown",
        `CommitUploadInner 缺字段；itemKeys=${Object.keys((committed ?? {}) as object).join(",")}`,
      );
    }

    this.log.info(`图片上传完成：oid=${oid.slice(0, 24)}… size=${data.length}`);

    return {
      oid,
      skey,
      md5: createHash("md5").update(data).digest("hex"),
      size: data.length,
      width,
      height,
    };
  }

  /**
   * 取（并缓存）STS 凭证。
   *
   * 为什么必须在**页面里**发：`context.request` 直接打这个接口会被 WAF 判风险，
   * 回一个纯文本 "blocked"（2026-11-21 实测）；页面同源 fetch 带齐浏览器头与 cookie 就正常。
   */
  private async ensureCredentials(
    page: Page,
  ): Promise<StsCredentials & { spaceName: string; expireAt: number }> {
    const now = Date.now();
    if (this.creds && this.creds.expireAt * 1000 > now + 60_000) {
      return this.creds;
    }

    // 参数要与页面一致（缺了会被 WAF 判风险，直接返回 "blocked" —— 2026-11-21 实测）
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
        const text = await response.text();
        return { status: response.status, text };
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
      region: "cn-north-1",
      service: "vod",
      spaceName: cfg.space_name ?? "zhenzhen",
      expireAt: cfg.expire_at ?? Math.floor(Date.now() / 1000) + 3600,
    };

    return this.creds;
  }
}
