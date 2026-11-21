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
import { ConversationRegistry, DouyinSendApi } from "./send-api.js";
import { DouyinImageUploader } from "./image-upload.js";
import {
  OutboundError,
  type OutboundPlan,
  sessionIdFor,
} from "./protocol.js";

import {
  clickConversationRow,
  findConversationRowByAny,
  isConversationOpen,
  reloadChatPage,
  sendImageInOpenConversation,
  sendTextInOpenConversation,
  uploadImageViaPageApiAware,
  SITE,
  type ConversationNameCache,
} from "./site.js";

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
  /** 文本实际走的路（`api` = HTTP 接口，`dom` = 页面操作）。 */
  via: "api" | "dom";
  /** 平台业务码（0 = 成功；DOM 路径没有这个码，给 `null`）。 */
  status: number | null;
}

export interface OutboundSenderOptions {
  browser: DouyinBrowser;
  log: BrowserLogger;
  names: ConversationNameCache;
  /** 会话坐标登记表（来自入站帧）：API 发送只需要它，不需要 DOM 会话列表 */
  conversations: ConversationRegistry;
  /** HTTP API 出站器（页面上下文里 fetch，自动带 cookie） */
  api: DouyinSendApi;
  /** 图片上传器（SigV4 + TOS，纯 API） */
  uploader: DouyinImageUploader;
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
  private readonly uploader: DouyinImageUploader;
  private readonly workDir: string;

  constructor(options: OutboundSenderOptions) {
    this.browser = options.browser;
    this.log = options.log;
    this.names = options.names;
    this.conversations = options.conversations;
    this.api = options.api;
    this.uploader = options.uploader;
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
      throw new OutboundError(
        "not_found",
        `还没有这个会话的坐标（threadId=${plan.threadId}）：先让对方发一条消息，入站帧里才会带 conversation_id`,
      );
    }

    const page = await this.browser.page;
    const context = this.browser.browserContext;
    let via: "api" | "dom" = "api";
    let status: number | null = null;

    // 挂号：旁听页面自己的 imapi 请求体，攒「会话凭证模板」
    this.api.attach(context);

    // ① 首选：HTTP API（模板 + ticket-guard 签名 + Node 侧请求），**不点 DOM**
    try {
      let chunks = 0;
      for (const chunk of plan.chunks) {
        const result = await this.api.sendText(context, page, target, chunk, crypto.randomUUID(), {
          mentions: plan.mentions,
          replyTo: plan.replyTo,
        });
        status = result.statusCode;
        if (!result.ok) {
          throw new OutboundError(
            result.statusCode === 10509 ? "risk" : "unknown",
            `抖音发送失败：status=${result.statusCode} ${result.errorDesc ?? ""}`.trim(),
          );
        }

        chunks += 1;
      }

      const images = await this.sendImages(page, plan, target);
      const degraded = this.collectDegraded(plan, images.sent);
      this.log.info(
        `API 出站完成：${session} 文本=${chunks} 图片=${images.sent} 降级=${degraded.join(",") || "无"}`,
      );
      return { chunks, images: images.sent, degraded, via, status: status ?? 0 };
    } catch (error) {
      const apiFailure = error instanceof Error ? error.message : String(error);
      via = "dom";
      this.log.warn(`API 出站失败，改用 DOM 兜底：${apiFailure}`);

      // ② 兜底：DOM（重载 → 按线索定位会话 → 点开 → 输入 → 回读）
      try {
        const chunks = await this.sendByDom(page, plan, target);
        const images = await this.sendImages(page, plan, target);
        const degraded = this.collectDegraded(plan, images.sent);
        this.log.info(
          `DOM 出站完成：${session} 文本=${chunks} 图片=${images.sent} 降级=${degraded.join(",") || "无"}`,
        );
        return { chunks, images: images.sent, degraded, via, status: null };
      } catch (domError) {
        const domFailure = domError instanceof Error ? domError.message : String(domError);
        throw new OutboundError(
          domError instanceof OutboundError ? domError.code : "unknown",
          `抖音发送失败（API：${apiFailure}；DOM：${domFailure}）`,
        );
      }
    }
  }

  /**
   * 发图片（DOM 通道）：落盘 → 确保会话已打开 → 塞文件 → 点「发送（N）」。
   *
   * 一张失败不拖垮整条消息：失败的那几张计入 `degraded:"image"`。
   */
  private async sendImages(
    page: Awaited<ReturnType<DouyinBrowser["ensureChatPage"]>>,
    plan: OutboundPlan,
    target: { lastText?: string; lastSent?: string },
  ): Promise<{ sent: number }> {
    const media = [
      ...plan.imageData.map((data) => ({ kind: "data" as const, data })),
      ...plan.imageUrls.map((url) => ({ kind: "url" as const, url })),
    ];

    if (media.length === 0) {
      return { sent: 0 };
    }

    let sent = 0;
    const context = this.browser.browserContext;

    for (const item of media) {
      const file = await this.materialize(item);
      try {
        // ① **纯 API**：上传（STS + SigV4 + TOS + Commit 加密）→ 发 message_type=27
        //    关键：`s` 随机 11 位、签名不带 host、Commit 带 Functions 加密配置（对齐旧基座）
        const uploaded = await this.uploader.upload(context, page, file);
        const result = await this.api.sendImage(context, page, target as never, uploaded, crypto.randomUUID());

        if (result.ok) {
          this.conversations.noteSent(plan.threadId, "[图片]");
          sent += 1;
          this.log.info(`图片已用 API 发出（skey ${uploaded.skey.length} 字符）`);
          continue;
        }

        this.log.warn(`API 发图被拒：status=${result.statusCode} ${result.errorDesc ?? ""}`);
      } catch (error) {
        this.log.warn(`API 发图失败，改用 DOM 兜底：${error instanceof Error ? error.message : String(error)}`);
      }

      // ② 兜底：DOM（塞文件 → 点「发送（N）」）
      try {
        const opened = await this.openConversationForMedia(page, plan, target);
        if (opened && (await sendImageInOpenConversation(page, file))) {
          this.conversations.noteSent(plan.threadId, "[图片]");
          sent += 1;
        } else {
          this.log.warn("图片没发出去（API 与 DOM 都没成功）");
        }
      } catch (error) {
        this.log.warn(`DOM 发图异常：${error instanceof Error ? error.message : String(error)}`);
      } finally {
        await rm(file, { force: true }).catch(() => undefined);
      }
    }

    return { sent };
  }

  /**
   * 为发图打开会话：重载 → 按线索找行 → 点开 → 断言输入框出现。
   *
   * 为什么要单独一条：文本走 API 时不碰 DOM，页面可能从没打开过这个会话；
   * 而发图必须在「已打开会话」的输入区里塞文件。
   */
  private async openConversationForMedia(
    page: Awaited<ReturnType<DouyinBrowser["ensureChatPage"]>>,
    plan: OutboundPlan,
    target: { lastText?: string; lastSent?: string },
  ): Promise<boolean> {
    const nameHint = this.names.get(plan.threadId);
    const needles = [target.lastSent, target.lastText, nameHint].filter(
      (value): value is string => Boolean(value && value.trim().length > 0),
    );

    for (let attempt = 0; attempt < 3; attempt += 1) {
      if (await isConversationOpen(page, needles)) {
        return true;
      }

      await reloadChatPage(page).catch(() => undefined);
      const row = await findConversationRowByAny(page, needles);
      if (row) {
        await clickConversationRow(page, row);
      } else if (nameHint) {
        // 列表行标题就是昵称：直接用 Playwright 文本定位（比坐标稳）
        await page.locator(`text=${nameHint}`).first().click({ timeout: 8_000 }).catch(() => undefined);
        await page.waitForTimeout(2_000);
      }

      if (await isConversationOpen(page, needles)) {
        return true;
      }

      await page.waitForTimeout(2_000);
    }

    return false;
  }

  /** 两条路共用的降级清单（@ 与图片按实际结果留痕）。 */
  private collectDegraded(plan: OutboundPlan, imagesSent = 0): string[] {
    const degraded = [...plan.degraded];

    const mediaTotal = plan.imageData.length + plan.imageUrls.length;
    if (imagesSent < mediaTotal && !degraded.includes("image")) {
      degraded.push("image");
    }

    // @ 与引用现在会真正写进请求（mentioned_users / ref_msg_info），
    // 只有「拿不到可用的用户 id / 消息 id」才算降级
    const usableMentions = plan.mentions.filter((m) => /^\d+$/.test(m.userId)).length;
    if (plan.mentions.length > 0 && usableMentions < plan.mentions.length && !degraded.includes("mention")) {
      degraded.push("mention");
    }

    if (plan.replyTo && !/^\d+$/.test(plan.replyTo) && !degraded.includes("reply")) {
      degraded.push("reply");
    }

    return degraded;
  }

  /**
   * DOM 兜底发送（**API 不通时**才走）。
   *
   * 顺序：重载 /chat → 用「刚收到的消息预览」找到会话行 → 点开 → 输入 → 回读确认。
   * 每一步失败都抛，不猜、不点错人。
   */
  private async sendByDom(
    page: Awaited<ReturnType<DouyinBrowser["ensureChatPage"]>>,
    plan: OutboundPlan,
    target: { lastText?: string; lastSent?: string },
  ): Promise<number> {
    await this.browser.ensureChatPage(SITE.chatUrl);

    const nameHint = this.names.get(plan.threadId);
    // 线索从「最新」到「最旧」：刚发的那条 → 入站那条 → 昵称
    const needles = [target.lastSent, target.lastText, nameHint].filter(
      (value): value is string => Boolean(value && value.trim().length > 0),
    );

    // 已经停在目标会话里就直接发（多段发送时**不要 reload**：会把会话关掉，
    // 而此刻行的预览已经变成我们刚发的那条，重载后再按旧线索找不到 —— 2026-11-21 真机）
    const alreadyOpen = await isConversationOpen(page, needles);

    if (!alreadyOpen) {
      await reloadChatPage(page);
      let row = await findConversationRowByAny(page, needles);

      if (!row) {
        // 列表可能还没渲染完：再重载一次（实测这一下最有效）
        await reloadChatPage(page);
        row = await findConversationRowByAny(page, needles);
      }

      if (!row) {
        throw new OutboundError(
          "not_found",
          `DOM 兜底找不到会话（threadId=${plan.threadId}，线索=${needles.map((n) => n.slice(0, 10)).join("/") || "无"}）`,
        );
      }

      await clickConversationRow(page, row);
    }

    let sent = 0;
    for (const chunk of plan.chunks) {
      const ok = await sendTextInOpenConversation(page, chunk);
      if (!ok) {
        throw new OutboundError("readback", `DOM 发送后没在会话里看到刚发的那条（前 20 字：${chunk.slice(0, 20)}）`);
      }

      // 发出去之后，行的预览就变成它了 —— 记下来给下一段用
      this.conversations.noteSent(plan.threadId, chunk);
      sent += 1;
    }

    return sent;
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
