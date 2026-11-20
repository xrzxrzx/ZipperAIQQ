/**
 * 入站（**L1 主路：WS 旁听**）。
 *
 * 为什么不用 DOM 轮询：`/chat` 的消息列表是**虚拟滚动**（实测节点数 123 →216 →123），
 * 离开视口的消息会被卸载，纯 DOM 轮询必漏（实施方案 §G9）。
 * 而页面自己在跑一条 `wss://frontier-im.douyin.com/ws/v2`，帧里**带完整消息体**
 * （私信 / 「评论了你」/「提到了你」都在这条流上，靠 `service` 区分）。
 *
 * 所以这里做的是「**旁听页面自己发的请求**」：
 *   - 不自己造签名、不裸调接口（签名与设备指纹由页面生成，跟它斗没收益）
 *   - 帧体用 `proto/im.proto` 解码（与 `douyin-im` 同一套 schema，字段号照抄）
 *
 * 分层（与 §G9 对齐）：
 *   L1 本文件（WS 帧）→ L2 由服务端按 `serverMessageId` 去重 / 补拉 → L3 DOM 兜底（`scanDom`）
 */

import protobuf from "protobufjs";
import type { Page } from "playwright";
import type { BrowserLogger } from "./browser.js";
import {
  IdLru,
  normalizePrivateMessage,
  toInboundFrame,
  type Frame as ChannelFrame,
  type ProtoMessageBody,
} from "./protocol.js";
import type { ConversationRegistry } from "./send-api.js";
import { currentConversationName, readTail, type ConversationNameCache } from "./site.js";

/** `Frame.service` 取值（T0.8 实测）。 */
const SERVICE = {
  /** protobuf `im_proto.Response`：私信消息推送。 */
  privateMessage: 5,
  /** 纯 JSON：互动消息（评论 / @ / 回复）。 */
  interaction: 20003,
  /** 纯 JSON：未读计数（本 sidecar 只记 debug，不做业务）。 */
  noticeCount: 20313,
} as const;

/** `Response.cmd`：500 = 新消息通知。 */
const CMD_NEW_MESSAGE = 500;

export interface InboundListenerOptions {
  log: BrowserLogger;
  /** proto 文件绝对路径（随包发布的 `proto/im.proto`）。 */
  protoPath: string;
  /** 发一帧给 Server。 */
  emit: (frame: ChannelFrame) => void;
  /** 会话名缓存（给入站事件补昵称）。 */
  names: ConversationNameCache;
  /** 会话坐标登记表：出站 API 只用它，不需要 DOM 列表。 */
  conversations: ConversationRegistry;
  /** 取自己的 uid（拿到之前返回 null）。 */
  selfUid: () => string | null;
  /** 学到「自己」的 IM uid 时回调（web 侧探测值与 IM uid 不一致时靠它纠正）。 */
  onSelfLearned?: (selfUid: string) => void;
}

/** 入站旁听器。 */
export class InboundListener {
  private readonly log: BrowserLogger;
  private readonly emit: (frame: ChannelFrame) => void;
  private readonly names: ConversationNameCache;
  private readonly conversations: ConversationRegistry;
  private readonly selfUid: () => string | null;
  private readonly onSelfLearned?: (selfUid: string) => void;
  private readonly protoPath: string;

  private protoRoot: protobuf.Root | null = null;
  private frameType: protobuf.Type | null = null;
  private responseType: protobuf.Type | null = null;

  /** 幂等窗口（平台消息 id）。 */
  private readonly seen = new IdLru(800);

  /** 已挂过的页面（避免重复挂监听）。 */
  private readonly attached = new WeakSet<Page>();

  /** 最近一次收到任何 WS 帧的时间（DOM 兜底的判据）。 */
  private lastFrameAt = 0;

  /** 从会话 ID 学到的自身 IM uid（优先于探测值）。 */
  private learnedSelf: string | null = null;

  /** 统计（`browser.state` 与排障用）。 */
  private stats = { frames: 0, messages: 0, interactions: 0, strangers: 0, dropped: 0 };

  constructor(options: InboundListenerOptions) {
    this.log = options.log;
    this.emit = options.emit;
    this.names = options.names;
    this.conversations = options.conversations;
    this.selfUid = options.selfUid;
    this.onSelfLearned = options.onSelfLearned;
    this.protoPath = options.protoPath;
  }

  /** 当前统计快照。 */
  get counters(): {
    frames: number;
    messages: number;
    interactions: number;
    strangers: number;
    dropped: number;
  } {
    return { ...this.stats };
  }

  /** 最近一次收到 WS 帧距今多少毫秒（没收到过返回 `Infinity`）。 */
  get silenceMs(): number {
    return this.lastFrameAt === 0 ? Number.POSITIVE_INFINITY : Date.now() - this.lastFrameAt;
  }

  /** 加载 proto（首次调用才真读盘）。 */
  async load(): Promise<void> {
    if (this.frameType && this.responseType) {
      return;
    }

    const root = await protobuf.load(this.protoPath);
    this.protoRoot = root;
    this.frameType = root.lookupType("im_proto.Frame");
    this.responseType = root.lookupType("im_proto.Response");
    this.log.info(`入站协议已加载：${this.protoPath}`);
  }

  /** 往一个页面上挂监听（新页面 / 重建后的页面都要挂）。 */
  async attach(page: Page): Promise<void> {
    if (this.attached.has(page)) {
      return;
    }

    this.attached.add(page);
    await this.load();

    page.on("websocket", (socket) => {
      const url = socket.url();
      if (!/frontier-im\.douyin\.com/.test(url)) {
        return;
      }

      this.log.info(`已旁听入站 WS：${url.split("?")[0]}`);
      socket.on("framereceived", (event) => {
        try {
          this.handleFrame(event.payload);
        } catch (error) {
          this.log.debug(`入站帧处理失败：${error instanceof Error ? error.message : String(error)}`);
        }
      });
      socket.on("close", () => this.log.info("入站 WS 已关闭（页面重连时会再挂上）"));
    });

    // 页面已经开着 WS（挂监听晚了一步）时，用 DOM 兜底不至于失明
    this.log.debug("入站监听已挂载到页面");
  }

  /**
   * L3 DOM 兜底：只在「长时间没收到任何 WS 帧」时扫一眼当前会话末尾。
   *
   * 判据保守（宁少不多）：已知 id 之外的文本才发，且打 `source:"dom"` 标。
   */
  async scanDom(page: Page, limit = 5): Promise<void> {
    if (this.silenceMs < 5 * 60 * 1000) {
      return; // L1 还活着，不需要兜底
    }

    // 兜底也要知道"这条属于哪个会话"：只有标题没有 id 时用名字缓存反查，
    // 反查不到就**不发**（挂到假会话 `douyin:pcurrent` 上比丢一条更糟：数据脏了）。
    const title = await currentConversationName(page).catch(() => null);
    const threadId = this.names.idForName(title);
    if (!threadId) {
      this.log.warn(
        `DOM 兜底放弃：认不出当前会话（标题=${title ?? "未知"}）；等 WS 恢复或先打开该会话`,
      );
      return;
    }

    const bubbles = await readTail(page, limit).catch(() => []);
    for (const bubble of bubbles) {
      const text = bubble.text.trim();
      if (!text) {
        continue;
      }

      // 幂等键带时间桶：同一句话过几分钟再发一次是**正常**的，不能被去重吃掉
      const bucket = Math.floor(Date.now() / (5 * 60 * 1000));
      const id = `dom:${threadId}:${hash(text)}:${bucket}`;
      if (!this.seen.admit(id)) {
        continue;
      }

      this.log.warn(
        `DOM 兜底转发一条入站消息（WS 静默 ${Math.round(this.silenceMs / 1000)}s，会话 ${threadId}）：${text.slice(0, 30)}`,
      );

      const frame = toInboundFrame({
        chatType: "private",
        threadId,
        messageId: id,
        senderUid: "unknown",
        senderNickname: title ?? undefined,
        text,
        placeholders: [],
        imageUrls: [],
        mentions: [],
        occurredAtMs: Date.now(),
        timeFromPlatform: false,
      });

      // G9 的 L3 要求：**标记来源与不完整风险**（服务端据此留档，别当正常消息用）
      frame.data = { ...frame.data, source: "dom", incomplete: true };
      this.emit(frame);
    }
  }

  // ─────────────────────────── 内部 ───────────────────────────

  private handleFrame(payload: unknown): void {
    const buffer = toBuffer(payload);
    if (!buffer) {
      return;
    }

    this.lastFrameAt = Date.now();
    this.stats.frames += 1;

    const frameType = this.frameType;
    if (!frameType) {
      return;
    }

    let decoded: { service?: number; method?: number; payload?: Uint8Array; headers?: { key: string; value: string }[] };
    try {
      decoded = frameType.toObject(frameType.decode(buffer), {
        longs: String,
        bytes: Buffer,
        defaults: false,
      }) as typeof decoded;
    } catch (error) {
      this.log.debug(`帧解码失败（${buffer.length}B）：${error instanceof Error ? error.message : String(error)}`);
      return;
    }

    const service = Number(decoded.service ?? 0);
    switch (service) {
      case SERVICE.privateMessage:
        this.handlePrivateMessage(decoded.payload, decoded.headers);
        return;

      case SERVICE.interaction: {
        // 评论 / @ / 回复：B5 才做业务响应，B1 只计数 + debug（别当成私信回出去）
        this.stats.interactions += 1;
        const text = decoded.payload ? Buffer.from(decoded.payload).toString("utf8") : "";
        const parsed = safeJson(text);
        this.log.debug(
          `互动消息（评论/@，B5 处理）：${String(parsed?.content ?? "").slice(0, 60)}`,
        );
        return;
      }

      case SERVICE.noticeCount:
        return;

      default:
        return;
    }
  }

  private handlePrivateMessage(payload?: Uint8Array, headers?: { key: string; value: string }[]): void {
    const responseType = this.responseType;
    if (!responseType || !payload) {
      return;
    }

    let response: {
      cmd?: number;
      statusCode?: number;
      body?: {
        hasNewMessageNotify?: { message?: ProtoMessageBody };
        strangerHasNewMessageNotify?: { message?: ProtoMessageBody };
      };
    };

    try {
      response = responseType.toObject(responseType.decode(payload), {
        longs: String,
        bytes: Buffer,
        defaults: false,
      }) as typeof response;
    } catch (error) {
      this.log.debug(`Response 解码失败：${error instanceof Error ? error.message : String(error)}`);
      return;
    }

    if (Number(response.cmd ?? 0) !== CMD_NEW_MESSAGE) {
      this.log.debug(`service=5 但 cmd=${response.cmd ?? 0}（不是新消息推送），已忽略`);
      return;
    }

    const normalMessage = response.body?.hasNewMessageNotify?.message;
    const strangerMessage = response.body?.strangerHasNewMessageNotify?.message;

    if (strangerMessage) {
      // 陌生人盒：第一版不响应（与既有 sidecar 同口径），但**要记录**——
      // 静默丢会让人以为"协议没通"，排障时白查一轮。
      this.stats.strangers += 1;
      this.log.info(
        `陌生人盒消息（第一版不响应）：conv=${strangerMessage.conversationId ?? "-"} ` +
          `type=${strangerMessage.messageType ?? "-"} sender=${strangerMessage.sender ?? "-"}`,
      );
      return;
    }

    const message = normalMessage;
    if (!message) {
      this.log.warn(
        `service=5 cmd=500 但 body 里没有 message（可能有新的 notify 分支未解出）：${Object.keys(response.body ?? {}).join(",") || "空"}`,
      );
      return;
    }

    const selfUid = this.selfUid();
    const normalized = normalizePrivateMessage(message, selfUid);
    if (!normalized) {
      this.stats.dropped += 1;

      if (!selfUid && Number(message.messageType ?? 0) !== 50001) {
        // 自己是谁都不知道 → 分不清"对方" → 这条只能丢；但必须**明说**，否则像丢消息的鬼
        this.log.warn(
          `入站消息被丢弃：还没探测到自身 uid（等登录探测完成），conv=${message.conversationId ?? "-"}`,
        );
      } else {
        this.log.debug(`入站消息被丢弃（命令型 / 缺会话）：type=${message.messageType} conv=${message.conversationId ?? "-"}`);
      }

      return;
    }

    if (!this.seen.admit(normalized.messageId)) {
      this.stats.dropped += 1;
      this.log.debug(`重复入站消息已丢弃：${normalized.messageId}`);
      return;
    }

    // 自己是谁：web 侧探测到的 uid 可能与 IM uid 不一致 —— 用会话 ID 反推出来的值纠正它
    if (!selfUid && normalized.selfCandidate && !this.learnedSelf) {
      this.learnedSelf = normalized.selfCandidate;
      this.log.info(`已从会话 ID 学到自身 IM uid：${this.learnedSelf}`);
      this.onSelfLearned?.(this.learnedSelf);
    }

    // 昵称：协议体里没有，靠会话名缓存（列表扫描 / 打开会话时学的）
    const known = this.names.get(normalized.threadId);
    if (!normalized.senderNickname && known) {
      normalized.senderNickname = known;
    }

    // 记下「这个会话怎么发」：conversation_id 只出现在入站帧里，出站全靠它
    this.conversations.remember(normalized.threadId, {
      conversationId: message.conversationId ?? "",
      conversationShortId: message.conversationShortId,
      conversationType: Number(message.conversationType ?? 1),
      lastText: (normalized.text ?? "").trim().slice(0, 40),
    });

    this.stats.messages += 1;
    this.log.info(
      `入站私信：session=douyin:p${normalized.threadId} msgId=${normalized.messageId} ` +
        `type=${message.messageType} frameMsg=${headerOf(headers, ":x_frontier_msg_id") ?? "-"}`,
    );

    this.emit(toInboundFrame(normalized));
  }
}

function toBuffer(payload: unknown): Buffer | null {
  if (Buffer.isBuffer(payload)) {
    return payload;
  }

  if (payload instanceof Uint8Array) {
    return Buffer.from(payload);
  }

  if (typeof payload === "string") {
    // 字符串帧出现说明连的不是二进制流（不同版本可能变），这条路走 JSON
    return Buffer.from(payload, "utf8");
  }

  return null;
}

function headerOf(headers: { key: string; value: string }[] | undefined, key: string): string | undefined {
  return headers?.find((header) => header.key === key)?.value;
}

function safeJson(text: string): Record<string, unknown> | null {
  try {
    const parsed: unknown = JSON.parse(text);
    return typeof parsed === "object" && parsed !== null ? (parsed as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/** 稳定的短哈希（DOM 兜底的幂等键用；不需要抗碰撞，只要同文本同值）。 */
function hash(text: string): string {
  let value = 0;
  for (let i = 0; i < text.length; i += 1) {
    value = (value * 31 + text.charCodeAt(i)) | 0;
  }

  return Math.abs(value).toString(36);
}
