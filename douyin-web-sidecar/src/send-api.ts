/**
 * 出站·**HTTP API 版**（2026-11-21 真机打通）。
 *
 * 一次发送 = 三步，全部有实测依据：
 *
 * 1. **会话模板**：页面每次轮询私信都会 POST 一次 `/v1/message/get_*`，
 *    请求体同样是 `im_proto.Request`，里面那张 `headers` 表（`session_aid` / `session_did` /
 *    `identity_security_token` / `user_agent` / `referer` …）就是**会话凭证**。
 *    我们旁听页面自己的请求，把最近一次的请求体当模板留着 —— 不需要点 DOM、不需要用户操作。
 * 2. **签名**：页面里的 `window.ucSecureConsumerSDK.sign({url, method, body, path})`
 *    返回 `bd-ticket-guard-*` 五件套（实测 `state=ready`、`cryptoUsage=system`）。
 * 3. **发送**：**在 Node 侧**用 `context.request.post()` 发 —— 它与浏览器共享 cookie，
 *    但不受页面 CORS / 安全 SDK 的 `blockFetch` 限制（页面内 fetch/XHR 一律被拦成 `Failed to fetch`）。
 *
 * 只换 `body.send_message_body`（会话 + 正文），其余字段照模板 —— 实测返回 `status_code=0 OK`。
 */

import { readFileSync, writeFileSync } from "node:fs";
import protobuf from "protobufjs";
import type { BrowserContext, Page } from "playwright";
import type { BrowserLogger } from "./browser.js";
import { OutboundError } from "./protocol.js";

/** 发送接口。 */
const SEND_PATH = "/v1/message/send";
const SEND_URL = "https://imapi.douyin.com" + SEND_PATH;

/** 页面里用来签名的 SDK 全局（实测存在）。 */
const SIGNER_GLOBAL = "ucSecureConsumerSDK";

/** `IMCMD.SEND_MESSAGE`。 */
const CMD_SEND_MESSAGE = 100;

/** 一次会话的可发送坐标（入站帧里学，出站只用它）。 */
export interface ConversationRef {
  conversationId: string;
  conversationShortId?: string;
  /** `im_proto` 的 conversation_type：1=私聊 2=群。 */
  conversationType: number;
  /** 最后一条**入站**文本（截断）；DOM 兜底定位会话行用。 */
  lastText?: string;
  /** 我们最后一次成功发出去的文本（截断）；再发下一条时预览已变成它。 */
  lastSent?: string;
}

/** 会话登记表：`threadId`(uid/gid) → 可发送坐标。 */
export class ConversationRegistry {
  private readonly map = new Map<string, ConversationRef>();

  remember(threadId: string, ref: ConversationRef): void {
    if (!threadId || !ref.conversationId) {
      return;
    }

    this.map.set(threadId, { ...this.map.get(threadId), ...ref });
  }

  get(threadId: string): ConversationRef | undefined {
    return this.map.get(threadId);
  }

  /** 记下「刚发出去的那条」——多段发送时下一段靠它定位（会话行预览已被它覆盖）。 */
  noteSent(threadId: string, text: string): void {
    const current = this.map.get(threadId);
    if (!current || !text) {
      return;
    }

    this.map.set(threadId, { ...current, lastSent: text.trim().slice(0, 40) });
  }

  get size(): number {
    return this.map.size;
  }
}

/** 发送结果（成功/失败都带平台原文，供 ack 如实回填）。 */
export interface SendApiResult {
  ok: boolean;
  /** 平台业务码（0 = 成功）。 */
  statusCode: number;
  /** 平台给的原因（失败时原样带出）。 */
  errorDesc?: string;
  /** 平台 logId（排查用，不是机密）。 */
  logId?: string;
}

/**
 * 会话模板：页面自己请求体里可复用的那部分（会话凭证 + 客户端标识）。
 *
 * 只保留**能复用**的字段；`sequence_id` / `body` / `cmd` 每次重算。
 */
interface SessionTemplate {
  sdkVersion?: string;
  refer?: number;
  buildNumber?: string;
  channel?: string;
  deviceId?: string;
  devicePlatform?: string;
  deviceType?: string;
  osVersion?: string;
  versionCode?: string;
  authType?: number;
  configId?: number;
  headers: Record<string, string>;
  /** 是否来自**发送家族**的请求体（`cmd=100`）——它的 headers 带全，最可信 */
  fromSend: boolean;
  capturedAt: number;
}

/**
 * 按模板构造发送请求体（**纯函数**，单测用它锁字段形状）。
 *
 * 模板给「会话凭证 + 客户端标识」，我们只覆盖 `sequence_id` 与 `body.send_message_body`。
 */
export function buildSendPayload(
  requestType: protobuf.Type,
  template: {
    sdkVersion?: string;
    refer?: number;
    inboxType?: number;
    buildNumber?: string;
    channel?: string;
    deviceId?: string;
    devicePlatform?: string;
    deviceType?: string;
    osVersion?: string;
    versionCode?: string;
    authType?: number;
    headers: Record<string, string>;
  },
  target: ConversationRef,
  text: string,
  clientMessageId: string,
  sequenceId: number,
  nowMs: number,
  extras: { mentions?: { userId: string; label: string }[]; replyTo?: string | null } = {},
): Buffer {
  // @：`mentioned_users` 是 int64 列表（真正的 @，不用敲 @ 再选人）
  const mentionedUsers = (extras.mentions ?? [])
    .map((mention) => Number(mention.userId))
    .filter((id) => Number.isFinite(id) && id > 0);

  // 引用：`ref_msg_info` 需要被引用消息的 serverMessageId（纯数字）
  const replyTo = extras.replyTo && /^\d+$/.test(extras.replyTo) ? extras.replyTo : null;
  const message = requestType.create({
    cmd: CMD_SEND_MESSAGE,
    sequenceId,
    sdkVersion: template.sdkVersion ?? "0.1.8",
    refer: template.refer ?? 3,
    // 发送家族固定 0：页面轮询用的是 1，照抄会被判 `invalid access param`（2026-11-21 实测）
    inboxType: 0,
    buildNumber: template.buildNumber ?? "0d50935:feat/pc-im-group",
    // 实测发送家族**不带 channel**；带了会被判 invalid access param
    channel: template.channel,
    deviceId: template.deviceId,
    devicePlatform: template.devicePlatform,
    deviceType: template.deviceType,
    osVersion: template.osVersion,
    versionCode: template.versionCode || "360000",
    authType: template.authType ?? 1,
    // 这两个字段平台会校验：缺了就是 `invalid access param`（2026-11-21 实测定位）
    biz: "douyin_web",
    access: "web_sdk",
    // 空串也要写（proto2 的存在性）
    token: "",
    headers: template.headers,
    body: {
      sendMessageBody: {
        conversationId: target.conversationId,
        conversationType: target.conversationType,
        conversationShortId: target.conversationShortId,
        content: JSON.stringify({ aweType: 700, type: 0, richTextInfos: [], text }),
        messageType: 7,
        mentionedUsers,
        refMsgInfo: replyTo ? { referencedMessageId: replyTo, hint: "" } : undefined,
        ext: {
          "s:client_message_id": clientMessageId,
          "s:stime": String(nowMs),
          "s:mentioned_users": "",
        },
      },
    },
  });

  return Buffer.from(requestType.encode(message).finish());
}

/**
 * 构造图片消息体（`message_type=27`，真机抓包确认的 content 结构）。
 *
 * content 形状：
 * `{resource_url:{oid,skey,data_size,md5}, md5, cover_width, cover_height, from_gallery:1, aweType:2702}`
 */
export function buildImagePayload(
  requestType: protobuf.Type,
  template: {
    sdkVersion?: string;
    refer?: number;
    buildNumber?: string;
    channel?: string;
    deviceId?: string;
    devicePlatform?: string;
    deviceType?: string;
    osVersion?: string;
    versionCode?: string;
    authType?: number;
    headers: Record<string, string>;
  },
  target: ConversationRef,
  image: { oid: string; skey: string; md5: string; size: number; width: number; height: number },
  clientMessageId: string,
  sequenceId: number,
  nowMs: number,
): Buffer {
  const content = JSON.stringify({
    resource_url: { oid: image.oid, skey: image.skey, data_size: image.size, md5: image.md5 },
    cover_width: image.width,
    cover_height: image.height,
    check_pics: [],
    md5: image.md5,
    from_gallery: 1,
    aweType: 2702,
  });

  const message = requestType.create({
    cmd: CMD_SEND_MESSAGE,
    sequenceId,
    sdkVersion: template.sdkVersion ?? "0.1.8",
    refer: template.refer ?? 3,
    inboxType: 0,
    buildNumber: template.buildNumber ?? "0d50935:feat/pc-im-group",
    channel: template.channel,
    deviceId: template.deviceId,
    devicePlatform: template.devicePlatform,
    deviceType: template.deviceType,
    osVersion: template.osVersion,
    versionCode: template.versionCode || "360000",
    authType: template.authType ?? 1,
    biz: "douyin_web",
    access: "web_sdk",
    token: "",
    headers: template.headers,
    body: {
      sendMessageBody: {
        conversationId: target.conversationId,
        conversationType: target.conversationType,
        conversationShortId: target.conversationShortId,
        content,
        messageType: 27,
        ext: {
          "s:client_message_id": clientMessageId,
          "s:stime": String(nowMs),
          "s:mentioned_users": "",
        },
      },
    },
  });

  return Buffer.from(requestType.encode(message).finish());
}

/** API 出站器（模板 + 签名 + Node 侧请求）。 */
export class DouyinSendApi {
  private readonly log: BrowserLogger;
  private readonly protoPath: string;
  private readonly seedPath?: string;
  private seed: Record<string, string> | null = null;
  private requestType: protobuf.Type | null = null;
  private template: SessionTemplate | null = null;
  private readonly attached = new WeakSet<BrowserContext>();

  constructor(options: { log: BrowserLogger; protoPath: string; seedPath?: string }) {
    this.log = options.log;
    this.protoPath = options.protoPath;
    this.seedPath = options.seedPath;
  }

  /**
   * 读「身份凭证种子」（`identity_security_token` 等）。
   *
   * 为什么要它：发送家族的 `headers` 里必须有 `identity_security_token`，
   * 而页面轮询接口的请求体里**没有**它（照抄会被判 `invalid access param`，2026-11-21 实测）。
   * 这些值来自一次真实发送（seed 文件），长时间有效；失效时平台会明确报错。
   */
  private loadSeed(): Record<string, string> {
    if (this.seed) {
      return this.seed;
    }

    this.seed = {};
    if (!this.seedPath) {
      return this.seed;
    }

    try {
      const raw = readFileSync(this.seedPath, "utf8");
      const parsed = JSON.parse(raw) as Record<string, unknown>;
      for (const [key, value] of Object.entries(parsed)) {
        if (typeof value === "string" && value.length > 0 && key.startsWith("identity_security_")) {
          this.seed[key] = value;
        }
      }

      this.log.info(`身份凭证种子已加载（${Object.keys(this.seed).length} 项）`);
    } catch {
      this.log.warn(`身份凭证种子不可用：${this.seedPath ?? "(未配置)"}`);
      this.seed = {};
    }

    return this.seed;
  }

  /** 懒加载 proto。 */
  async load(): Promise<void> {
    if (this.requestType) {
      return;
    }

    const root = await protobuf.load(this.protoPath);
    this.requestType = root.lookupType("im_proto.Request");
    this.log.info(`出站协议已加载：${this.protoPath}`);
  }

  /**
   * 挂上「采集会话模板」的旁听（幂等：同一个 context 只挂一次）。
   *
   * 页面每次轮询私信都会 POST `/v1/message/…`，我们只读它的**请求体**（不拦截、不改写）。
   */
  attach(context: BrowserContext): void {
    if (this.attached.has(context)) {
      return;
    }

    this.attached.add(context);
    context.on("request", (request) => {
      const url = request.url();
      if (!/imapi\.douyin\.com\/v\d+\/(message|conversation)\//.test(url) || request.method() !== "POST") {
        return;
      }

      const body = request.postDataBuffer();
      if (body && body.length > 0) {
        this.captureFromRequestBody(body, new URL(url).pathname);
      }
    });

    this.log.info("已挂上会话模板采集（旁听页面自己的 imapi 请求体）");
  }

  /**
   * 把 `identity_security_*` 落盘（token 会过期，靠真实发送自愈）。
   *
   * 为什么要落盘：模板只在内存里，重启后第一次发送就没凭证；
   * 存下来下次启动直接可用，等到真的失效再由一次真实发送刷新。
   */
  private persistSeed(headers: Record<string, string>): void {
    if (!this.seedPath) {
      return;
    }

    const picked: Record<string, string> = {};
    for (const [key, value] of Object.entries(headers)) {
      if (key.startsWith("identity_security_") && typeof value === "string") {
        picked[key] = value;
      }
    }

    if (!picked["identity_security_token"]) {
      return;
    }

    this.seed = { ...(this.seed ?? {}), ...picked };

    try {
      writeFileSync(this.seedPath, JSON.stringify({ refreshedAt: new Date().toISOString(), ...picked }, null, 2), "utf8");
      this.log.info(`身份凭证 seed 已刷新（${Object.keys(picked).length} 项）`);
    } catch (error) {
      this.log.warn(`seed 落盘失败：${error instanceof Error ? error.message : String(error)}`);
    }
  }

  /**
   * 兜底模板：页面没轮询过（WS 在线时可能完全不轮询）也能发。
   *
   * 这些 `headers` 的非敏感值来自真机抓包（2026-11-21），敏感凭证由 seed 提供；
   * `user_agent` / 屏幕尺寸从页面取，取不到就用抓包常量。
   */
  private fallbackTemplate(pageValues: Partial<Record<string, string>> = {}): SessionTemplate {
    const userAgent = pageValues.userAgent ?? "";

    return {
      sdkVersion: "0.1.8",
      refer: 3,
      buildNumber: "0d50935:feat/pc-im-group",
      devicePlatform: "douyin_pc",
      deviceId: "0",
      versionCode: "360000",
      authType: 1,
      fromSend: false,
      capturedAt: Date.now(),
      headers: {
        app_name: "douyin_pc",
        browser_language: pageValues.browserLanguage ?? "zh-CN",
        browser_name: "Mozilla",
        browser_online: "true",
        browser_platform: pageValues.browserPlatform ?? "Win32",
        browser_version: userAgent.replace(/^Mozilla\//, ""),
        cookie_enabled: "true",
        deviceId: "0",
        "is-retry": "0",
        priority_region: "cn",
        referer: "",
        screen_height: pageValues.screenHeight ?? "950",
        screen_width: pageValues.screenWidth ?? "1400",
        session_aid: "6383",
        session_did: "0",
        timezone_name: pageValues.timezone ?? "Asia/Shanghai",
        user_agent: userAgent,
        ...this.loadSeed(),
      },
    };
  }

  /** 从页面取兜底模板需要的环境值（取不到就用常量）。 */
  private async readPageEnv(page: Page): Promise<Partial<Record<string, string>>> {
    try {
      return await page.evaluate(() => ({
        userAgent: navigator.userAgent,
        browserLanguage: navigator.language,
        browserPlatform: navigator.platform,
        screenWidth: String(window.screen?.width ?? 1400),
        screenHeight: String(window.screen?.height ?? 950),
        timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      }));
    } catch {
      return {};
    }
  }

  /** 是否已有可用模板（没有就发不出去，要等页面自己请求一次）。 */
  get hasTemplate(): boolean {
    return this.template !== null;
  }

  /**
   * 从页面自己的 imapi 请求体里采集会话模板。
   *
   * 调用时机：`page.on("response")` 命中 `/v1/message/…` 或 `/v3/conversation/…` 时。
   * 好处：**不需要真实发一条消息**就有会话凭证（页面本来就在轮询）。
   */
  captureFromRequestBody(body: Buffer, path: string): void {
    if (!this.requestType || body.length === 0) {
      return;
    }

    try {
      const decoded = this.requestType.toObject(this.requestType.decode(body), {
        longs: String,
        bytes: Buffer,
        defaults: false,
      }) as Record<string, unknown>;

      const headers = (decoded.headers ?? {}) as Record<string, string>;
      if (!headers.session_did && !headers.identity_security_token) {
        // 没有会话凭证的请求体没用（例如匿名接口）
        return;
      }

      const cmd = Number(decoded.cmd ?? 0);
      const fromSend = cmd === CMD_SEND_MESSAGE;

      // 发送家族的请求体里带 identity_security_*：顺手把 seed 刷新并落盘（token 会过期）
      if (fromSend) {
        this.persistSeed(headers);
      }

      // 发送家族的模板更完整；已有发送家族模板时不被轮询体覆盖
      if (!fromSend && this.template?.fromSend) {
        return;
      }

      this.template = {
        sdkVersion: decoded.sdkVersion as string | undefined,
        refer: decoded.refer === undefined ? undefined : Number(decoded.refer),
        buildNumber: decoded.buildNumber as string | undefined,
        channel: decoded.channel as string | undefined,
        deviceId: decoded.deviceId as string | undefined,
        devicePlatform: decoded.devicePlatform as string | undefined,
        deviceType: decoded.deviceType as string | undefined,
        osVersion: decoded.osVersion as string | undefined,
        versionCode: decoded.versionCode as string | undefined,
        authType: decoded.authType === undefined ? undefined : Number(decoded.authType),
        configId: decoded.configId === undefined ? undefined : Number(decoded.configId),
        headers,
        fromSend,
        capturedAt: Date.now(),
      };

      this.log.info(
        `会话模板已采集：${path}（${fromSend ? "发送家族" : "轮询家族"}，headers=${Object.keys(headers).length} 项）`,
      );
    } catch (error) {
      this.log.debug(`会话模板采集失败（${path}）：${error instanceof Error ? error.message : String(error)}`);
    }
  }

  /**
   * 发一条文本。
   *
   * @throws {OutboundError} 模板缺失 / 签名失败 / 网络失败
   */
  async sendText(
    context: BrowserContext,
    page: Page,
    target: ConversationRef,
    text: string,
    clientMessageId: string,
    extras: { mentions?: { userId: string; label: string }[]; replyTo?: string | null } = {},
  ): Promise<SendApiResult> {
    await this.load();

    const requestType = this.requestType;
    if (!requestType) {
      throw new OutboundError("page", "出站协议未加载");
    }

    // 有页面采到的模板优先（它的 headers 最贴近真实）；没有就用兜底模板 + seed
    const template = this.template ?? this.fallbackTemplate(await this.readPageEnv(page));
    if (!template.fromSend && !this.loadSeed()["identity_security_token"]) {
      throw new OutboundError(
        "not_found",
        "既没有页面采到的会话模板，也没有身份凭证 seed —— 先让页面请求一次私信接口",
      );
    }

    const sequenceId = Date.now() % 2_000_000_000;
    const payload = buildSendPayload(
      requestType,
      // 发送家族必须有 identity_security_token：模板（轮询体）里没有，从 seed 补
      { ...template, headers: { ...template.headers, ...this.loadSeed() } },
      target,
      text,
      clientMessageId,
      sequenceId,
      Date.now(),
      extras,
    );

    // 文本与图片共用同一套「签名（可选）+ 纯 cookie 发送」
    const result = await this.postSigned(context, page, payload);
    if (result.ok) {
      this.log.debug(`API 发送成功：seq=${sequenceId} conv=${target.conversationId} logId=${result.logId ?? "-"}`);
    }

    return result;
  }

  /**
   * 发一张已上传的图片（`message_type=27`）。
   *
   * 图片字节的上传在 `image-upload.ts`（SigV4 + TOS）；这里只负责「发」。
   */
  async sendImage(
    context: BrowserContext,
    page: Page,
    target: ConversationRef,
    image: { oid: string; skey: string; md5: string; size: number; width: number; height: number },
    clientMessageId: string,
  ): Promise<SendApiResult> {
    await this.load();

    const requestType = this.requestType;
    if (!requestType) {
      throw new OutboundError("page", "出站协议未加载");
    }

    const template = this.template ?? this.fallbackTemplate(await this.readPageEnv(page));
    const merged = { ...template, headers: { ...template.headers, ...this.loadSeed() } };
    const sequenceId = Date.now() % 2_000_000_000;
    const payload = buildImagePayload(
      requestType,
      merged,
      target,
      image,
      clientMessageId,
      sequenceId,
      Date.now(),
    );

    return this.postSigned(context, page, payload);
  }

  /** 签名 + `context.request` 发送（文本与图片共用）。 */
  private async postSigned(context: BrowserContext, page: Page, payload: Buffer): Promise<SendApiResult> {
    // 签名器是**可选**的：真机上有的会话/版本根本不强制 ticket-guard
    // （2026-11-21 实测：新号会话下不带任何签名头，/v1/message/send 直接 status_code=0）
    for (let i = 0; i < 5; i += 1) {
      const ready = await page
        .evaluate((name) => {
          const signer = (window as unknown as Record<string, unknown>)[name] as { sign?: unknown } | undefined;
          return Boolean(signer && typeof signer.sign === "function");
        }, SIGNER_GLOBAL)
        .catch(() => false);

      if (ready) {
        break;
      }

      await page.waitForTimeout(1000);
    }

    const bodyB64 = payload.toString("base64");
    const signed = await page.evaluate(
      async ({ url, path, b64, signerGlobal }) => {
        const signer = (window as unknown as Record<string, unknown>)[signerGlobal] as
          | { sign?: (input: unknown) => unknown }
          | undefined;
        if (!signer || typeof signer.sign !== "function") {
          return { error: "签名器不可用" };
        }

        const bytes = Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
        let result = signer.sign({ url, method: "POST", body: bytes, path });
        if (result && typeof (result as Promise<unknown>).then === "function") {
          result = await result;
        }

        return { headers: ((result as { headers?: Record<string, string> }) ?? {}).headers ?? {} };
      },
      { url: SEND_URL, path: SEND_PATH, b64: bodyB64, signerGlobal: SIGNER_GLOBAL },
    );

    let signHeaders: Record<string, string> = {};
    if ("error" in signed && signed.error) {
      this.log.warn(`签名器不可用，按纯 cookie 发送：${String(signed.error)}`);
    } else {
      signHeaders = ("headers" in signed ? (signed.headers as Record<string, string> | undefined) : undefined) ?? {};
      if (Object.keys(signHeaders).length === 0) {
        this.log.info("签名头为空，按纯 cookie 发送（真机验证该接口接受）");
      }
    }

    const response = await context.request.post(SEND_URL, {
      headers: { "content-type": "application/x-protobuf", ...signHeaders },
      data: payload,
      timeout: 20_000,
    });

    if (response.status() !== 200) {
      return { ok: false, statusCode: response.status(), errorDesc: `HTTP ${response.status()}` };
    }

    const decoded = this.decodeResponse(await response.body());
    return decoded.statusCode === 0
      ? { ok: true, statusCode: 0, logId: decoded.logId }
      : { ok: false, statusCode: decoded.statusCode, errorDesc: decoded.errorDesc, logId: decoded.logId };
  }

  /** 解 `im_proto.Response`（只取业务码/原因/logId）。 */
  private decodeResponse(raw: Buffer): { statusCode: number; errorDesc?: string; logId?: string } {
    const requestType = this.requestType;
    if (!requestType?.root) {
      return { statusCode: -1, errorDesc: "proto 未加载" };
    }

    try {
      const responseType = requestType.root.lookupType("im_proto.Response");
      const decoded = responseType.toObject(responseType.decode(raw), {
        longs: String,
        bytes: Buffer,
        defaults: false,
      }) as { statusCode?: number; errorDesc?: string; logId?: string };

      return {
        statusCode: Number(decoded.statusCode ?? 0),
        errorDesc: decoded.errorDesc,
        logId: decoded.logId,
      };
    } catch (error) {
      return {
        statusCode: -1,
        errorDesc: `响应解不出来：${error instanceof Error ? error.message : String(error)}`,
      };
    }
  }
}
