/**
 * 出站·**HTTP API 版**（2026-11-20 真机抓包确认的正式路径）。
 *
 * 为什么不用点 DOM：`/chat` 的消息列表是虚拟滚动 + 首屏延迟，用 DOM 找会话行极不稳定
 * （真机实测：一次扫到「拉链」，一次只有「抖音聊天」头 → `not_found`，消息全退回）。
 * 而页面自己发消息走的是**一条普通 HTTP 请求**：
 *
 * ```
 * POST https://imapi.douyin.com/v1/message/send?verifyFp=…&fp=…&msToken=…
 * Content-Type: application/x-protobuf
 * Body: im_proto.Request{ cmd=100, sequence_id, sdk_version="0.1.8",
 *                        build_number="0d50935:feat/pc-im-group",
 *                        body.send_message_body{ conversation_id, conversation_type,
 *                          conversation_short_id, content(JSON), ext{s:client_message_id,s:stime} } }
 * ```
 *
 * 关键点（都是实测结论）：
 * - **鉴权靠 cookie**：请求头只有 `content-type` 与 `referer`，没有自定义 token 头
 * - **必须在页面上下文里发**（`page.evaluate(fetch)`）：cookie、`msToken`、指纹都由浏览器提供，
 *   我们**不自己造签名**
 * - `conversation_id` 来自**入站帧**（`0:1:<self>:<peer>`），所以出站不再依赖会话列表
 */

import protobuf from "protobufjs";
import type { Page } from "playwright";
import type { BrowserLogger } from "./browser.js";
import { OutboundError } from "./protocol.js";

/** 发送接口地址（实测）。 */
const SEND_URL = "https://imapi.douyin.com/v1/message/send";

/** `IMCMD.SEND_MESSAGE`（实测 100）。 */
const CMD_SEND_MESSAGE = 100;

/** 页面自己上报的客户端标识（照抄实测值；平台用它做兼容判断，别乱改）。 */
const SDK_VERSION = "0.1.8";
const BUILD_NUMBER = "0d50935:feat/pc-im-group";

/** 一次会话的可发送坐标（全部来自入站帧，不靠 DOM）。 */
export interface ConversationRef {
  conversationId: string;
  conversationShortId?: string;
  /** `im_proto` 的 conversation_type：1=私聊 2=群。 */
  conversationType: number;
}

/**
 * 会话登记表：`threadId`(uid/gid) → 可发送坐标。
 *
 * 为什么要它：发送请求只要 `conversation_id`，而它只出现在**入站帧**里。
 * 所以「先收到过对方的消息」才发得出去 —— 拿不到就明确报 `not_found`，不猜。
 */
export class ConversationRegistry {
  private readonly map = new Map<string, ConversationRef>();

  remember(threadId: string, ref: ConversationRef): void {
    if (!threadId || !ref.conversationId) {
      return;
    }

    this.map.set(threadId, ref);
  }

  get(threadId: string): ConversationRef | undefined {
    return this.map.get(threadId);
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
}

/**
 * 构造发送请求的二进制体（**纯函数**，单测用它锁字段形状）。
 *
 * @param requestType `im_proto.Request`
 * @param target 会话坐标
 * @param text 文本
 * @param clientMessageId 幂等键（UUID）
 * @param sequenceId 序列号（配对响应）
 * @param nowMs 时间戳（`s:stime`）
 */
export function encodeSendRequest(
  requestType: protobuf.Type,
  target: ConversationRef,
  text: string,
  clientMessageId: string,
  sequenceId: number,
  nowMs: number,
): Buffer {
  const content = JSON.stringify({
    aweType: 700,
    type: 0,
    richTextInfos: [],
    text,
  });

  const message = requestType.create({
    cmd: CMD_SEND_MESSAGE,
    sequenceId,
    sdkVersion: SDK_VERSION,
    refer: 3,
    buildNumber: BUILD_NUMBER,
    body: {
      sendMessageBody: {
        conversationId: target.conversationId,
        conversationType: target.conversationType,
        conversationShortId: target.conversationShortId,
        content,
        messageType: 7,
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

/** 带重试语义的 API 出站器。 */
export class DouyinSendApi {
  private readonly log: BrowserLogger;
  private readonly protoPath: string;
  private requestType: protobuf.Type | null = null;
  private responseType: protobuf.Type | null = null;

  constructor(options: { log: BrowserLogger; protoPath: string }) {
    this.log = options.log;
    this.protoPath = options.protoPath;
  }

  /** 懒加载 proto（首次发送才读盘）。 */
  async load(): Promise<void> {
    if (this.requestType && this.responseType) {
      return;
    }

    const root = await protobuf.load(this.protoPath);
    this.requestType = root.lookupType("im_proto.Request");
    this.responseType = root.lookupType("im_proto.Response");
    this.log.info(`出站协议已加载：${this.protoPath}`);
  }

  /**
   * 发一条文本（页面上下文里 fetch，自动带 cookie 与指纹）。
   *
   * @throws {OutboundError} 网络/页面异常；平台拒绝返回 `ok=false`（由调用方转 ack）
   */
  async sendText(
    page: Page,
    target: ConversationRef,
    text: string,
    clientMessageId: string,
  ): Promise<SendApiResult> {
    await this.load();

    const requestType = this.requestType;
    const responseType = this.responseType;
    if (!requestType || !responseType) {
      throw new OutboundError("page", "出站协议未加载");
    }

    const sequenceId = Date.now() % 2_000_000_000;
    const payload = encodeSendRequest(
      requestType,
      target,
      text,
      clientMessageId,
      sequenceId,
      Date.now(),
    );

    const payloadB64 = payload.toString("base64");

    const raw = await page.evaluate(
      async ({ url, bodyB64 }) => {
        // 反爬参数从 cookie 取（页面自己也在用这套）
        const readCookie = (name: string): string => {
          const hit = document.cookie
            .split(";")
            .map((part) => part.trim())
            .find((part) => part.startsWith(name + "="));
          return hit ? decodeURIComponent(hit.slice(name.length + 1)) : "";
        };

        const fp = readCookie("s_v_web_id");
        const msToken = readCookie("msToken");
        const query = new URLSearchParams();
        if (fp) {
          query.set("verifyFp", fp);
          query.set("fp", fp);
        }
        if (msToken) {
          query.set("msToken", msToken);
        }

        const bytes = Uint8Array.from(atob(bodyB64), (c) => c.charCodeAt(0));
        const fullUrl = query.toString().length > 0 ? `${url}?${query.toString()}` : url;

        const resp = await fetch(fullUrl, {
          method: "POST",
          credentials: "include",
          headers: { "content-type": "application/x-protobuf" },
          body: bytes,
        });

        const buf = new Uint8Array(await resp.arrayBuffer());
        let binary = "";
        for (let i = 0; i < buf.length; i += 1) {
          binary += String.fromCharCode(buf[i] as number);
        }

        return { status: resp.status, bodyB64: btoa(binary) };
      },
      { url: SEND_URL, bodyB64: payloadB64 },
    );

    if (raw.status !== 200) {
      return { ok: false, statusCode: raw.status, errorDesc: `HTTP ${raw.status}` };
    }

    let decoded: { cmd?: number; statusCode?: number; errorDesc?: string };
    try {
      decoded = responseType.toObject(
        responseType.decode(Buffer.from(raw.bodyB64, "base64")),
        { longs: String, bytes: Buffer, defaults: false },
      ) as typeof decoded;
    } catch (error) {
      throw new OutboundError(
        "unknown",
        `发送响应解不出来：${error instanceof Error ? error.message : String(error)}`,
      );
    }

    const statusCode = Number(decoded.statusCode ?? 0);
    if (statusCode !== 0) {
      return {
        ok: false,
        statusCode,
        errorDesc: decoded.errorDesc || `status=${statusCode}`,
      };
    }

    this.log.debug(`API 发送成功：seq=${sequenceId} conv=${target.conversationId}`);
    return { ok: true, statusCode: 0 };
  }
}
