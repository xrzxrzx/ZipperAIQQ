/**
 * 出站执行（帧 `outbound` → 页面动作 → `ack`）。
 *
 * 三条硬规则（都是踩过的）：
 *   ① **回读校验**：点回车不等于发出去了。只有「回读到刚发的那条」才算成功，
 *      否则回 `readback` 失败 —— 宁可让模型知道没发成，也不能让它以为发了。
 *   ② **引用降级**：网页版按 id 定位消息不稳，一律降级成普通消息并带 `degraded:"reply_to"`。
 *   ③ **会话不许猜**：定位不到目标会话就失败（`not_found`），绝不点列表第一行凑数。
 */

import { mkdir, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import type { Page } from "playwright";
import type { BrowserLogger, DouyinBrowser } from "./browser.js";
import {
  OutboundError,
  type OutboundPlan,
  sessionIdFor,
} from "./protocol.js";
import {
  currentConversationName,
  listConversations,
  openChatPage,
  openConversation,
  readTail,
  sendImage,
  sendText,
  SITE,
  type ConversationNameCache,
} from "./site.js";

/** 回读等待上限：页面渲染 + 网络往返，8 秒还没出现就判失败。 */
const READBACK_TIMEOUT_MS = 8_000;

/** 回读轮询间隔。 */
const READBACK_POLL_MS = 500;

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
  /** 临时媒体目录（发完即删）。 */
  workDir: string;
}

/** 出站发送器。 */
export class OutboundSender {
  private readonly browser: DouyinBrowser;
  private readonly log: BrowserLogger;
  private readonly names: ConversationNameCache;
  private readonly workDir: string;

  constructor(options: OutboundSenderOptions) {
    this.browser = options.browser;
    this.log = options.log;
    this.names = options.names;
    this.workDir = options.workDir;
  }

  /** 按计划发送；失败抛异常（由调用方转 `ack.ok=false`）。 */
  async send(plan: OutboundPlan): Promise<OutboundResult> {
    const session = sessionIdFor(plan.chatType, plan.threadId);
    const page = await this.browser.ensureChatPage(SITE.chatUrl);

    if (!page.url().includes("/chat")) {
      throw new OutboundError("auth", `私信页打不开（当前 ${page.url()}）：登录态可能已失效`);
    }

    // 私信页刚打开时列表要一会儿才渲染出来
    await waitForList(page);

    const nameHint = this.names.get(plan.threadId);
    const row = await openConversation(page, { threadId: plan.threadId, nameHint });
    this.names.remember(plan.threadId, (await currentConversationName(page)) ?? row.name);

    this.log.info(`已打开会话：${session}（列表名=${row.name}）`);

    let chunks = 0;
    for (const chunk of plan.chunks) {
      // 发送前先记基线：判「新增」而不是「附近有像的东西」
      const before = await snapshotTail(page);
      await sendText(page, chunk);
      await expectNewBubble(page, before, chunk, false);
      chunks += 1;
    }

    let images = 0;
    const media = [
      ...plan.imageData.map((data) => ({ kind: "data" as const, data })),
      ...plan.imageUrls.map((url) => ({ kind: "url" as const, url })),
    ];

    for (const item of media) {
      const before = await snapshotTail(page);
      const file = await this.materialize(item);
      try {
        await sendImage(page, file);
        await expectNewBubble(page, before, "", true);
        images += 1;
      } finally {
        await rm(file, { force: true }).catch(() => undefined);
      }
    }

    // @ 没能还原：**必须**让服务端与模型知道（不是"发成功了"）
    const degraded = [...plan.degraded];
    if (plan.mentions.length > 0 && !degraded.includes("mention")) {
      degraded.push("mention");
    }

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

/** 等会话列表渲染出来（最多 15 秒）。 */
async function waitForList(page: Page): Promise<void> {
  const deadline = Date.now() + 15_000;

  while (Date.now() < deadline) {
    const rows = await listConversations(page).catch(() => []);
    if (rows.length > 0) {
      return;
    }

    await page.waitForTimeout(1_000);
  }

  throw new OutboundError("not_found", "私信页会话列表 15 秒内没有渲染出来（页面可能未登录 / 前端异常）");
}

/**
 * 发送前的气泡基线（判「新增」用）。
 *
 * 为什么必须记基线：只判「末尾有像的东西」时，
 * ① 同一句话重发一次会命中上一条 → 假成功 ② 上一条本来就是图 → 图片失败也判成功。
 */
interface TailSnapshot {
  count: number;
  images: number;
  texts: string[];
}

async function snapshotTail(page: Page): Promise<TailSnapshot> {
  const tail = await readTail(page, 12).catch(() => []);
  return {
    count: tail.length,
    images: tail.filter((bubble) => bubble.hasImage).length,
    texts: tail.map((bubble) => normalize(bubble.text)),
  };
}

/**
 * 回读校验：等到「**新增**的那条」出现。
 *
 * @param before 发送前基线
 * @param text 期望文本（图片时为 `""`）
 * @param expectImage 期望是图片气泡
 */
async function expectNewBubble(
  page: Page,
  before: TailSnapshot,
  text: string,
  expectImage: boolean,
): Promise<void> {
  const deadline = Date.now() + READBACK_TIMEOUT_MS;
  const expected = normalize(text);
  const probe = expected.slice(0, Math.min(60, expected.length));

  while (Date.now() < deadline) {
    const tail = await readTail(page, 12).catch(() => []);

    if (expectImage) {
      const images = tail.filter((bubble) => bubble.hasImage).length;
      if (images > before.images) {
        return;
      }
    } else if (probe.length > 0) {
      const texts = tail.map((bubble) => normalize(bubble.text));
      // 「末尾那段里出现次数变多」才算新增：先用基线里已有的次数减掉
      const beforeHits = before.texts.filter((value) => matches(value, probe)).length;
      const afterHits = texts.filter((value) => matches(value, probe)).length;

      if (afterHits > beforeHits) {
        return;
      }
    }

    await page.waitForTimeout(READBACK_POLL_MS);
  }

  throw new OutboundError(
    "readback",
    expectImage
      ? "图片发送后回读不到新气泡（可能被风控拦截或上传未完成）"
      : `发送后回读不到新出现的内容（前 40 字：${text.slice(0, 40)}）`,
  );
}

function matches(value: string, probe: string): boolean {
  if (!value || !probe) {
    return false;
  }

  return value.includes(probe) || probe.includes(value);
}

function normalize(text: string): string {
  return text.replace(/\s+/g, " ").trim();
}

function stripDataUrl(data: string): string {
  const index = data.indexOf("base64,");
  return index >= 0 ? data.slice(index + "base64,".length) : data;
}
