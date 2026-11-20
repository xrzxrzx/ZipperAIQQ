/**
 * `douyin-web-sidecar` 的**协议层（纯函数）**。
 *
 * 定位与 `douyin-sidecar/src/bridge.ts` 的「① 翻译层」一致：不碰网络、不碰浏览器、不碰 Playwright，
 * 只做「帧 ↔ 平台无关模型」的翻译与校验，便于 `node --test` 直接喂假数据（实施方案 §7 单测层）。
 *
 * 分四块：
 *   ① 帧外壳与解析（`{type, time, data}`）
 *   ② 会话 ID、分段、出站计划（含失败分类）
 *   ③ 入站归一化（私信 / 表情 / 图片 / 视频占位）与 `inbound` 帧
 *   ④ 幂等 LRU（平台消息 id 去重）
 *
 * ⚠️ 本文件**不只被自己用**：Server 侧 `DouyinProtocol` 也要能解这里吐出的帧。
 * 改字段名 = 改契约，必须两边一起改（实施方案 §6.2）。
 */

// ═══════════════════════════ 常量 ═══════════════════════════

/** 协议版本（`hello` 帧声明；与 Server 侧 `DouyinProtocol.Version` 对齐）。 */
export const PROTOCOL_VERSION = "1";

/** 平台标识（Server 侧 `ChatRef.Platform` / 会话前缀都用它）。 */
export const PLATFORM = "douyin";

/** 驱动标识：本 sidecar 走浏览器底座（Server 用它在 `douyin.driver` 上做闸门）。 */
export const DRIVER = "web";

/** 能力集（`hello` 帧）。引用（reply）浏览器侧只能降级，但仍声明 —— 见 {@link OutboundPlan.degraded}。 */
export const CAPABILITIES = ["text", "image", "mention", "reply"] as const;

/** 私信单条文本上限：网页版输入框没有硬截断，这里按服务端同一口径切段，保证两边一致。 */
export const DEFAULT_MAX_TEXT_LENGTH = 16_000;

// ═══════════════════════════ ① 帧外壳 ═══════════════════════════

/** 帧外壳：`{type, time, data}`（与既有 `/ws/channel` 完全同构）。 */
export interface Frame {
  type: string;
  time?: number;
  data?: Record<string, unknown>;
}

/** 把一帧文本解析成 {@link Frame}；畸形（非 JSON / 非对象 / 缺 type）返回 `null`。 */
export function parseFrame(text: string): Frame | null {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return null;
  }

  const record = asRecord(parsed);
  if (!record) {
    return null;
  }

  const type = str(record.type);
  if (!type) {
    return null;
  }

  const data = asRecord(record.data);
  const time = num(record.time);
  return time === undefined ? { type, data: data ?? {} } : { type, time, data: data ?? {} };
}

/** 拼一帧（带 `time`）。 */
export function frame(type: string, data: Record<string, unknown>): Frame {
  return { type, time: Date.now(), data };
}

// ═══════════════════════════ ② 会话与出站 ═══════════════════════════

export type ChatType = "private" | "group";

/** 会话 ID：`douyin:p<uid>` / `douyin:g<gid>`（与既有一致，换底座不改会话键）。 */
export function sessionIdFor(chatType: ChatType, threadId: string): string {
  return `${PLATFORM}:${chatType === "group" ? "g" : "p"}${threadId}`;
}

/** 反解会话 ID；认不出返回 `null`（不猜）。 */
export function parseSessionId(session: string): { chatType: ChatType; threadId: string } | null {
  const m = /^douyin:([pg])(.+)$/.exec(session.trim());
  if (!m) {
    return null;
  }

  const [, kind, id] = m;
  if (!kind || !id) {
    return null;
  }

  return { chatType: kind === "g" ? "group" : "private", threadId: id };
}

/** 单条消息段（与 Server 侧 `MessageSegment` 的线上形态一致）。 */
export type OutboundSegment =
  | { type: "text"; text: string }
  | { type: "mention"; userId: string }
  | { type: "image"; url?: string; data?: string }
  | { type: "reply"; messageId?: string };

/** 出站计划：翻译层把一帧 `outbound` 拆成「发到哪、发什么、哪些能力降级」。 */
export interface OutboundPlan {
  session: string;
  chatType: ChatType;
  threadId: string;
  /** 已按 `maxTextLength` 切好的文本块。 */
  chunks: string[];
  imageUrls: string[];
  /** 服务端内联的 base64 图（`/status` 这类服务端自己生成的图）。 */
  imageData: string[];
  mentions: { userId: string; label: string }[];
  replyTo: string | null;
  /** 降级说明：写进 `ack.degraded`，让服务端与模型知道「这条不是原样发出去的」。 */
  degraded: string[];
}

/** 出站计划失败（带可机器识别的 `code`，服务端原样回填给模型）。 */
export class OutboundError extends Error {
  readonly code: FailureCode;

  constructor(code: FailureCode, message: string) {
    super(message);
    this.name = "OutboundError";
    this.code = code;
  }
}

/** 解析一帧 `outbound` 成出站计划。 */
export function buildOutboundPlan(
  frame: Frame,
  options: { maxTextLength?: number } = {},
): OutboundPlan {
  const data = frame.data ?? {};
  const session = str(data.session);
  if (!session) {
    throw new OutboundError("bad_request", "outbound 帧缺少 session");
  }

  const target = parseSessionId(session);
  if (!target) {
    throw new OutboundError("bad_request", `session 不是抖音会话 ID：${session}`);
  }

  const maxTextLength = options.maxTextLength && options.maxTextLength > 0
    ? options.maxTextLength
    : DEFAULT_MAX_TEXT_LENGTH;

  const degraded: string[] = [];
  const chunks: string[] = [];
  const imageUrls: string[] = [];
  const imageData: string[] = [];
  const mentions: { userId: string; label: string }[] = [];

  const segments = Array.isArray(data.segments) ? data.segments : [];
  for (const raw of segments) {
    const segment = asRecord(raw);
    if (!segment) {
      continue;
    }

    switch (str(segment.type)) {
      case "text": {
        const text = str(segment.text);
        if (text) {
          chunks.push(...chunkText(text, maxTextLength));
        }
        break;
      }

      case "mention": {
        const userId = str(segment.userId) ?? str(segment.uid);
        if (userId && isNumericUid(userId)) {
          mentions.push({ userId, label: `@${userId}` });
        } else if (userId) {
          // 群 @ 要求数字 uid；拿不到就退化成文本，别把整条消息赔进去（与既有 sidecar 同口径）。
          degraded.push("mention");
          chunks.push(...chunkText(`@${userId}`, maxTextLength));
        }
        break;
      }

      case "image": {
        const url = str(segment.url);
        const inline = str(segment.data);
        if (url) {
          imageUrls.push(url);
        } else if (inline) {
          imageData.push(inline);
        } else {
          degraded.push("image");
        }
        break;
      }

      case "reply": {
        // 引用由帧级 replyTo 承载（与既有 sidecar 一致），段本身不用再发。
        break;
      }

      default: {
        const label = str(segment.type) ?? "未知";
        degraded.push(`segment:${label}`);
        break;
      }
    }
  }

  const replyTo = str(data.replyTo) ?? null;
  if (replyTo) {
    // 网页版「引用」要先定位到那条消息再点「回复」；按 id 定位不稳，
    // 一律**降级成普通消息 + 明确标注**（实施方案 B1 关键设计：不得伪装成功）。
    degraded.push("reply_to");
  }

  if (chunks.length === 0 && imageUrls.length === 0 && imageData.length === 0) {
    throw new OutboundError("bad_request", "消息没有可发送的内容（只有引用 / 被降级的段）");
  }

  return {
    session,
    chatType: target.chatType,
    threadId: target.threadId,
    chunks,
    imageUrls,
    imageData,
    mentions,
    replyTo,
    degraded: dedupe(degraded),
  };
}

/** 按上限切文本，**不切断代理对**（半个 emoji 会变成乱码或直接发送失败）。 */
export function chunkText(text: string, maxLength: number): string[] {
  if (!text) {
    return [];
  }

  if (maxLength <= 0 || text.length <= maxLength) {
    return [text];
  }

  const out: string[] = [];
  let start = 0;

  while (start < text.length) {
    let length = Math.min(maxLength, text.length - start);

    if (
      start + length < text.length &&
      isHighSurrogate(text.charCodeAt(start + length - 1)) &&
      isLowSurrogate(text.charCodeAt(start + length))
    ) {
      length -= 1;
    }

    if (length <= 0) {
      length = 1; // maxLength == 1 且首字符就是高代理：躲不开，硬切（否则死循环）
    }

    out.push(text.slice(start, start + length));
    start += length;
  }

  return out;
}

// ═══════════════════════════ ③ 入站归一化 ═══════════════════════════

/** 平台消息类型（数值来自 `im.proto` 的 `MessageType`，只列本 sidecar 认识的）。 */
export const MESSAGE_TYPE = {
  /** 文本（新协议）。 */
  text: 10001,
  sticker: 10002,
  image: 10003,
  video: 10004,
  file: 10005,
  audio: 10006,
  /** 文本（旧协议，实测仍在用）。 */
  legacyText: 7,
  legacyImage: 2,
  legacyAudio: 3,
  legacyVideo: 4,
  legacyEmoji: 5,
  legacyFile: 6,
  /** 分享卡片（视频 / 作品），content 里带 `itemId`。 */
  shareCard: 105,
  /** 命令型事件（已读回执等）：**必须丢弃**，否则会被当成消息回给用户。 */
  command: 50001,
} as const;

/** `im_proto.MessageBody` 里本 sidecar 用得到的字段（数值一律字符串化，避免 JS 精度丢失）。 */
export interface ProtoMessageBody {
  conversationId?: string;
  conversationType?: number;
  serverMessageId?: string;
  conversationShortId?: string;
  messageType?: number;
  sender?: string;
  content?: string;
  createTime?: string;
  secSender?: string;
  referenceInfo?: { referencedMessageId?: string };
}

/** 归一化后的入站事件（翻译层的输出，也是单测的输入）。 */
export interface NormalizedInbound {
  chatType: ChatType;
  threadId: string;
  conversationShortId?: string;
  messageId: string;
  senderUid: string;
  senderNickname?: string;
  text?: string;
  /** 视频 / 表情 / 文件等降级占位（与既有 sidecar 同口径，AI 至少知道"发了个视频"）。 */
  placeholders: string[];
  imageUrls: string[];
  mentions: { uid: string; name?: string }[];
  replyTo?: string;
  occurredAtMs: number;
  /** `false` = 平台没给时间戳，用了本地兜底（会记 warn 便于排查）。 */
  timeFromPlatform: boolean;
  /** 分享卡片的视频 id（B4 取媒体用；B1 只是留字段）。 */
  sharedItemId?: string;
}

/**
 * 私信消息 → 归一化事件。
 *
 * @param body `im_proto.MessageBody`
 * @param selfUid 自己的 uid（用于从 `conversationId` 里推出「对方」是谁）
 * @returns 无法处理（命令型事件 / 缺会话）返回 `null`
 */
export function normalizePrivateMessage(
  body: ProtoMessageBody,
  selfUid: string | null,
  now: () => number = Date.now,
): NormalizedInbound | null {
  const messageType = body.messageType ?? 0;
  if (messageType === MESSAGE_TYPE.command) {
    // 已读回执 / 撤回等命令：不是人发的消息，转发出去会被 AI 当话回（T0.9 实测踩到）
    return null;
  }

  const conversationId = body.conversationId ?? "";
  const conversationType = body.conversationType ?? 0;
  const chatType: ChatType = conversationType === 2 ? "group" : "private";

  const peer = resolveThreadId(conversationId, body.sender ?? "", selfUid, chatType);
  if (!peer) {
    return null;
  }

  const content = parseContentJson(body.content);
  const placeholders: string[] = [];
  const imageUrls: string[] = [];
  const mentions: { uid: string; name?: string }[] = [];
  let text: string | undefined;
  let sharedItemId: string | undefined;

  switch (messageType) {
    case MESSAGE_TYPE.text:
    case MESSAGE_TYPE.legacyText: {
      text = firstNonEmpty(str(content.text), str(content.msgHint));
      if (!text) {
        placeholders.push("[文本]");
      }
      break;
    }

    case MESSAGE_TYPE.image:
    case MESSAGE_TYPE.legacyImage: {
      const urls = collectImageUrls(content);
      if (urls.length > 0) {
        imageUrls.push(...urls);
      } else {
        placeholders.push("[图片]");
      }
      break;
    }

    case MESSAGE_TYPE.legacyEmoji:
    case MESSAGE_TYPE.sticker: {
      const name = firstNonEmpty(str(content.display_name), str(content.display));
      placeholders.push(name ? `[表情:${name}]` : "[表情]");
      break;
    }

    case MESSAGE_TYPE.video:
    case MESSAGE_TYPE.legacyVideo: {
      placeholders.push("[视频]");
      break;
    }

    case MESSAGE_TYPE.audio:
    case MESSAGE_TYPE.legacyAudio: {
      placeholders.push("[语音]");
      break;
    }

    case MESSAGE_TYPE.file:
    case MESSAGE_TYPE.legacyFile: {
      placeholders.push("[文件]");
      break;
    }

    case MESSAGE_TYPE.shareCard: {
      sharedItemId = firstNonEmpty(str(content.itemId), str(content.item_id));
      const title = firstNonEmpty(str(content.title), str(content.desc), str(content.text));
      placeholders.push(title ? `[分享:${title}]` : "[分享卡片]");
      break;
    }

    default: {
      placeholders.push(`[消息类型:${messageType}]`);
      break;
    }
  }

  const createTime = toNumber(body.createTime);
  const occurredAtMs = createTime && createTime > 0 ? normalizeEpochMs(createTime) : now();

  return {
    chatType,
    threadId: peer,
    conversationShortId: body.conversationShortId || undefined,
    messageId: body.serverMessageId || `${peer}:${occurredAtMs}`,
    senderUid: body.sender || "unknown",
    senderNickname: senderNicknameOf(content),
    text,
    placeholders,
    imageUrls,
    mentions,
    replyTo: body.referenceInfo?.referencedMessageId || undefined,
    occurredAtMs,
    timeFromPlatform: Boolean(createTime && createTime > 0),
    sharedItemId,
  };
}

/** 归一化事件 → `inbound` 帧（形状与既有 sidecar **同构**，Server 侧解析不用改）。 */
export function toInboundFrame(event: NormalizedInbound): Frame {
  const segments: Record<string, unknown>[] = [];

  if (event.replyTo) {
    segments.push({ type: "reply", messageId: event.replyTo });
  }

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
    segments.push({ type: "mention", userId: mention.uid, nickname: mention.name });
  }

  return frame("inbound", {
    type: event.chatType,
    chatType: event.chatType,
    threadId: event.threadId,
    session: sessionIdFor(event.chatType, event.threadId),
    conversationShortId: event.conversationShortId,
    serverMessageId: event.messageId,
    sender: {
      userId: event.senderUid,
      nickname: event.senderNickname,
    },
    segments,
    time: event.occurredAtMs,
  });
}

/**
 * 从 `conversationId`（形如 `0:1:<uidA>:<uidB>`）里推出「对方」的 id。
 *
 * 私聊取「不是自己的那个 uid」；群聊取最后一段（群 id）。
 * 拿不到就返回 `null` —— 宁可丢一条，也不要凭空编一个会话出来。
 */
export function resolveThreadId(
  conversationId: string,
  sender: string,
  selfUid: string | null,
  chatType: ChatType,
): string | null {
  const parts = conversationId.split(":").filter((part) => part.length > 0);
  if (parts.length === 0) {
    return null;
  }

  if (chatType === "group") {
    return parts[parts.length - 1] ?? null;
  }

  const uids = parts.slice(2);
  if (selfUid) {
    const other = uids.find((uid) => uid !== selfUid);
    if (other) {
      return other;
    }
  }

  if (sender && uids.includes(sender)) {
    const other = uids.find((uid) => uid !== sender);
    if (other) {
      return other;
    }
  }

  return uids[0] ?? null;
}

// ═══════════════════════════ ④ 幂等 LRU ═══════════════════════════

/**
 * 有上限的「见过的 id」集合（LRU）。
 *
 * 为什么需要：WS 帧在重连 / 重放时会重复投递；服务端也有唯一索引兜底，
 * 但在这里挡掉能省一次往返，也避免「同一句话被回两次」这种最难解释的现象。
 */
export class IdLru {
  private readonly order: string[] = [];
  private readonly seen = new Set<string>();

  private readonly capacity: number;

  constructor(capacity: number = 500) {
    if (capacity <= 0) {
      throw new Error("IdLru 的容量必须为正数");
    }

    this.capacity = capacity;
  }

  /** 见过就返回 `false`（= 应当丢弃），没见过则记下并返回 `true`。 */
  admit(id: string): boolean {
    if (!id) {
      return true; // 没有 id 时不去重（去重靠 id，不能靠猜）
    }

    if (this.seen.has(id)) {
      return false;
    }

    this.seen.add(id);
    this.order.push(id);

    while (this.order.length > this.capacity) {
      const evicted = this.order.shift();
      if (evicted !== undefined) {
        this.seen.delete(evicted);
      }
    }

    return true;
  }

  get size(): number {
    return this.seen.size;
  }
}

// ═══════════════════════════ 失败分类 ═══════════════════════════

/**
 * 失败码：服务端把它原样回填给模型（`ack.code`），便于「模型知道该不该重试」。
 *
 * - `auth`：登录态失效 / 被踢下线 —— 重试无用，要人扫码
 * - `risk`：风控拦截（滑块 / 频繁操作）—— 要冷却，不能立刻重试
 * - `not_found`：找不到会话 / 找不到输入框 —— 选择器或会话名不匹配
 * - `timeout`：页面动作超时
 * - `readback`：发出去但回读不一致（**不算成功**）
 * - `upload`：图片上传失败
 * - `bad_request`：帧本身不合法（session / 段）
 * - `page`：页面崩了 / 被关掉（会触发重建）
 */
export type FailureCode =
  | "auth"
  | "risk"
  | "not_found"
  | "timeout"
  | "readback"
  | "upload"
  | "bad_request"
  | "page"
  | "unknown";

export interface FailureInfo {
  code: FailureCode;
  message: string;
}

/** 把任意抛出物翻译成「码 + 一句人话」（中文，直接进日志与 ack）。 */
export function classifyFailure(error: unknown): FailureInfo {
  if (error instanceof OutboundError) {
    return { code: error.code, message: error.message };
  }

  const raw = error instanceof Error ? error.message : String(error);
  const message = raw.length > 400 ? `${raw.slice(0, 400)}…` : raw;

  const lower = message.toLowerCase();

  if (/target (page|closed)|browser has been closed|has been closed|crash/i.test(lower)) {
    return { code: "page", message: `页面不可用：${message}` };
  }

  if (/timeout|timed out|超时/i.test(lower)) {
    return { code: "timeout", message: `页面动作超时：${message}` };
  }

  if (/验证码|滑块|captcha|verify|风控|风险|频繁/i.test(message)) {
    return { code: "risk", message: `疑似风控拦截：${message}` };
  }

  if (/登录|扫码|未登录|login|session expired/i.test(message)) {
    return { code: "auth", message: `登录态异常：${message}` };
  }

  if (/找不到|未找到|no such|not found/i.test(message)) {
    return { code: "not_found", message };
  }

  if (/上传|upload/i.test(lower)) {
    return { code: "upload", message };
  }

  return { code: "unknown", message };
}

// ═══════════════════════════ 小工具 ═══════════════════════════

export function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

export function str(value: unknown): string | undefined {
  if (typeof value === "string") {
    return value.length > 0 ? value : undefined;
  }

  if (typeof value === "number" && Number.isFinite(value)) {
    return String(value);
  }

  return undefined;
}

export function num(value: unknown): number | undefined {
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === "string" && value.trim().length > 0) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : undefined;
  }

  return undefined;
}

function toNumber(value: unknown): number | undefined {
  return num(value);
}

function firstNonEmpty(...values: (string | undefined)[]): string | undefined {
  for (const value of values) {
    if (value !== undefined && value.length > 0) {
      return value;
    }
  }

  return undefined;
}

function dedupe(values: string[]): string[] {
  return [...new Set(values)];
}

function isHighSurrogate(code: number): boolean {
  return code >= 0xd800 && code <= 0xdbff;
}

function isLowSurrogate(code: number): boolean {
  return code >= 0xdc00 && code <= 0xdfff;
}

/** 群 @ 要求数字 uid（与既有 sidecar 同口径）。 */
export function isNumericUid(uid: string): boolean {
  return uid.length > 0 && /^[0-9]+$/.test(uid);
}

/** 平台时间戳：毫秒 / 秒都见得到，按量级判（>= 1e11 视为毫秒）。 */
function normalizeEpochMs(raw: number): number {
  return raw >= 100_000_000_000 ? raw : raw * 1000;
}

function parseContentJson(raw: string | undefined): Record<string, unknown> {
  if (!raw) {
    return {};
  }

  try {
    return asRecord(JSON.parse(raw)) ?? {};
  } catch {
    // 不是 JSON：当成纯文本（旧协议里有直接放正文的情况）
    return { text: raw };
  }
}

/** 图片 URL：`content.url.url_list` / `content.url_list` / `content.url.uri` 都试一遍。 */
function collectImageUrls(content: Record<string, unknown>): string[] {
  const urls: string[] = [];

  const pushList = (value: unknown): void => {
    if (Array.isArray(value)) {
      for (const item of value) {
        const url = str(item);
        if (url && url.startsWith("http")) {
          urls.push(url);
        }
      }
    }
  };

  pushList(content.url_list);

  const url = asRecord(content.url);
  if (url) {
    pushList(url.url_list);
    const uri = str(url.uri);
    if (uri && uri.startsWith("http")) {
      urls.push(uri);
    }
  }

  const direct = str(content.url);
  if (direct && direct.startsWith("http")) {
    urls.push(direct);
  }

  return dedupe(urls);
}

/** 昵称：私信 content 里没有，靠 `display_name` / `nickname` 兜（多数情况为空，靠会话标题补）。 */
function senderNicknameOf(content: Record<string, unknown>): string | undefined {
  return firstNonEmpty(str(content.nickname), str(content.user_name));
}
