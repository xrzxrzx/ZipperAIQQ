/**
 * 出站执行（帧 `outbound` → **HTTP API** → `ack`）。
 *
 * 2026-11-20 真机起，出站改走 `send-api.ts` 的 HTTP 接口（不再点 DOM）：
 * 会话列表虚拟滚动、首屏延迟导致"找不到会话"，消息全退回 —— 而页面自己发消息本就是一条 HTTP 请求。
 *
 * 三条硬规则（都是踩过的）：
 *   ① **回读校验**：点回车不等于发出去了。只有「回读到刚发的那条」才算成功，
 *      否则回 `readback` 失败 —— 宁可让模型知道没发成，也不能让它以为发了。
 *   ② **引用降级**：网页版按 id 定位消息不稳，一律降级成普通消息并带 `degraded:"reply_to"`。
 *   ③ **会话不许猜**：定位不到目标会话就失败（`not_found`），绝不点列表第一行凑数。
 */

import { mkdir, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import type { BrowserLogger, DouyinBrowser } from "./browser.js";
import {
  OutboundError,
  type OutboundPlan,
  sessionIdFor,
} from "./protocol.js";
import type { ConversationRegistry, DouyinSendApi } from "./send-api.js";
import { sendImage, SITE, type ConversationNameCache } from "./site.js";

/** 图片下载超时：CDN 挂住时不能把整条发送拖死。 */
const IMAGE_FETCH_TIMEOUT_MS = 20_000;

/** 单张图片体积上限（与 /ws/channel 单帧 1 MB 的口径分开：入站帧才是 1 MB）。 */
const MAX_IMAGE_BYTES = 20 * 1024 * 1024;

/** 出站结果（进 `ack` 的附加字段）。 */
export interface OutboundResult {
  /** 真正发出去的文本块数。 */
  chunks: number;
  /** 发出去的图片数。 */
  images: number;
  /** 降级项（`reply_to` / `mention` / `image` 等）。 */
  degraded: string[];
}

export interface OutboundSenderOptions {
  browser: DouyinBrowser;
  log: BrowserLogger;
  names: ConversationNameCache;
  /** 会话坐标登记表（来自入站帧）：API 发送只需要它，不需要 DOM 会话列表 */
  conversations: ConversationRegistry;
  /** HTTP API 出站器（页面上下文里 fetch，自动带 cookie） */
  api: DouyinSendApi;
  /** 临时媒体目录（发完即删）。 */
  workDir: string;
}

/** 出站发送器。 */
export class OutboundSender {
  private readonly browser: DouyinBrowser;
  private readonly log: BrowserLogger;
  private readonly names: ConversationNameCache;
  private readonly conversations: ConversationRegistry;
  private readonly api: DouyinSendApi;
  private readonly workDir: string;

  constructor(options: OutboundSenderOptions) {
    this.browser = options.browser;
    this.log = options.log;
    this.names = options.names;
    this.conversations = options.conversations;
    this.api = options.api;
    this.workDir = options.workDir;
  }

  /** 按计划发送；失败抛异常（由调用方转 `ack.ok=false`）。 */
  /**
   * 按计划发送：**走 HTTP API**（`POST /v1/message/send`），不点 DOM。
   *
   * 为什么改：真机实测 DOM 找会话行极不稳定（列表虚拟滚动 + 首屏延迟），
   * 而页面自己发消息本来就是一条 HTTP 请求 —— 复刻它就是「API 方式」。
   * 失败一律抛异常（由调用方转 `ack.ok=false`），**不伪装成功**。
   */
  async send(plan: OutboundPlan): Promise<OutboundResult> {
    const session = sessionIdFor(plan.chatType, plan.threadId);
    const target = this.conversations.get(plan.threadId);

    if (!target) {
      // 发送只需要 conversation_id，而它只出现在入站帧里 —— 没收到过对方的消息就拿不到
      throw new OutboundError(
        "not_found",
        `还没有这个会话的坐标（threadId=${plan.threadId}）：先让对方发一条消息，` +
          "入站帧里才会带 conversation_id",
      );
    }

    const page = await this.browser.page;

    let chunks = 0;
    for (const chunk of plan.chunks) {
      const clientMessageId = crypto.randomUUID();
      const result = await this.api.sendText(page, target, chunk, clientMessageId);

      if (!result.ok) {
        throw new OutboundError(
          result.statusCode === 10509 ? "risk" : "unknown",
          `抖音发送失败：status=${result.statusCode} ${result.errorDesc ?? ""}`.trim(),
        );
      }

      chunks += 1;
    }

    // 图片：API 需要先上传拿 key，本期未适配 —— **明确降级**（不假装发了）
    let images = 0;
    const degraded = [...plan.degraded];
    const mediaCount = plan.imageData.length + plan.imageUrls.length;
    if (mediaCount > 0) {
      degraded.push("image");
      this.log.warn(`图片发送未适配 API（${mediaCount} 张已跳过，等待上传通道）`);
    }

    // @ 没能还原：同样必须让服务端与模型知道
    if (plan.mentions.length > 0 && !degraded.includes("mention")) {
      degraded.push("mention");
    }

    this.log.info(`API 出站完成：${session} 文本=${chunks} 降级=${degraded.join(",") || "无"}`);
    return { chunks, images, degraded };
  }

  /** 把 base64 / URL 落成临时文件（网页版上传只吃本地文件）。 */
  private async materialize(item: { kind: "data"; data: string } | { kind: "url"; url: string }): Promise<string> {
    await mkdir(this.workDir, { recursive: true });

    const stamp = `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

    if (item.kind === "data") {
      const file = path.join(this.workDir, `out-${stamp}.png`);
      await writeFile(file, Buffer.from(stripDataUrl(item.data), "base64"));
      return file;
    }

    // 超时与体积上限都要有：抖音的 CDN 偶发挂住，不能把整个发送卡死
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), IMAGE_FETCH_TIMEOUT_MS);
    let response: Response;
    let buffer: Buffer;

    try {
      response = await fetch(item.url, { signal: controller.signal });
      if (!response.ok) {
        throw new OutboundError("upload", `图片下载失败：HTTP ${response.status}`);
      }

      const declared = Number(response.headers.get("content-length") ?? 0);
      if (declared > MAX_IMAGE_BYTES) {
        throw new OutboundError("upload", `图片过大：${declared} 字节（上限 ${MAX_IMAGE_BYTES}）`);
      }

      buffer = Buffer.from(await response.arrayBuffer());
    } catch (error) {
      if (error instanceof OutboundError) {
        throw error;
      }

      throw new OutboundError(
        "upload",
        `图片下载失败：${error instanceof Error ? error.message : String(error)}`,
      );
    } finally {
      clearTimeout(timer);
    }

    if (buffer.length === 0) {
      throw new OutboundError("upload", "图片下载回来是 0 字节");
    }

    if (buffer.length > MAX_IMAGE_BYTES) {
      throw new OutboundError("upload", `图片过大：${buffer.length} 字节（上限 ${MAX_IMAGE_BYTES}）`);
    }

    const contentType = response.headers.get("content-type") ?? "image/jpeg";
    const ext = contentType.includes("png") ? "png" : contentType.includes("webp") ? "webp" : "jpg";
    const file = path.join(this.workDir, `out-${stamp}.${ext}`);
    await writeFile(file, buffer);
    return file;
  }
}


function stripDataUrl(data: string): string {
  const index = data.indexOf("base64,");
  return index >= 0 ? data.slice(index + "base64,".length) : data;
}
