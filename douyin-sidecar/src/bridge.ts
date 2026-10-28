#!/usr/bin/env node
/**
 * ZipperAIQQ 抖音 sidecar（桥）。
 *
 * 职责：把 `douyin-im` 的私聊 / 群聊事件翻成 Server 的 `/ws/channel` 帧，
 * 并把 Server 下发的出站指令落成真实发送（设计 §2.1 拓扑、§3 协议、§4 映射）。
 *
 * 分层（**副作用集中**，便于以后单测）：
 *   ① 翻译层 —— `normalizeInbound` / `toInboundFrame` / `buildOutboundPlan` 等**纯函数**，
 *      不碰网络、不碰 douyin-im，可直接喂假数据测（设计 §9）。
 *   ② `DouyinImAdapter` —— **唯一** import 并调用 douyin-im 的地方。
 *   ③ `ChannelLink` —— **唯一**碰 WebSocket 的地方：连接 / 退避重连 / 心跳。
 *   ④ `main` —— 把 ①②③ 接起来 + 帧分发（outbound → ack、query.* → result）。
 *
 * 为什么是 sidecar：`douyin-im` 是 Node 22.5+ / ESM / `node:sqlite`，.NET 调不动（设计 §1.3）。
 * 为什么 sidecar **主动连入** Server：Server 常驻、sidecar 可重启，主动连入省掉端口分配，
 * 且断线能自己重连（设计 §2.2）。
 *
 * 鉴权：**Bearer token 走 query 参数**（`?token=...`）。选 query 而不是 header 的原因：
 * Node 内置的全局 `WebSocket`（WHATWG API）**不支持自定义请求头**，
 * 用 header 就得额外引入 `ws` 依赖；而 loopback 部署本来免 token（设计 §6.2）。
 * 详见 README「鉴权」一节（含"改用 header 要动哪一行"）。
 */

import { readFile } from "node:fs/promises";
import { existsSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import path from "node:path";
import process from "node:process";

// ═══════════════════════════ 常量 ═══════════════════════════

/** 协议版本（hello 帧里声明；与 Server 侧 `DouyinProtocol` 对齐）。 */
const PROTOCOL_VERSION = "1";

/** 平台标识（Server 侧 `ChatRef.Platform` / 会话前缀都用它）。 */
const PLATFORM = "douyin";

/** 能力集（设计 §3.3 hello 帧）。 */
/** 登录相关事件名（douyin-im 1.1.0 的 system.login.* / system.online）。 */
const LOGIN_EVENTS = [
  "system.login.qrcode",
  "system.login.sms",
  "system.login.voice",
  "system.login.verification",
  "system.login.error",
  "system.online",
] as const;

const CAPABILITIES = ["text", "image", "mention", "reply"] as const;

const DEFAULT_SERVER_URL = "ws://127.0.0.1:5180/ws/channel";
const DEFAULT_DATA_DIR = "data";
const DEFAULT_MAX_TEXT_LENGTH = 16_000;
const DEFAULT_HEARTBEAT_MS = 30_000;
const DEFAULT_DEAD_AFTER_MS = 90_000;

/** 重连退避：1s 起步，翻倍封顶 60s（设计 §2.2「断了自动重连」）。 */
const RECONNECT_INITIAL_MS = 1_000;
const RECONNECT_MAX_MS = 15_000;

/** WebSocket.OPEN 的字面值：不依赖 DOM 的 lib，避免额外 tsconfig lib 配置。 */
const SOCKET_OPEN = 1;

/** 入站去重窗口：Server 侧也会按 messageId 去重（设计 §3.4），这里只是省一次往返。 */
const SEEN_MESSAGE_CAP = 500;

// ═══════════════════════════ 基础工具 ═══════════════════════════

type AnyFn = (this: unknown, ...args: unknown[]) => unknown;

function str(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0 ? value : undefined;
}

function num(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null ? (value as Record<string, unknown>) : null;
}

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

/** 把任意抛出物转成一句可读原因 —— 出站失败要原样回给模型（设计 §10 #6）。 */
function describeError(err: unknown): string {
  if (err instanceof Error) {
    return err.message.length > 0 ? err.message : err.name;
  }

  const text = str(err);
  if (text) {
    return text;
  }

  try {
    return JSON.stringify(err) ?? String(err);
  } catch {
    return String(err);
  }
}

function truncate(text: string, maxLength: number): string {
  return text.length <= maxLength ? text : `${text.slice(0, maxLength)}…`;
}

/** 打印 URL 时抹掉 token —— token **不落日志、不回显**（设计 §6.2）。 */
function redactToken(url: string): string {
  return url.replace(/([?&](?:token|access_token)=)[^&]*/gi, "$1***");
}

/**
 * 平台时间戳 → UTC Unix 毫秒。
 *
 * 有的实现给秒、有的给毫秒，还有的给 ISO 字符串：`< 1e12` 一律按秒处理
 * （1e12 毫秒 ≈ 2001 年，秒级时间戳要到 33658 年才会追上来）。
 */
function toEpochMs(value: unknown): number | undefined {
  const numeric = num(value);
  if (numeric !== undefined) {
    if (numeric <= 0) {
      return undefined;
    }

    return numeric < 1e12 ? Math.round(numeric * 1000) : Math.round(numeric);
  }

  const text = str(value);
  if (!text) {
    return undefined;
  }

  const parsed = Date.parse(text);
  return Number.isNaN(parsed) ? undefined : parsed;
}

/** 群 `@` 的 uid 必须是数字（设计 §10 #7），非数字的降级成纯文本。 */
function isNumericUid(uid: string): boolean {
  return /^\d+$/.test(uid);
}

// ═══════════════════════════ 日志 ═══════════════════════════

type LogLevel = "debug" | "info" | "warn" | "error";

const LOG_LEVELS: Record<LogLevel, number> = { debug: 10, info: 20, warn: 30, error: 40 };

interface Logger {
  debug(message: string, extra?: unknown): void;
  info(message: string, extra?: unknown): void;
  warn(message: string, extra?: unknown): void;
  error(message: string, extra?: unknown): void;
  /**
   * 需要**人**做的事（登录验证 URL、扫码提示）。
   *
   * 只走 stdout：远程宿主会把 stdout 抓走做代理 / 转发（设计 §10 #2），
   * 诊断日志走 stderr，两者不要混。
   */
  prompt(lines: readonly string[]): void;
  setLevel(level: LogLevel): void;
}

function createLogger(initialLevel: LogLevel): Logger {
  let threshold = LOG_LEVELS[initialLevel];

  const emit = (level: LogLevel, message: string, extra?: unknown): void => {
    if (LOG_LEVELS[level] < threshold) {
      return;
    }

    const line = `[${new Date().toISOString()}] [${level.toUpperCase()}] ${message}`;
    process.stderr.write(extra === undefined ? `${line}\n` : `${line} ${safeJson(extra)}\n`);
  };

  return {
    debug: (message, extra) => emit("debug", message, extra),
    info: (message, extra) => emit("info", message, extra),
    warn: (message, extra) => emit("warn", message, extra),
    error: (message, extra) => emit("error", message, extra),
    prompt: (lines) => {
      const rule = "=".repeat(72);
      process.stdout.write(`\n${rule}\n${lines.join("\n")}\n${rule}\n\n`);
    },
    setLevel: (level) => {
      threshold = LOG_LEVELS[level];
    },
  };
}

function safeJson(value: unknown): string {
  try {
    return JSON.stringify(value) ?? String(value);
  } catch {
    return String(value);
  }
}

// ═══════════════════════════ 配置 ═══════════════════════════

/**
 * sidecar 配置（设计 §6.3）。
 *
 * 文件：`douyin-sidecar/config.json`（可用环境变量 `ZIPPER_DOUYIN_CONFIG` 指到别处）。
 * 环境变量优先级高于文件，方便容器 / 计划任务里注入 token 而不落文件。
 */
interface SidecarConfig {
  serverUrl: string;
  token: string | null;
  dataDir: string;
  maxTextLength: number;
  heartbeatIntervalMs: number;
  deadAfterMs: number;
  logLevel: LogLevel;
  configPath: string;
}

function parseLogLevel(value: unknown, fallback: LogLevel): LogLevel {
  const text = str(value)?.toLowerCase();
  if (text === "debug" || text === "info" || text === "warn" || text === "error") {
    return text;
  }

  return fallback;
}

function positiveInt(value: unknown, fallback: number): number {
  const numeric = num(value);
  return numeric !== undefined && numeric > 0 ? Math.floor(numeric) : fallback;
}

async function loadConfig(log: Logger): Promise<SidecarConfig> {
  const fromEnv = str(process.env.ZIPPER_DOUYIN_CONFIG);
  const configPath = fromEnv ?? path.resolve(process.cwd(), "config.json");

  let fileConfig: Record<string, unknown> = {};
  if (existsSync(configPath)) {
    try {
      const parsed: unknown = JSON.parse(await readFile(configPath, "utf8"));
      fileConfig = asRecord(parsed) ?? {};
      log.debug(`已读取配置文件：${configPath}`);
    } catch (err) {
      throw new Error(`配置文件解析失败（${configPath}）：${describeError(err)}`);
    }
  } else if (fromEnv) {
    throw new Error(`ZIPPER_DOUYIN_CONFIG 指向的文件不存在：${configPath}`);
  } else {
    log.warn(`未找到 ${configPath}，改用默认值 + 环境变量（详见 README「配置」）`);
  }

  const fileToken = str(fileConfig.token);
  const envToken = str(process.env.ZIPPER_DOUYIN_TOKEN);

  return {
    serverUrl:
      str(process.env.ZIPPER_DOUYIN_SERVER_URL) ??
      str(fileConfig.serverUrl) ??
      str(fileConfig.server_url) ??
      DEFAULT_SERVER_URL,
    // 老规矩：token 只在内存里待着，绝不打印（设计 §6.2）
    token: envToken ?? fileToken ?? null,
    dataDir:
      str(process.env.ZIPPER_DOUYIN_DATA_DIR) ??
      str(fileConfig.dataDir) ??
      str(fileConfig.data_dir) ??
      DEFAULT_DATA_DIR,
    maxTextLength: positiveInt(fileConfig.maxTextLength, DEFAULT_MAX_TEXT_LENGTH),
    heartbeatIntervalMs: positiveInt(fileConfig.heartbeatIntervalMs, DEFAULT_HEARTBEAT_MS),
    deadAfterMs: positiveInt(fileConfig.deadAfterMs, DEFAULT_DEAD_AFTER_MS),
    logLevel: parseLogLevel(process.env.ZIPPER_DOUYIN_LOG_LEVEL ?? fileConfig.logLevel, "info"),
    configPath,
  };
}

// ═══════════════════════════ 帧与领域类型 ═══════════════════════════

/** 帧外壳：`{type, time, data}`（设计 §3.1）。 */
export interface Frame {
  type: string;
  time?: number;
  data?: unknown;
}

export interface Mention {
  uid: string;
  name?: string;
}

/** `douyin-im` 事件归一化后的形状 —— 翻译层的输入，也是将来单测的输入。 */
export interface NormalizedInbound {
  chatType: "private" | "group" | "stranger";
  threadId: string;
  conversationShortId?: string;
  messageId?: string;
  senderUid: string;
  senderNickname?: string;
  senderDisplayName?: string;
  text?: string;
  /** 视频 / 表情 / 文件等第一版不解析的类型，降级成 `[视频]` 这类占位（设计 §4.1）。 */
  placeholders: string[];
  imageUrls: string[];
  mentions: Mention[];
  replyTo?: string;
  occurredAtMs: number;
  /** `false` = 平台没给时间戳，用了本地兜底（会记 warn 便于排查）。 */
  timeFromPlatform: boolean;
}

export interface ContactItem {
  userId: string;
  nickname: string;
  remark?: string;
}

export interface GroupItem {
  groupId: string;
  name: string;
  memberCount?: number;
}

/** 出站计划：翻译层把一帧 `outbound` 拆成"发什么、发到哪"。 */
export interface OutboundPlan {
  chatType: "private" | "group";
  threadId: string;
  conversationShortId?: string;
  /** 已按 `maxTextLength` 切好的文本块（设计 §4.2：超长自动分段）。 */
  chunks: string[];
  mentions: Mention[];
  imageUrls: string[];
  replyTo: string | null;
}

// ───────────────────── ① 翻译层（纯函数） ─────────────────────

/** 会话 ID：`douyin:p<uid>` / `douyin:g<gid>`（与 QQ 的 `qq:p10001` 同构，设计 §3.3）。 */
export function sessionIdFor(chatType: "private" | "group", threadId: string): string {
  return `${PLATFORM}:${chatType === "group" ? "g" : "p"}${threadId}`;
}

export function parseSessionId(session: string): { chatType: "private" | "group"; threadId: string } {
  const matched = /^douyin:([pg])(.+)$/.exec(session);
  if (!matched) {
    throw new Error(`无法识别的 session「${session}」—— 期望 douyin:p<uid> 或 douyin:g<gid>`);
  }

  return { chatType: matched[1] === "g" ? "group" : "private", threadId: matched[2] };
}

/**
 * `douyin-im` 事件 → 归一化入站消息；不是消息事件时返回 `null`。
 *
 * ⚠️ 字段名来自设计 §1.3 + `douyin-im` README（**未真实账号验收**，设计 §10 #1）。
 * 实际 fork 的字段若不同，只改这个函数即可 —— 其余层都不认平台字段。
 */
export function normalizeInbound(raw: unknown): NormalizedInbound | null {
  const root = asRecord(raw);
  if (!root) {
    return null;
  }

  // 事件可能直接平铺，也可能包一层 {message} / {data} / {event}
  const body = asRecord(root.message) ?? asRecord(root.data) ?? asRecord(root.event) ?? root;
  const type = (str(root.type) ?? str(body.type) ?? "").toLowerCase();

  let chatType: NormalizedInbound["chatType"] | null = null;
  if (type.includes("private")) {
    chatType = "private";
  } else if (type.includes("group")) {
    chatType = "group";
  } else if (type.includes("stranger")) {
    chatType = "stranger";
  }

  if (!chatType) {
    // 登录状态 / 通知 / 心跳之类，不是消息
    return null;
  }

  const senderRecord0 = asRecord(body.sender);
  const senderUid0 =
    str(body.senderUid) ?? str(body.sender_uid) ?? str(body.uid) ??
    str(senderRecord0?.userId) ?? str(senderRecord0?.uid);

  // 私聊的 threadId 常常是 conversationId（形如 0:1:xxx:yyy），那样会话标题会变成一串数字
  // （2026-10-28 实测：douyin:p0:1:88892527967:4106032998778416）。私聊优先用**对方 uid**。
  const rawThreadId =
    str(body.threadId) ??
    str(body.thread_id) ??
    str(body.groupId) ??
    str(body.conversationId) ??
    str(body.conversationShortId);

  const isGroupChat =
    (str(body.type) ?? "").toLowerCase().includes("group") ||
    (str(body.chatType) ?? "").toLowerCase() === "group";

  const threadId = !isGroupChat && senderUid0 ? senderUid0 : rawThreadId;
  if (!threadId) {
    return null;
  }

  const content = asRecord(body.content);
  const contentKind = (str(content?.kind) ?? str(body.kind) ?? str(body.contentType) ?? "").toLowerCase();

  // 卡片/分享类消息（tips / template / awemeType）没有正常正文：
  // 直接把原始 JSON 塞进库会让会话内容变成一堆 {"tips":...}（2026-10-28 实测）。
  // 这里降级成一句人话；真要看卡片内容，等 douyin-im 的卡片解析验收后再做。
  const rawText = str(body.text) ?? str(content?.text);
  const looksLikeCard =
    body.tips !== undefined || body.template !== undefined || body.awemeType !== undefined;
  const text = rawText && rawText.trim().length > 0
    ? rawText
    : looksLikeCard
      ? "[分享卡片]（对方发来的是作品/卡片消息，当前只记录占位）"
      : rawText;

  const imageUrls: string[] = [];
  const contentUrl = str(content?.url) ?? str(content?.uri) ?? str(content?.imageUrl);
  if (contentUrl) {
    imageUrls.push(contentUrl);
  }

  for (const item of asArray(body.imageUrls)) {
    const url = str(item);
    if (url) {
      imageUrls.push(url);
    }
  }

  // 第一版不解析的类型 → 占位文本，保证"有消息"这件事不丢（设计 §4.1）
  const placeholders: string[] = [];
  if (contentKind.length > 0 && !contentKind.includes("text") && !contentKind.includes("image")) {
    if (contentKind.includes("video")) {
      placeholders.push("[视频]");
    } else if (contentKind.includes("emoji")) {
      placeholders.push("[表情]");
    } else if (contentKind.includes("file")) {
      placeholders.push("[文件]");
    } else {
      placeholders.push(`[${contentKind}]`);
    }
  }

  const mentions: Mention[] = [];
  for (const item of asArray(body.mentions ?? body.mentionList)) {
    const record = asRecord(item);
    const uid = str(record?.uid) ?? str(record?.userId) ?? str(record?.id);
    if (!uid) {
      continue;
    }

    const name = str(record?.name) ?? str(record?.nickname) ?? str(record?.displayName);
    mentions.push(name ? { uid, name } : { uid });
  }

  const reference = asRecord(body.referenceInfo) ?? asRecord(body.reference_info);
  const replyTo =
    str(reference?.serverMessageId) ??
    str(reference?.server_message_id) ??
    str(reference?.messageId) ??
    str(body.replyTo);

  const senderRecord = asRecord(body.sender);
  const senderUid =
    str(body.senderUid) ??
    str(body.sender_uid) ??
    str(body.uid) ??
    str(senderRecord?.userId) ??
    str(senderRecord?.uid) ??
    "";

  const senderNickname =
    str(body.senderNickname) ??
    str(body.sender_nickname) ??
    str(senderRecord?.nickname) ??
    str(senderRecord?.name);

  const senderDisplayName = str(body.senderDisplayName) ?? str(senderRecord?.displayName);

  const messageId =
    str(body.serverMessageId) ??
    str(body.server_message_id) ??
    str(body.messageId) ??
    str(body.msgId);

  const platformTime = toEpochMs(
    body.time ?? body.timestamp ?? body.createdAt ?? body.serverTime ?? body.occurredAt,
  );

  return {
    chatType,
    threadId,
    conversationShortId: str(body.conversationShortId),
    messageId,
    senderUid,
    senderNickname,
    senderDisplayName,
    text,
    placeholders,
    imageUrls,
    mentions,
    replyTo,
    occurredAtMs: platformTime ?? Date.now(),
    timeFromPlatform: platformTime !== undefined,
  };
}

/**
 * 归一化消息 → `inbound` 帧（设计 §3.3 / §4.1）。
 *
 * `stranger` 第一版**直接丢弃**（陌生人盒不响应）；返回 `null` 让调用方记 Debug。
 */
export function toInboundFrame(
  event: NormalizedInbound,
  warn?: (message: string) => void,
): Frame | null {
  if (event.chatType === "stranger") {
    return null;
  }

  const segments: Record<string, unknown>[] = [];

  if (event.text) {
    segments.push({ type: "text", text: event.text });
  }

  for (const placeholder of event.placeholders) {
    segments.push({ type: "text", text: placeholder });
  }

  for (const url of event.imageUrls) {
    segments.push({ type: "image", url });
  }

  for (const mention of event.mentions) {
    if (isNumericUid(mention.uid)) {
      segments.push({ type: "mention", userId: mention.uid, ...(mention.name ? { name: mention.name } : {}) });
    } else {
      // 设计 §10 #7：群 @ 只接受数字 uid，非数字降级成纯文本
      warn?.(`@ 的 uid 非数字（${mention.uid}），已降级为纯文本`);
      segments.push({ type: "text", text: `@${mention.name ?? mention.uid}` });
    }
  }

  if (segments.length === 0) {
    segments.push({ type: "text", text: "[空消息]" });
  }

  return {
    type: "inbound",
    time: Date.now(),
    data: {
      session: sessionIdFor(event.chatType, event.threadId),
      chatType: event.chatType,
      messageId: event.messageId ?? null,
      sender: {
        userId: event.senderUid,
        nickname: event.senderNickname ?? null,
        displayName: event.senderDisplayName ?? null,
      },
      segments,
      replyTo: event.replyTo ?? null,
      // 内层 time = 平台时间（不取 UtcNow，设计 §4.1）；外层 time = 发出帧的时间
      time: event.occurredAtMs,
    },
  };
}

/**
 * 文本切块：单块 ≤ `maxLength`（与 douyin-im 的 16000 上限对齐，设计 §1.3）。
 *
 * 两个细节：不切断代理对（否则 emoji 变乱码）；尽量在换行 / 空格处断开，免得半句话。
 */
export function chunkText(text: string, maxLength: number): string[] {
  if (maxLength <= 0) {
    throw new Error(`maxTextLength 必须为正数，当前为 ${maxLength}`);
  }

  if (text.length === 0) {
    return [];
  }

  if (text.length <= maxLength) {
    return [text];
  }

  const chunks: string[] = [];
  let rest = text;

  while (rest.length > maxLength) {
    let cut = maxLength;

    // 高位代理项（0xD800–0xDBFF）落在切口上 → 往前退一位，别把 emoji 劈开
    const lastCode = rest.charCodeAt(cut - 1);
    if (lastCode >= 0xd800 && lastCode <= 0xdbff) {
      cut -= 1;
    }

    const window = rest.slice(0, cut);
    const breakAt = Math.max(window.lastIndexOf("\n"), window.lastIndexOf(" "));
    if (breakAt > cut * 0.6) {
      cut = breakAt + 1;
    }

    chunks.push(rest.slice(0, cut));
    rest = rest.slice(cut);
  }

  if (rest.length > 0) {
    chunks.push(rest);
  }

  return chunks;
}

/** `outbound` 帧 → 出站计划。缺 session / 段形状不对时抛错，理由会被 ack 回传。 */
export function buildOutboundPlan(
  frame: Frame,
  options: { maxTextLength: number; warn?: (message: string) => void },
): OutboundPlan {
  const data = asRecord(frame.data) ?? {};
  const session = str(data.session);
  if (!session) {
    throw new Error("outbound 帧缺少 session");
  }

  const { chatType, threadId } = parseSessionId(session);

  const texts: string[] = [];
  const mentions: Mention[] = [];
  const imageUrls: string[] = [];
  let replyTo = str(data.replyTo) ?? null;

  const segments = asArray(data.segments);
  if (segments.length === 0) {
    // 容忍简写形状 {session, text}
    const plainText = str(data.text);
    if (plainText) {
      texts.push(plainText);
    }
  }

  for (const item of segments) {
    const segment = asRecord(item);
    const type = (str(segment?.type) ?? "").toLowerCase();

    if (type === "text") {
      const value = str(segment?.text);
      if (value) {
        texts.push(value);
      }
    } else if (type === "mention") {
      const uid = str(segment?.userId) ?? str(segment?.uid);
      if (uid && isNumericUid(uid)) {
        const name = str(segment?.name);
        mentions.push(name ? { uid, name } : { uid });
      } else {
        options.warn?.(`出站 @ 的 uid 非法（${uid ?? "空"}），已忽略该段`);
      }
    } else if (type === "image") {
      const url = str(segment?.url);
      if (url) {
        imageUrls.push(url);
      }
    } else if (type === "reply") {
      replyTo = str(segment?.referencedMessageId) ?? str(segment?.messageId) ?? replyTo;
    } else {
      options.warn?.(`未知出站段类型「${type}」，已忽略`);
    }
  }

  return {
    chatType,
    threadId,
    conversationShortId: str(data.conversationShortId),
    chunks: chunkText(texts.join(""), options.maxTextLength),
    mentions,
    imageUrls,
    replyTo,
  };
}

/** 好友列表 → `query.contacts` 结果项（字段名容错：uid / userId / id 都认）。 */
/** 取第一个非空字符串（null/undefined/空串都跳过）。避免 ?? 链被空串"命中"。 */
function firstNonEmpty(...values: (string | undefined)[]): string | undefined {
  for (const value of values) {
    if (typeof value === "string" && value.length > 0) {
      return value;
    }
  }

  return undefined;
}

export function mapContacts(raw: unknown): ContactItem[] {
  const list = Array.isArray(raw) ? raw : asArray(asRecord(raw)?.contacts ?? asRecord(raw)?.friends ?? asRecord(raw)?.list);
  const contacts: ContactItem[] = [];

  for (const item of list) {
    const record = asRecord(item);
    const userId = str(record?.userId) ?? str(record?.uid) ?? str(record?.id);
    if (!userId) {
      continue;
    }

    const remark = firstNonEmpty(str(record?.remark), str(record?.alias));
    // 昵称按优先级回退；空串不算"有值"（2026-10-28：候选有 2 个但 display 全空就是这个）
    const nickname =
      firstNonEmpty(
        remark,
        str(record?.nickname),
        str(record?.name),
        str(record?.displayName),
        str(record?.uniqueId),
      ) ?? userId;
    contacts.push(remark ? { userId, nickname, remark } : { userId, nickname });
  }

  return contacts;
}

/** 群列表 → `query.groups` 结果项。 */
export function mapGroups(raw: unknown): GroupItem[] {
  const list = Array.isArray(raw) ? raw : asArray(asRecord(raw)?.groups ?? asRecord(raw)?.list);
  const groups: GroupItem[] = [];

  for (const item of list) {
    const record = asRecord(item);
    const groupId = str(record?.groupId) ?? str(record?.id) ?? str(record?.conversationId);
    if (!groupId) {
      continue;
    }

    const name = firstNonEmpty(str(record?.name), str(record?.groupName), str(record?.nickname)) ?? groupId;
    const memberCount = num(record?.memberCount) ?? num(record?.member_count);
    groups.push(memberCount === undefined ? { groupId, name } : { groupId, name, memberCount });
  }

  return groups;
}

function firstFunction(target: Record<string, unknown>, names: readonly string[]): AnyFn | null {
  for (const name of names) {
    const candidate = target[name];
    if (typeof candidate === "function") {
      return candidate as AnyFn;
    }
  }

  return null;
}

// ═══════════════════════ ② douyin-im 适配层 ═══════════════════════

/** douyin-im 客户端：先用结构化"可选成员"描述，缺什么方法就降级报错，而不是崩。 */
type DouyinClient = Record<string, unknown>;

interface DouyinAdapterOptions {
  dataDir: string;
  log: Logger;
  /** 归一化后的消息（翻译层的输入）。 */
  onInbound: (event: NormalizedInbound) => void;
  /** 需要人机交互时的提示（验证 URL）。 */
  onLoginPrompt: (url: string, hint: string) => void;
  /**
   * 往 Server 发一帧（登录管理用，2026-10-28）。
   * 二维码 / 登录状态要主动推给服务端，客户端才能显示。
   */
  emit: (frame: Frame) => void;
}

/**
 * douyin-im 适配层：**唯一**碰 douyin-im 的地方。
 *
 * ⚠️ douyin-im 的导出名 / 方法名大量「未真实账号验收」（设计 §10 #1），
 * 所以这里一律「探测 + 降级」：找到就用，找不到就抛一句人话，
 * 不假装成功 —— 发送失败必须回填原因给模型（设计 §10 #6）。
 */
export class DouyinImAdapter {
  private readonly dataDir: string;
  private readonly log: Logger;
  private readonly onInbound: (event: NormalizedInbound) => void;
  private readonly onLoginPrompt: (url: string, hint: string) => void;
  private readonly emit: (frame: Frame) => void;

  private client: DouyinClient | null = null;
  private selfId = "";
  /** 是否处于"已退出登录"状态：为 true 时不再上报 online（否则一重连就翻回已登录）。 */
  private loggedOut = false;
  private readonly seenMessageIds = new Set<string>();
  /** threadId → conversationShortId：出站时 douyin-im 需要它，入站事件里顺手记下。 */
  private readonly conversationCache = new Map<string, string>();

  constructor(options: DouyinAdapterOptions) {
    this.dataDir = options.dataDir;
    this.log = options.log;
    this.onInbound = options.onInbound;
    this.onLoginPrompt = options.onLoginPrompt;
    this.emit = options.emit;
  }

  getSelfId(): string {
    return this.selfId;
  }

  /**
   * 把**当前**登录态推给服务端（连上 Server 后补一次）。
   *
   * 为什么需要：登录事件（system.online）发生在 sidecar 连上服务端**之前**，
   * 那一次的 login.state 发不出去 —— 服务端就会一直显示 unknown（2026-10-28 实测）。
   */
  emitLoginState(): void {
    if (this.loggedOut) {
      this.emit({ type: "login.state", time: Date.now(), data: { state: "loggedOut" } });
      return;
    }

    if (this.selfId) {
      this.emit({ type: "login.state", time: Date.now(), data: { state: "online", selfId: this.selfId } });
    } else {
      this.emit({ type: "login.state", time: Date.now(), data: { state: "waitingQr" } });
    }
  }

  /**
   * 客户端点「登录」时调（服务端下发 `login.start`）。
   *
   * 先回一个 waitingQr 状态，再走 douyin-im 的 login()：
   * 二维码会通过 `system.login.qrcode` 事件回来，再由 handleLoginEvent 推给服务端。
   */
  async startLogin(): Promise<void> {
    this.loggedOut = false;
    let client: DouyinClient | null = this.client;
    if (!client) {
      try {
        client = await this.ensureClient();
      } catch (err) {
        this.emit({
          type: "login.state",
          time: Date.now(),
          data: { state: "error", message: `重建 douyin-im 客户端失败：${describeError(err)}` },
        });
        return;
      }
    }
    if (!client) {
      this.emit({
        type: "login.state",
        time: Date.now(),
        data: { state: "error", message: "douyin-im 客户端还没创建" },
      });
      return;
    }

    this.emit({ type: "login.state", time: Date.now(), data: { state: "waitingQr" } });

    const login = firstFunction(client, ["login", "start", "connect", "init"]);
    if (!login) {
      this.emit({
        type: "login.state",
        time: Date.now(),
        data: { state: "error", message: "douyin-im 客户端没有 login()" },
      });
      return;
    }

    try {
      this.log.info("收到 login.start：开始抖音登录（等待扫码）");
      await Promise.resolve((login as AnyFn).call(client, { dataDir: this.dataDir }));
    } catch (err) {
      this.emit({
        type: "login.state",
        time: Date.now(),
        data: { state: "error", message: describeError(err) },
      });
    }
  }

  /**
   * 客户端点「退出登录」时调（服务端下发 `login.logout`）。
   *
   * 语义（用户 2026-10-28 选定）：**只清抖音登录态，sidecar 进程继续跑**。
   */
  async logout(): Promise<void> {
    const client = this.client;
    this.log.info("收到 login.logout：退出抖音登录（sidecar 继续运行）");

    // ① 先调 douyin-im 的 logout（有就调；没有也不算失败）
    const stopper = client ? firstFunction(client, ["logout", "stop", "close"]) : null;
    if (stopper) {
      try {
        await Promise.resolve((stopper as AnyFn).call(client));
      } catch (err) {
        this.log.warn(`douyin-im logout() 失败（继续清本地登录态）：${describeError(err)}`);
      }
    }

    // ② **必须清掉持久化登录态**，否则下次 login() 会直接复用旧会话、
    //    瞬间又变回「已登录」—— 用户看到的"退出后又显示已登录"就是这个（2026-10-28 实测）。
    //    douyin-im 把会话写在 dataDir/accounts/<uid>/ 下。
    try {
      const accountsDir = path.join(this.dataDir, "accounts");
      if (existsSync(accountsDir)) {
        rmSync(accountsDir, { recursive: true, force: true });
        this.log.info(`已清除抖音登录态目录：${accountsDir}`);
      }
    } catch (err) {
      this.log.warn(`清除登录态目录失败：${describeError(err)}（下次登录可能仍会复用旧会话）`);
    }

    // ③ **把 client 丢掉**：douyin-im 的 Client 对象在内存里还握着账号，
    //    只用 rmSync 删磁盘的话，下次 login() 会把它重新写回去 ——
    //    表现就是"退出登录后点登录，直接又变 online、根本不出二维码"（2026-10-28 实测）。
    //    置空后由 ensureClient() 在下次登录时重新创建，内存/磁盘都干净。
    try {
      const stopper2 = client ? firstFunction(client, ["stop", "close"]) : null;
      if (stopper2) {
        await Promise.resolve((stopper2 as AnyFn).call(client)).catch(() => undefined);
      }
    } catch {
      // 停不掉也无所谓，反正对象要丢
    }

    this.client = null;

    this.loggedOut = true;
    this.selfId = "";
    this.emit({ type: "login.state", time: Date.now(), data: { state: "loggedOut" } });
  }

  /**
   * 确保 client 存在（首次启动 / 退出登录后重建）。
   */
  private async ensureClient(): Promise<DouyinClient> {
    if (this.client) {
      return this.client;
    }

    const module = await importDouyinIm();
    const client = createClientFromModule(module, this.dataDir, this.log);
    this.bindEvents(client);
    this.client = client;
    this.log.debug("douyin-im 客户端已重新创建（登录态已清空）");
    return client;
  }

  /** `hello` 帧（设计 §3.3）：登录完成后调用，`selfId` 可能为空。 */
  buildHelloFrame(): Frame {
    return {
      type: "hello",
      time: Date.now(),
      data: {
        version: PROTOCOL_VERSION,
        platform: PLATFORM,
        selfId: this.selfId,
        capabilities: [...CAPABILITIES],
      },
    };
  }

  /**
   * 启动 douyin-im 客户端并登录。
   *
   * **登录需要人机交互**（扫码 / 短信 / 滑块，设计 §10 #2）：这里可能阻塞到有人操作完成，
   * 期间把验证 URL 打到 stdout（`onLoginPrompt`）。
   */
  async start(): Promise<void> {
    const module = await importDouyinIm();
    this.client = createClientFromModule(module, this.dataDir, this.log);

    // 先挂事件再 start：登录过程中的验证事件不会漏掉
    this.bindEvents(this.client);
    this.selfId = await this.resolveSelfId(this.client);

    const starter = firstFunction(this.client, ["login", "start", "connect", "init"]);
    if (!starter) {
      this.log.warn("douyin-im 客户端没有 start()/login()，跳过自动登录（请按实际 API 调整 firstFunction 的候选名）");
      return;
    }

    this.log.info("正在启动 douyin-im（首次需要人机交互登录，见下方提示）");
    await Promise.resolve(starter.call(this.client, { dataDir: this.dataDir, storageDir: this.dataDir }));

    if (!this.selfId) {
      this.selfId = await this.resolveSelfId(this.client);
    }

    this.log.info(`douyin-im 已就绪（selfId=${this.selfId || "未知"}）`);
  }

  async stop(): Promise<void> {
    const client = this.client;
    if (!client) {
      return;
    }

    const stopper = firstFunction(client, ["stop", "close", "logout"]);
    if (!stopper) {
      return;
    }

    try {
      await Promise.resolve(stopper.call(client));
      this.log.debug("douyin-im 客户端已停止");
    } catch (err) {
      this.log.warn(`停止 douyin-im 客户端失败：${describeError(err)}`);
    }
  }

  /** 按出站计划真实发送；失败抛错，由上层回 `ack.ok=false`。 */
  async send(plan: OutboundPlan): Promise<void> {
    const client = this.requireClient();

    if (plan.chunks.length === 0 && plan.imageUrls.length === 0) {
      throw new Error("出站内容为空（没有文本也没有图片）");
    }

    const conversationShortId = plan.conversationShortId ?? this.conversationCache.get(plan.threadId);
    const mentions = plan.mentions.map((mention) =>
      mention.name ? { uid: mention.uid, name: mention.name } : { uid: mention.uid },
    );

    const plain = plan.imageUrls.length === 0 && plan.replyTo === null;

    // ① douyin-im 1.1.0 的正路：account.pickFriend/pickGroup(threadId) → contact.sendMsg(text)
    //    （2026-10-28 实测：client.sendMessage() 不存在，之前所有发送都失败）
    if (plain) {
      const contact = this.pickContact(plan);
      const sendMsg = contact ? firstFunction(contact, ["sendMsg", "sendText", "send", "reply"]) : null;
      if (contact && sendMsg) {
        for (const chunk of plan.chunks) {
          await Promise.resolve((sendMsg as AnyFn).call(contact, chunk));
        }

        return;
      }
    }

    // ② 兼容路径：老实现里的 client.sendText（图片 / 引用 / 找不到联系人）
    const sendText = firstFunction(client, ["sendText"]);
    if (plain && sendText) {
      for (const chunk of plan.chunks) {
        await Promise.resolve(
          sendText.call(client, {
            threadId: plan.threadId,
            conversationShortId,
            text: chunk,
            mentions,
          }),
        );
      }

      return;
    }

    // 图片 / 引用：需要 sendMessage + segments（design §4.2）
    const sendMessage = firstFunction(client, ["sendMessage"]);
    if (!sendMessage) {
      throw new Error("douyin-im 客户端没有 sendMessage()，无法发送图片 / 引用消息（未验收 API，见设计 §10 #1）");
    }

    const segments: Record<string, unknown>[] = plan.chunks.map((chunk) => ({ type: "text", text: chunk }));
    for (const url of plan.imageUrls) {
      segments.push(await this.uploadImage(client, url));
    }

    if (plan.replyTo) {
      segments.push({ type: "reply", referencedMessageId: plan.replyTo });
    }

    await Promise.resolve(
      sendMessage.call(client, {
        threadId: plan.threadId,
        conversationShortId,
        segments,
        mentions,
        replyTo: plan.replyTo ?? undefined,
      }),
    );
  }

  /**
   * 取"当前账号"对象：douyin-im 1.1.0 里好友/群/发送都在 **account** 上，
   * 不在 client 上（2026-10-28 实测：client.getFriendList 不存在 → 人员名单永远空）。
   */
  private requireAccount(): Record<string, any> {
    const client = this.requireClient() as unknown as Record<string, any>;
    const picker = firstFunction(client, ["pickAccount"]);
    if (picker) {
      // 不传参：只有一个账号时可省略 ID（多账号会抛错，交给上层显示原因）
      const account = (picker as AnyFn).call(client);
      if (account) {
        return account as Record<string, any>;
      }
    }

    // 退一步：有些实现直接把 account 挂在 client 上
    if (client.account) {
      return client.account as Record<string, any>;
    }

    throw new Error("douyin-im 客户端没有 pickAccount()，取不到当前账号（未验收 API，见设计 §10 #1）");
  }

  /**
   * 按出站计划挑联系人实例（douyin-im 1.1.0：发送对象是 contact，不是 client）。
   *
   * 私聊 → `pickFriend(uid)`，群 → `pickGroup(gid)`；都没有就返回 null，
   * 由调用方退回兼容路径或抛可读错误 —— **不猜**（猜错会把消息发到奇怪的地方）。
   */
  private pickContact(plan: OutboundPlan): Record<string, any> | null {
    let account: Record<string, any>;
    try {
      account = this.requireAccount();
    } catch {
      return null;
    }

    const try_ = (name: string): Record<string, any> | null => {
      const fn = firstFunction(account, [name]);
      if (!fn) {
        return null;
      }

      try {
        const picked = (fn as AnyFn).call(account, plan.threadId);
        return picked ? (picked as Record<string, any>) : null;
      } catch (err) {
        this.log.debug(`${name}(${plan.threadId}) 失败：${describeError(err)}`);
        return null;
      }
    };

    return plan.chatType === "group"
      ? try_("pickGroup") ?? try_("pickStranger")
      : try_("pickFriend") ?? try_("pickStranger");
  }

  /** `query.contacts` → `account.getFriendList()`（设计 §3.5）。 */
  async getContacts(): Promise<ContactItem[]> {
    const account = this.requireAccount();
    const getter = firstFunction(account, ["getFriendList", "getContacts", "getFriends"])
      ?? firstFunction(this.requireClient() as unknown as Record<string, any>, ["getFriendList", "getContacts", "getFriends"]);
    if (!getter) {
      throw new Error("douyin-im 账号没有 getFriendList()（未验收 API，见设计 §10 #1）");
    }

    return mapContacts(await Promise.resolve((getter as AnyFn).call(account)));
  }

  /** `query.groups` → `account.getGroupList()`（设计 §3.5）。 */
  async getGroups(): Promise<GroupItem[]> {
    const account = this.requireAccount();
    const getter = firstFunction(account, ["getGroupList", "getGroups", "getConversationList"])
      ?? firstFunction(this.requireClient() as unknown as Record<string, any>, ["getGroupList", "getGroups", "getConversationList"]);
    if (!getter) {
      throw new Error("douyin-im 账号没有 getGroupList()（未验收 API，见设计 §10 #1）");
    }

    return mapGroups(await Promise.resolve((getter as AnyFn).call(account)));
  }

  private requireClient(): DouyinClient {
    if (!this.client) {
      throw new Error("douyin-im 尚未启动");
    }

    return this.client;
  }

  private async resolveSelfId(client: DouyinClient): Promise<string> {
    const direct = str(client.selfId) ?? str(client.uid) ?? str(client.userId);
    if (direct) {
      return direct;
    }

    const numericDirect = num(client.selfId) ?? num(client.uid);
    if (numericDirect !== undefined) {
      return String(numericDirect);
    }

    const getter = firstFunction(client, ["getSelfInfo", "getCurrentUser", "getAccountInfo"]);
    if (!getter) {
      return "";
    }

    try {
      const info = asRecord(await Promise.resolve(getter.call(client)));
      return str(info?.uid) ?? str(info?.userId) ?? str(info?.id) ?? "";
    } catch (err) {
      this.log.warn(`读取 selfId 失败：${describeError(err)}`);
      return "";
    }
  }

  /**
   * 订阅 douyin-im 的事件。
   *
   * 候选事件名一次订全（各 fork 命名不一），靠 `normalizeInbound` 判类型，
   * 再用 `seenMessageIds` 去掉"多个事件名收到同一条"的重复。
   */
  private bindEvents(client: DouyinClient): void {
    const on = firstFunction(client, ["on", "addListener", "subscribe"]);
    if (on) {
      for (const eventName of ["message", "messages", "message.private", "message.group", "message.stranger", "event"]) {
        try {
          (on as AnyFn).call(client, eventName, (payload: unknown) => this.handleRawEvent(eventName, payload));
        } catch (err) {
          this.log.debug(`订阅事件「${eventName}」失败（忽略）：${describeError(err)}`);
        }
      }
    }

    const onEvent = firstFunction(client, ["onEvent"]);
    if (onEvent) {
      (onEvent as AnyFn).call(client, (payload: unknown) => this.handleRawEvent("onEvent", payload));
    }

    // ── 登录事件（douyin-im 1.1.0 的正式流程，2026-10-28 补）──
    // 关键：**必须先订阅 system.login.qrcode 并调 account.continueLogin()**。
    // 不订阅的话 `client.login()` 拿不到"人等用户扫完"的开关 —— 实测表现是
    // 进程启动后立刻退出（exit=0），日志停在「正在启动 douyin-im」，服务器上踩到过一次。
    if (on) {
      for (const eventName of LOGIN_EVENTS) {
        try {
          (on as AnyFn).call(client, eventName, (payload: unknown) =>
            this.handleLoginEvent(eventName, payload),
          );
        } catch (err) {
          this.log.debug(`订阅登录事件「${eventName}」失败（忽略）：${describeError(err)}`);
        }
      }
    }

    if (!on && !onEvent) {
      this.log.warn("douyin-im 客户端没有 on()/onEvent()，收不到任何消息（需按实际 fork 适配 bindEvents）");
    }
  }

  /**
   * 登录事件：能自动推进的推进，需要人做的用日志说清楚。
   */
  private handleLoginEvent(eventName: string, payload: unknown): void {
    const data = (payload ?? {}) as Record<string, any>;
    const account = data.account;

    try {
      switch (eventName) {
        case "system.login.qrcode": {
          const base64 = str(data.qrcodeBase64) ?? str(data.qrCodeBase64) ?? str(data.qrcode);
          if (base64) {
            const file = path.join(this.dataDir, "login-qr.png");
            try {
              mkdirSync(path.dirname(file), { recursive: true });
              writeFileSync(file, Buffer.from(base64, "base64"), { mode: 0o600 });
              this.log.info(`登录二维码已保存：${file}（用抖音 App 扫码，并在手机上确认）`);
              this.emit({
                type: "login.qr",
                time: Date.now(),
                data: { qrcodeBase64: base64 },
              });
            } catch (err) {
              this.log.warn(`写二维码文件失败：${describeError(err)}（仍可继续等待扫码）`);
            }
          } else {
            this.log.info("收到扫码登录事件（没有二维码数据），请在抖音 App 内确认登录");
          }

          // 这一步不能省：continueLogin() 才是"等用户扫完"的开关
          if (typeof account?.continueLogin === "function") {
            void Promise.resolve(account.continueLogin()).catch((err: unknown) => {
              this.log.warn(`continueLogin 失败：${describeError(err)}`);
            });
          }
          return;
        }

        case "system.login.sms":
        case "system.login.voice": {
          this.log.warn("抖音登录需要短信/语音验证码 —— 服务器上没有交互终端，无法自动输入");
          return;
        }

        case "system.login.verification": {
          const methods = Array.isArray(data.verification?.methods)
            ? data.verification.methods.join(", ")
            : "未知";
          this.log.warn(`抖音要求安全验证（可用方式：${methods}）—— 需要在能打开浏览器的环境完成`);
          return;
        }

        case "system.online": {
          const nickname = str(data.account?.nickname) ?? "未知";
          const uid = str(data.account?.uid) ?? str(data.account?.selfId);
          if (uid) {
            // ⚠️ 必须记下来：hello 帧的 selfId 取自这里；不赋值的话服务端永远看到 selfId=未知，
            // 客户端也就显示不出"已连入（selfId=…）"（2026-10-28 实测）。
            this.selfId = uid;
          }
          this.log.info(`抖音已上线：${nickname}（selfId=${uid ?? this.selfId ?? "未知"}）`);
          this.emit({
            type: "login.state",
            time: Date.now(),
            data: { state: "online", selfId: uid ?? this.selfId ?? null, nickname },
          });
          return;
        }

        case "system.login.error": {
          this.log.error(`抖音登录失败：${str(data.error) ?? "未知原因"}`);
          this.emit({
            type: "login.state",
            time: Date.now(),
            data: { state: "error", message: str(data.error) ?? "未知原因" },
          });
          return;
        }

        default:
          this.log.debug(`未处理的登录事件：${eventName}`);
      }
    } catch (err) {
      // 登录事件处理失败也不能让进程崩：崩了连接就没了，人还得重来
      this.log.warn(`处理登录事件「${eventName}」失败：${describeError(err)}`);
    }
  }

  /** 事件入口：**任何异常都不许逃回 douyin-im**（逃回去会把它的接收循环带崩）。 */
  private handleRawEvent(source: string, payload: unknown): void {
    try {
      const prompt = extractLoginPrompt(payload);
      if (prompt) {
        this.onLoginPrompt(prompt.url, prompt.hint);
        return;
      }

      const event = normalizeInbound(payload);
      if (!event) {
        this.log.debug(`忽略非消息事件（来源 ${source}）`);
        return;
      }

      if (event.messageId) {
        if (this.seenMessageIds.has(event.messageId)) {
          this.log.debug(`重复事件已丢弃：messageId=${event.messageId}（来源 ${source}）`);
          return;
        }

        this.rememberMessageId(event.messageId);
      }

      if (event.conversationShortId) {
        this.conversationCache.set(event.threadId, event.conversationShortId);
      }

      if (!event.timeFromPlatform) {
        this.log.warn(`平台未给时间戳，已用本地时间兜底：threadId=${event.threadId}`);
      }

      this.onInbound(event);
    } catch (err) {
      this.log.error(`处理 douyin-im 事件失败（来源 ${source}）：${describeError(err)}`);
    }
  }

  private rememberMessageId(messageId: string): void {
    this.seenMessageIds.add(messageId);
    if (this.seenMessageIds.size <= SEEN_MESSAGE_CAP) {
      return;
    }

    // Set 按插入序迭代：丢最早的那批即可
    const overflow = this.seenMessageIds.size - SEEN_MESSAGE_CAP;
    let removed = 0;
    for (const oldest of this.seenMessageIds) {
      if (removed >= overflow) {
        break;
      }

      this.seenMessageIds.delete(oldest);
      removed += 1;
    }
  }

  /** 图片：sidecar 自己下载再上传（Server 只传 URL，设计 §4.2）。 */
  private async uploadImage(client: DouyinClient, url: string): Promise<Record<string, unknown>> {
    const upload = firstFunction(client, ["uploadImage", "uploadMedia", "upload"]);
    if (!upload) {
      throw new Error("douyin-im 客户端没有 uploadImage()，无法发送图片（未验收 API，见设计 §10 #1）");
    }

    const response = await fetch(url);
    if (!response.ok) {
      throw new Error(`下载图片失败：HTTP ${response.status}（${url}）`);
    }

    const buffer = Buffer.from(await response.arrayBuffer());
    const uploaded = asRecord(
      await Promise.resolve(upload.call(client, { buffer, data: buffer, url, filename: path.basename(new URL(url).pathname) })),
    );

    const uri = str(uploaded?.uri) ?? str(uploaded?.url) ?? str(uploaded?.id);
    return uri ? { type: "image", uri } : { type: "image" };
  }
}

/** 从任意事件里捞登录验证信息 —— 远程宿主靠它代理人机交互（设计 §10 #2）。 */
function extractLoginPrompt(payload: unknown): { url: string; hint: string } | null {
  const root = asRecord(payload);
  if (!root) {
    return null;
  }

  const body = asRecord(root.data) ?? asRecord(root.payload) ?? root;
  const candidates: unknown[] = [
    body.verifyUrl,
    body.verify_url,
    body.url,
    body.loginUrl,
    body.qrcodeUrl,
    body.qrCodeUrl,
    body.qr_url,
    root.verifyUrl,
    root.url,
  ];

  for (const candidate of candidates) {
    const url = str(candidate);
    if (url && /^https?:\/\//i.test(url)) {
      const text = str(body.message) ?? str(body.tip) ?? str(root.type) ?? "需要人机交互";
      return { url, hint: text };
    }
  }

  return null;
}

/** 动态 import：用变量做说明符，tsc 就不会在编译期解析这个包（装不装依赖都能过类型检查）。 */
async function importDouyinIm(): Promise<Record<string, unknown>> {
  const specifier = "douyin-im";
  const loaded: unknown = await import(specifier);
  const module = asRecord(loaded);
  if (!module) {
    throw new Error("douyin-im 模块加载结果不是对象，无法使用");
  }

  return module;
}

/**
 * 按"工厂函数 / 构造函数 / 单字符串参数"三种常见写法轮试。
 *
 * 之所以这么啰嗦：`douyin-im` 的导出形状未验收，猜错一次就整个 sidecar 起不来。
 */
function createClientFromModule(module: Record<string, unknown>, dataDir: string, log: Logger): DouyinClient {
  const candidates: unknown[] = [
    module.createClient,
    module.createDouyinClient,
    module.DouyinClient,
    module.DouyinImClient,
    module.default,
  ];

  const factory = candidates.find((candidate): candidate is AnyFn => typeof candidate === "function");
  if (!factory) {
    throw new Error("douyin-im 没有导出客户端工厂（期望 createClient / DouyinClient / default 之一）—— 请按实际 fork 调整");
  }

  const options = { dataDir, storageDir: dataDir, profileDir: path.join(dataDir, "profile") };
  const attempts: Array<() => unknown> = [
    () => factory.call(module, options),
    () => new (factory as unknown as new (opts?: unknown) => unknown)(options),
    () => factory.call(module, dataDir),
  ];

  let lastError: unknown = null;
  for (const attempt of attempts) {
    try {
      const client = asRecord(attempt());
      if (client) {
        log.debug("douyin-im 客户端已创建");
        return client;
      }
    } catch (err) {
      lastError = err;
    }
  }

  throw new Error(`创建 douyin-im 客户端失败：${describeError(lastError)}`);
}

// ═══════════════════════ ③ /ws/channel 连接层 ═══════════════════════

/** Node 内置 `WebSocket` 的最小结构类型（不引 DOM lib，也不引 `ws` 依赖）。 */
interface SocketLike {
  readyState: number;
  send(data: string): void;
  close(code?: number, reason?: string): void;
  addEventListener(type: string, handler: (event: unknown) => void): void;
}

interface SocketConstructor {
  new (url: string): SocketLike;
}

function resolveSocketConstructor(): SocketConstructor {
  const ctor = (globalThis as { WebSocket?: SocketConstructor }).WebSocket;
  if (!ctor) {
    throw new Error("当前 Node 没有全局 WebSocket（需要 Node 21+；本项目要求 Node 22.5+）");
  }

  return ctor;
}

interface ChannelLinkOptions {
  url: string;
  token: string | null;
  /** 连上后立刻发 hello（设计 §3.2）。 */
  buildHello: () => Frame;
  onFrame: (frame: Frame) => void;
  heartbeatIntervalMs: number;
  deadAfterMs: number;
  log: Logger;
  /** 连接建立并发出 hello 之后回调（用来补报登录态，2026-10-28）。 */
  onConnected?: () => void;
}

/**
 * 到 Server `/ws/channel` 的连接：连接、退避重连、心跳、死连接检测。
 *
 * 设计 §2.2：sidecar 主动连入，断了自动重连 —— 所以这里是"永远重试"的循环，
 * 只有 `stop()` 才结束。
 */
export class ChannelLink {
  private readonly options: ChannelLinkOptions;
  private socket: SocketLike | null = null;
  private stopped = false;
  private attempt = 0;
  private lastInboundAt = 0;
  private heartbeatTimer: ReturnType<typeof setInterval> | null = null;

  constructor(options: ChannelLinkOptions) {
    this.options = options;
  }

  /** 连接循环：直到 `stop()` 被调用。 */
  async start(): Promise<void> {
    while (!this.stopped) {
      if (this.attempt > 0) {
        const delay = this.backoffMs(this.attempt);
        this.options.log.info(`${delay}ms 后重连（第 ${this.attempt} 次）`);
        await sleep(delay);
        if (this.stopped) {
          return;
        }
      }

      try {
        await this.connectOnce();
        // 连接建立过又断开（Server 重启 / 主动踢）：退到 1s 再试。
        // 不退的话 attempt 已被 open 里的重置归零，会变成"断开→立刻连→再断开"的热循环。
        this.attempt = Math.max(this.attempt, 1);
      } catch (err) {
        this.attempt += 1;
        this.options.log.warn(`连接 /ws/channel 失败（第 ${this.attempt} 次）：${describeError(err)}`);
      }
    }
  }

  /** 发一帧；未连接直接抛错 —— 上层据此回 `ack.ok=false`（设计 §10 #6）。 */
  send(frame: Frame): void {
    const socket = this.socket;
    if (!socket || socket.readyState !== SOCKET_OPEN) {
      throw new Error("与 Server 的连接未就绪（sidecar 正在重连）");
    }

    socket.send(JSON.stringify(frame));
  }

  stop(): void {
    this.stopped = true;
    this.stopHeartbeat();
    const socket = this.socket;
    this.socket = null;
    if (socket) {
      try {
        socket.close(1000, "sidecar shutdown");
      } catch {
        // 关闭失败无所谓：进程都要退出了
      }
    }
  }

  /** 1s → 2s → 4s … 封顶 60s，带 ±20% 抖动（避免多个 sidecar 同时撞 Server）。 */
  private backoffMs(attempt: number): number {
    const base = Math.min(RECONNECT_MAX_MS, RECONNECT_INITIAL_MS * 2 ** (attempt - 1));
    const jitter = 0.8 + Math.random() * 0.4;
    return Math.round(Math.min(RECONNECT_MAX_MS, base * jitter));
  }

  private buildUrl(): string {
    const { url, token } = this.options;
    if (!token) {
      return url;
    }

    const separator = url.includes("?") ? "&" : "?";
    return `${url}${separator}token=${encodeURIComponent(token)}`;
  }

  private connectOnce(): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      const Constructor = resolveSocketConstructor();
      const url = this.buildUrl();
      let settled = false;

      this.options.log.info(`正在连接 ${redactToken(url)}`);

      let socket: SocketLike;
      try {
        socket = new Constructor(url);
      } catch (err) {
        reject(new Error(`创建 WebSocket 失败：${describeError(err)}`));
        return;
      }

      this.socket = socket;

      const finish = (error?: unknown): void => {
        if (settled) {
          return;
        }

        settled = true;
        this.stopHeartbeat();
        if (this.socket === socket) {
          this.socket = null;
        }

        if (error === undefined) {
          resolve();
        } else {
          reject(error);
        }
      };

      socket.addEventListener("open", () => {
        this.attempt = 0;
        this.lastInboundAt = Date.now();
        this.startHeartbeat(socket);
        try {
          socket.send(JSON.stringify(this.options.buildHello()));
          this.options.log.info("已连上 Server，hello 已发送");

          // 连上立刻补报当前登录态：system.online 往往早于这次连接，
          // 那一次的 login.state 发不出去（服务端就会一直显示 unknown）。
          this.options.onConnected?.();
        } catch (err) {
          this.options.log.error(`发送 hello 失败：${describeError(err)}`);
          try {
            socket.close();
          } catch {
            // 关闭失败也无所谓，下面 finish 已经会推进重连
          }

          finish(err);
        }
      });

      socket.addEventListener("message", (event) => {
        this.lastInboundAt = Date.now();
        const raw = (event as { data?: unknown }).data;
        if (typeof raw !== "string") {
          this.options.log.warn("收到非文本帧（已忽略）");
          return;
        }

        const frame = parseFrame(raw);
        if (!frame) {
          this.options.log.warn(`收到无法解析的帧（已忽略）：${truncate(raw, 200)}`);
          return;
        }

        // 分发逻辑里抛异常不能把接收循环带崩
        try {
          this.options.onFrame(frame);
        } catch (err) {
          this.options.log.error(`处理 Server 帧失败（type=${frame.type}）：${describeError(err)}`);
        }
      });

      socket.addEventListener("error", (event) => {
        const message = describeSocketError(event);
        this.options.log.warn(`WebSocket 出错：${message}`);

        // ⚠️ 必须在这里也 finish：Node 的 ws 有时只发 error、不发 close，
        // 不 finish 的话这次 `await` 永远不返回 → 重连循环直接停死
        // （2026-10-28 实测：服务端重启后 sidecar 只试了一次，之后 12 分钟没再重连）。
        finish(new Error(message));
      });

      socket.addEventListener("close", (event) => {
        const code = (event as { code?: unknown }).code;
        const reason = (event as { reason?: unknown }).reason;
        this.options.log.warn(`与 Server 的连接已断开（code=${String(code ?? "?")}，reason=${String(reason ?? "") || "-"}）`);
        finish();
      });
    });
  }

  private startHeartbeat(socket: SocketLike): void {
    this.stopHeartbeat();
    this.heartbeatTimer = setInterval(() => {
      if (this.stopped || this.socket !== socket) {
        return;
      }

      // 半死连接（拔网线、NAT 超时）不会触发 close：靠"多久没收到任何帧"判定
      if (Date.now() - this.lastInboundAt > this.options.deadAfterMs) {
        this.options.log.warn(`${this.options.deadAfterMs}ms 内没收到任何帧，判定连接已死，主动断开重连`);
        this.closeSocket(socket);
        return;
      }

      try {
        socket.send(JSON.stringify({ type: "ping", time: Date.now() } satisfies Frame));
      } catch (err) {
        this.options.log.warn(`心跳发送失败：${describeError(err)}`);
        this.closeSocket(socket);
      }
    }, this.options.heartbeatIntervalMs);
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer !== null) {
      clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = null;
    }
  }

  private closeSocket(socket: SocketLike): void {
    try {
      socket.close(4000, "heartbeat timeout");
    } catch (err) {
      this.options.log.debug(`关闭连接时出错（忽略）：${describeError(err)}`);
    }
  }
}

function describeSocketError(event: unknown): string {
  const record = asRecord(event);
  return str(record?.message) ?? str(record?.type) ?? "未知错误";
}

function sleep(ms: number): Promise<void> {
  return new Promise<void>((resolve) => {
    setTimeout(resolve, ms);
  });
}

/** 解析一帧文本；结构不对返回 `null`（畸形 JSON 不许掀翻接收循环，设计 §9）。 */
export function parseFrame(text: string): Frame | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return null;
  }

  const record = asRecord(parsed);
  const type = str(record?.type);
  if (!record || !type) {
    return null;
  }

  const time = num(record.time);
  return time === undefined ? { type, data: record.data } : { type, time, data: record.data };
}

// ═══════════════════════ ④ 帧分发（编排层） ═══════════════════════

function readRequestId(frame: Frame): string | undefined {
  return str(asRecord(frame.data)?.requestId);
}

function handleServerFrame(
  frame: Frame,
  context: { adapter: DouyinImAdapter; link: () => ChannelLink | null; log: Logger; maxTextLength: number },
): void {
  const { link, log } = context;

  switch (frame.type) {
    case "login.start":
      // 客户端点「登录」→ 服务端转成这一帧（2026-10-28）
      void context.adapter.startLogin();
      return;

    case "login.logout":
      // 客户端点「退出登录」：只清抖音登录态，sidecar 继续跑
      void context.adapter.logout();
      return;

    case "heartbeat":
      // Server 主动心跳（2026-10-28）：只用于"链路还活着"，不需要回任何东西。
      // 不要落成 warn —— 25 秒一次会把日志刷满。
      log.debug("收到服务端心跳");
      return;

    case "ping":
      // Server → sidecar ping / sidecar → Server pong（设计 §3.2）
      try {
        link()?.send({ type: "pong", time: Date.now() });
      } catch (err) {
        log.debug(`回 pong 失败（连接可能已断）：${describeError(err)}`);
      }
      return;

    case "pong":
    case "hello":
      // hello 由 Server 回发时也接受（不同实现可能两边都发）。
      //
      // 这里顺手补报一次登录态：system.online 往往发生在 sidecar 连上 Server **之前**，
      // 那一次的 login.state 发不出去 —— 不补的话服务端一直显示 unknown（2026-10-28 实测）。
      context.adapter.emitLoginState();
      return;

    case "outbound":
      void handleOutbound(frame, context);
      return;

    case "query.contacts":
      void handleQuery(frame, context, "contacts");
      return;

    case "query.groups":
      void handleQuery(frame, context, "groups");
      return;

    default:
      log.warn(`未知帧类型「${frame.type}」（已忽略）`);
  }
}

async function handleOutbound(
  frame: Frame,
  context: { adapter: DouyinImAdapter; link: () => ChannelLink | null; log: Logger; maxTextLength: number },
): Promise<void> {
  const { adapter, link, log, maxTextLength } = context;
  const requestId = readRequestId(frame);
  if (!requestId) {
    // 没有 requestId 就没法配对 ack；还是尽量把消息发出去，只是不回执
    log.warn("outbound 帧缺少 requestId：仍会尝试发送，但无法回 ack 配对");
  }

  const warnings: string[] = [];
  try {
    const plan = buildOutboundPlan(frame, {
      maxTextLength,
      warn: (message) => warnings.push(message),
    });

    for (const message of warnings) {
      log.warn(`出站翻译告警：${message}`);
    }

    await adapter.send(plan);
    log.info(`出站成功：session=${sessionIdFor(plan.chatType, plan.threadId)} 分段=${plan.chunks.length} requestId=${requestId ?? "(无)"}`);

    if (requestId) {
      link()?.send({ type: "ack", time: Date.now(), data: { requestId, ok: true, error: null } });
    }
  } catch (err) {
    const reason = describeError(err);
    log.warn(`出站失败：requestId=${requestId ?? "(无)"} 原因=${reason}`);

    if (!requestId) {
      return;
    }

    try {
      link()?.send({ type: "ack", time: Date.now(), data: { requestId, ok: false, error: reason } });
    } catch (sendErr) {
      // 连 ack 都发不出去：说明连接断了，等重连后 Server 自己超时（设计 §3.4 会清理 pending）
      log.error(`回 ack 失败：${describeError(sendErr)}`);
    }
  }
}

async function handleQuery(
  frame: Frame,
  context: { adapter: DouyinImAdapter; link: () => ChannelLink | null; log: Logger },
  kind: "contacts" | "groups",
): Promise<void> {
  const { adapter, link, log } = context;
  const requestId = readRequestId(frame);
  if (!requestId) {
    log.warn(`${kind} 查询缺少 requestId，结果无法配对（已忽略）`);
    return;
  }

  try {
    if (kind === "contacts") {
      const contacts = await adapter.getContacts();
      log.info(`query.contacts 返回 ${contacts.length} 个好友`);
      link()?.send({
        type: "result",
        time: Date.now(),
        data: { requestId, ok: true, error: null, contacts },
      });
      return;
    }

    const groups = await adapter.getGroups();
    log.info(`query.groups 返回 ${groups.length} 个群`);
    link()?.send({
      type: "result",
      time: Date.now(),
      data: { requestId, ok: true, error: null, groups },
    });
  } catch (err) {
    const reason = describeError(err);
    log.warn(`query.${kind} 失败：${reason}`);

    // 设计 §3.5：取不到候选**不阻塞**，返回空列表 + 错误原因
    try {
      link()?.send({
        type: "result",
        time: Date.now(),
        data:
          kind === "contacts"
            ? { requestId, ok: false, error: reason, contacts: [] }
            : { requestId, ok: false, error: reason, groups: [] },
      });
    } catch (sendErr) {
      log.error(`回 result 失败：${describeError(sendErr)}`);
    }
  }
}

// ═══════════════════════ 入口 ═══════════════════════

async function main(): Promise<void> {
  const bootstrapLogger = createLogger("info");
  const config = await loadConfig(bootstrapLogger);
  const log = bootstrapLogger;
  log.setLevel(config.logLevel);

  log.info(
    `抖音 sidecar 启动：server=${redactToken(config.serverUrl)} dataDir=${config.dataDir} ` +
      `token=${config.token ? "已配置（不外显）" : "未配置（loopback 免验）"} node=${process.version}`,
  );

  // 连接层要等登录完成后才建（先登录再连 Server），入站回调通过这个引用拿到它
  let linkRef: ChannelLink | null = null;
/** 适配层引用：连上 Server 后要补报登录态（登录事件可能早于连接建立）。 */


  const adapter = new DouyinImAdapter({
    dataDir: config.dataDir,
    log,
    onInbound: (event) => {
      // 入站：归一化事件 → 设计 §3.3 的 inbound 帧（stranger 在翻译层就被丢掉）
      //
      // 这里先把 chatType 收窄一次：toInboundFrame 内部虽然也挡 stranger，
      // 但那是**函数内部**的判断，TS 不会替我们收窄外层的 event.chatType；
      // 不收窄就会在下面 sessionIdFor(...) 处报 "stranger" 不能赋给 "private" | "group"。
      if (event.chatType === "stranger") {
        log.debug(`陌生人消息已丢弃（设计 §4.1）：threadId=${event.threadId}`);
        return;
      }

      const chatType: "private" | "group" = event.chatType;

      const frame = toInboundFrame(event, (message) => log.warn(`入站翻译告警：${message}`));
      if (!frame) {
        log.debug(`入站帧翻译失败，已丢弃：threadId=${event.threadId}`);
        return;
      }

      try {
        linkRef?.send(frame);
        log.info(
          `入站已转发：session=${sessionIdFor(chatType, event.threadId)} ` +
            `messageId=${event.messageId ?? "(无)"}`,
        );
      } catch (err) {
        // 断线期间收到的消息发不出去：douyin-im 重连后会补拉，Server 侧还有 messageId 去重兜底
        log.warn(`入站消息未能转发（连接未就绪）：${describeError(err)}`);
      }
    },
    emit: (frame) => {
    try {
      linkRef?.send(frame);
    } catch (err) {
      log.debug(`推送登录帧失败（连接可能已断）：${describeError(err)}`);
    }
  },
  onLoginPrompt: (url, hint) => {
      // 登录要人机交互：URL 打到 stdout，远程宿主抓 stdout 后在自己浏览器里打开
      log.prompt([
        "【需要人工登录】抖音登录要人机交互（扫码 / 短信 / 滑块），sidecar 不能自己完成。",
        `原因 / 提示：${hint}`,
        `验证地址：${url}`,
        "",
        "远程部署时：把上面这个地址复制到本机浏览器打开，登录完成后 sidecar 会自动继续。",
        "登录态保存在 data/ 目录 —— 该目录必须备份，且绝不入库。",
      ]);
    },
  });

  // ① **先连 Server**（2026-10-28 调整顺序）：
  //    登录要人机交互，可能要等好几分钟扫码；而"发起登录 / 拿二维码"这些命令是
  //    **服务端通过 /ws/channel 下发的**。以前先 await login() 再连，结果扫码期间
  //    根本没有控制通道 —— 客户端点什么都发不出去（实测：connected=False + 400）。
  //    Server 没起来也不致命：ChannelLink 会一直退避重连。

  const context = {
    adapter,
    log,
    maxTextLength: config.maxTextLength,
    link: (): ChannelLink | null => linkRef,
  };

  const link = new ChannelLink({
    url: config.serverUrl,
    token: config.token,
    buildHello: () => adapter.buildHelloFrame(),
    heartbeatIntervalMs: config.heartbeatIntervalMs,
    deadAfterMs: config.deadAfterMs,
    log,
    onFrame: (frame) => handleServerFrame(frame, context),
    // 连上后补报登录态：登录事件早于连接建立，不补服务端就是 unknown
    onConnected: () => adapter.emitLoginState(),
  });
  // 入站回调通过 linkRef 拿到连接层（`context.link` 也读它）
  linkRef = link;

  const shutdown = (signal: string): void => {
    log.info(`收到 ${signal}，正在退出`);
    link.stop();
  };

  process.once("SIGINT", () => {
    shutdown("SIGINT");
  });
  process.once("SIGTERM", () => {
    shutdown("SIGTERM");
  });
  process.on("unhandledRejection", (reason) => {
    log.error(`未处理的 Promise 拒绝：${describeError(reason)}`);
  });

  // ③ 连接跑起来（不 await：它会一直重连到进程退出），
  //    同时并行去起 douyin-im —— 登录可能阻塞几分钟等扫码，
  //    但控制通道已经通了，客户端点「登录/退出」随时能拿到二维码或状态。
  const linkTask = link.start();

  try {
    await adapter.start();
  } catch (err) {
    // 登录失败不致命：连接照常跑，客户端还能点「登录（显示二维码）」重试
    log.error(`启动 douyin-im 失败（连接仍保持，可重试登录）：${describeError(err)}`);
    adapter.emitLoginState();
  }

  try {
    await linkTask;
  } finally {
    await adapter.stop();
  }

  log.info("抖音 sidecar 已退出");
}

main().catch((err: unknown) => {
  process.stderr.write(`[FATAL] 抖音 sidecar 启动失败：${describeError(err)}\n`);
  process.exitCode = 1;
});

