#!/usr/bin/env node
/**
 * ZipperAIQQ 抖音 **Web sidecar**（浏览器底座）入口。
 *
 * 与已移除的 `douyin-sidecar`（`douyin-im` 底座）的区别：
 *   - 那边：`douyin-im` 管登录 + 收发事件；这边：Playwright 驱动真实 Chromium 走网页版
 *   - 为什么换：平台按客户端版本拒发（`status=3 check=10509`），`douyin-im` 无新版可升；
 *     而网页版能发私信、能读评论（PoC P-4/P-5 已实机验收）
 *
 * 分工（与本目录其它文件）：
 *   `protocol.ts` 纯函数翻译 · `browser.ts` Chromium 生命周期 · `login.ts` 登录编排
 *   `outbound.ts` 出站 · `inbound.ts` 入站旁听 · `site.ts` 站点知识
 *   **本文件只做编排**：配置 → 连 Server → 分发帧 → 定时体检
 *
 * 鉴权：`?token=` query（与既有 sidecar 同口径：Node 内置 WebSocket 不支持自定义头）。
 */

import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";

import { DouyinBrowser, type BrowserLogger, type BrowserStateSnapshot } from "./browser.js";
import { InboundListener } from "./inbound.js";
import { LoginFlow } from "./login.js";
import { OutboundSender } from "./outbound.js";
import {
  buildOutboundPlan,
  CAPABILITIES,
  classifyFailure,
  DRIVER,
  frame as makeFrame,
  num,
  parseFrame,
  PLATFORM,
  PROTOCOL_VERSION,
  str,
  type Frame,
} from "./protocol.js";
import { ConversationNameCache, listConversations, openChatPage, pageHealth, SITE } from "./site.js";

// ═══════════════════════════ 配置 ═══════════════════════════

interface SidecarConfig {
  serverUrl: string;
  token: string | null;
  profileDir: string;
  workDir: string;
  headless: boolean;
  recycleHours: number;
  maxJsHeapMb: number;
  maxTextLength: number;
  heartbeatIntervalMs: number;
  deadAfterMs: number;
  logLevel: "debug" | "info" | "warn" | "error";
  configPath: string;
}

const DEFAULT_SERVER_URL = "ws://127.0.0.1:5180/ws/channel";
const DEFAULT_PROFILE_DIR = "data/douyin-web-profile";
const DEFAULT_WORK_DIR = "data/tmp-media";
const DEFAULT_MAX_TEXT_LENGTH = 16_000;
const DEFAULT_HEARTBEAT_MS = 30_000;
const DEFAULT_DEAD_AFTER_MS = 90_000;
const DEFAULT_RECYCLE_HOURS = 6;
const DEFAULT_MAX_JS_HEAP_MB = 600;

/** WebSocket.OPEN 的字面值（不引 DOM lib）。 */
const SOCKET_OPEN = 1;

/** 重连退避：1s 起步翻倍，封顶 15s（与既有 sidecar 同口径）。 */
const RECONNECT_INITIAL_MS = 1_000;
const RECONNECT_MAX_MS = 15_000;

/** 体检周期：刷新会话名 + 播 `browser.state` + DOM 兜底。 */
const HEALTH_INTERVAL_MS = 60_000;

async function loadConfig(log: Logger): Promise<SidecarConfig> {
  const fromEnv = str(process.env.ZIPPER_DOUYIN_WEB_CONFIG);
  const configPath = fromEnv ?? path.resolve(process.cwd(), "config.json");

  let fileConfig: Record<string, unknown> = {};
  if (existsSync(configPath)) {
    try {
      const parsed: unknown = JSON.parse(await readFile(configPath, "utf8"));
      fileConfig = (typeof parsed === "object" && parsed !== null ? parsed : {}) as Record<string, unknown>;
      log.debug(`已读取配置文件：${configPath}`);
    } catch (error) {
      throw new Error(`配置文件解析失败（${configPath}）：${error instanceof Error ? error.message : String(error)}`);
    }
  } else if (fromEnv) {
    throw new Error(`ZIPPER_DOUYIN_WEB_CONFIG 指向的文件不存在：${configPath}`);
  } else {
    log.warn(`未找到 ${configPath}，改用默认值 + 环境变量（详见 README「配置」）`);
  }

  if (fileConfig.maxHeapMb !== undefined) {
    // 改名不静默：旧键还在的机器上，阈值会悄悄回落到默认值（审查 N-5）
    log.warn("配置项 maxHeapMb 已更名为 maxJsHeapMb（语义是渲染进程 JS 堆），本次按新键/默认值处理");
  }

  return {
    serverUrl:
      str(process.env.ZIPPER_DOUYIN_WEB_SERVER_URL) ??
      str(fileConfig.serverUrl) ??
      DEFAULT_SERVER_URL,
    token: str(process.env.ZIPPER_DOUYIN_WEB_TOKEN) ?? str(fileConfig.token) ?? null,
    profileDir:
      str(process.env.ZIPPER_DOUYIN_WEB_PROFILE) ??
      str(fileConfig.profileDir) ??
      DEFAULT_PROFILE_DIR,
    workDir: str(fileConfig.workDir) ?? DEFAULT_WORK_DIR,
    headless: fileConfig.headless === undefined ? true : fileConfig.headless !== false,
    recycleHours: positiveInt(fileConfig.recycleHours, DEFAULT_RECYCLE_HOURS),
    maxJsHeapMb: positiveInt(fileConfig.maxJsHeapMb, DEFAULT_MAX_JS_HEAP_MB),
    maxTextLength: positiveInt(fileConfig.maxTextLength, DEFAULT_MAX_TEXT_LENGTH),
    heartbeatIntervalMs: positiveInt(fileConfig.heartbeatIntervalMs, DEFAULT_HEARTBEAT_MS),
    deadAfterMs: positiveInt(fileConfig.deadAfterMs, DEFAULT_DEAD_AFTER_MS),
    logLevel: parseLevel(process.env.ZIPPER_DOUYIN_WEB_LOG_LEVEL ?? fileConfig.logLevel),
    configPath,
  };
}

function positiveInt(value: unknown, fallback: number): number {
  const parsed = num(value);
  return parsed !== undefined && parsed > 0 ? Math.floor(parsed) : fallback;
}

// ═══════════════════════════ 日志 ═══════════════════════════

type Level = SidecarConfig["logLevel"];

const LEVEL_ORDER: Record<Level, number> = { debug: 10, info: 20, warn: 30, error: 40 };

class Logger implements BrowserLogger {
  private level: Level = "info";

  setLevel(level: Level): void {
    this.level = level;
  }

  debug(message: string): void {
    this.write("debug", message);
  }

  info(message: string): void {
    this.write("info", message);
  }

  warn(message: string): void {
    this.write("warn", message);
  }

  error(message: string): void {
    this.write("error", message);
  }

  private write(level: Level, message: string): void {
    if (LEVEL_ORDER[level] < LEVEL_ORDER[this.level]) {
      return;
    }

    const line = `${new Date().toISOString()} [${level.toUpperCase()}] ${message}`;
    if (level === "error") {
      process.stderr.write(`${line}\n`);
      return;
    }

    process.stdout.write(`${line}\n`);
  }
}

function parseLevel(value: unknown): Level {
  const raw = str(value)?.toLowerCase();
  return raw === "debug" || raw === "warn" || raw === "error" ? raw : "info";
}

// ═══════════════════════════ 连接（WS 反连） ═══════════════════════════

interface SocketLike {
  readyState: number;
  send(data: string): void;
  close(code?: number, reason?: string): void;
  addEventListener(type: string, listener: (event: unknown) => void): void;
}

interface LinkOptions {
  url: string;
  token: string | null;
  buildHello: () => Frame;
  heartbeatIntervalMs: number;
  deadAfterMs: number;
  log: Logger;
  onFrame: (frame: Frame) => void;
  onConnected: () => void;
}

/** `/ws/channel` 的客户端：连接 / 退避重连 / 心跳（与既有 sidecar `ChannelLink` 同构）。 */
class ChannelLink {
  private readonly options: LinkOptions;
  private socket: SocketLike | null = null;
  private stopped = false;
  private attempt = 0;
  private heartbeatTimer: ReturnType<typeof setInterval> | null = null;
  private lastInboundAt = 0;

  /**
   * 当前这次连接的「收尾函数」。
   *
   * 为什么需要它：服务端拒绝（驱动不符）后**直接 CloseAsync**，实测这条连接
   * 既不发 `close` 也不发 `error`（2026-11-20 真机：socket readyState 已经不是 OPEN，
   * 但 connectOnce 的 Promise 一直不 settle → 重连循环卡死，永远不再重连）。
   * 心跳里发现「不是 OPEN」就手动收尾，把这个 Promise 解开。
   */
  private finishCurrent: ((error?: unknown) => void) | null = null;

  constructor(options: LinkOptions) {
    this.options = options;
  }

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
        this.attempt = Math.max(this.attempt, 1);
      } catch (error) {
        this.attempt += 1;
        this.options.log.warn(
          `连接 /ws/channel 失败（第 ${this.attempt} 次）：${error instanceof Error ? error.message : String(error)}`,
        );
      }
    }
  }

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
    try {
      socket?.close(1000, "sidecar shutdown");
    } catch {
      // 进程要退了，关不上也无所谓
    }
  }

  private backoffMs(attempt: number): number {
    const base = Math.min(RECONNECT_MAX_MS, RECONNECT_INITIAL_MS * 2 ** (attempt - 1));
    return Math.round(base * (0.8 + Math.random() * 0.4));
  }

  private connectOnce(): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      const url = this.options.token
        ? `${this.options.url}${this.options.url.includes("?") ? "&" : "?"}token=${encodeURIComponent(this.options.token)}`
        : this.options.url;

      let settled = false;
      const finish = (error?: unknown): void => {
        if (settled) {
          return;
        }

        settled = true;
        this.finishCurrent = null;
        this.stopHeartbeat();
        if (this.socket === socket) {
          this.socket = null;
        }

        if (error === undefined) {
          resolve();
          return;
        }

        reject(error instanceof Error ? error : new Error(String(error)));
      };

      this.options.log.info(`正在连接 ${url.replace(/token=[^&]+/, "token=***")}`);

      let socket: SocketLike;
      try {
        socket = new WebSocket(url) as unknown as SocketLike;
      } catch (error) {
        reject(new Error(`创建 WebSocket 失败：${error instanceof Error ? error.message : String(error)}`));
        return;
      }

      this.socket = socket;
      this.finishCurrent = finish;

      socket.addEventListener("open", () => {
        this.attempt = 0;
        this.lastInboundAt = Date.now();
        this.startHeartbeat(socket);

        try {
          socket.send(JSON.stringify(this.options.buildHello()));
          this.options.log.info("已连上 Server，hello 已发送");
          this.options.onConnected();
        } catch (error) {
          this.options.log.error(`发送 hello 失败：${error instanceof Error ? error.message : String(error)}`);
          finish(error);
        }
      });

      socket.addEventListener("message", (event) => {
        this.lastInboundAt = Date.now();
        const raw = (event as { data?: unknown }).data;
        if (typeof raw !== "string") {
          this.options.log.warn("收到非文本帧（已忽略）");
          return;
        }

        const parsed = parseFrame(raw);
        if (!parsed) {
          this.options.log.warn(`收到无法解析的帧（已忽略）：${raw.slice(0, 160)}`);
          return;
        }

        try {
          this.options.onFrame(parsed);
        } catch (error) {
          this.options.log.error(
            `处理 Server 帧失败（type=${parsed.type}）：${error instanceof Error ? error.message : String(error)}`,
          );
        }
      });

      socket.addEventListener("error", (event) => {
        const message = (event as { message?: string }).message ?? "未知错误";
        this.options.log.warn(`WebSocket 出错：${message}`);
        // Node 的 WS 有时只发 error 不发 close：不 finish 的话这次 await 永不返回
        finish(new Error(message));
      });

      socket.addEventListener("close", (event) => {
        const code = (event as { code?: number }).code;
        this.options.log.warn(`与 Server 的连接已断开（code=${code ?? "?"}）`);
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

      // 连接已经不是 OPEN（服务端关了 / 半死）：主动收尾，别让重连循环卡在一个永不 settle 的 Promise 上
      if (socket.readyState !== SOCKET_OPEN) {
        this.options.log.warn(`连接状态为 ${socket.readyState}（非 OPEN），强制重连`);
        this.options.log.debug("调用 finishCurrent 收尾");
        this.finishCurrent?.(new Error("socket not open"));
        return;
      }

      // 半死连接（拔网线 / NAT 超时）不会触发 close：靠"多久没收到帧"判死
      if (Date.now() - this.lastInboundAt > this.options.deadAfterMs) {
        this.options.log.warn(`${this.options.deadAfterMs}ms 没收到任何帧，判定连接已死，主动断开重连`);
        this.closeSocket(socket);
        return;
      }

      try {
        socket.send(JSON.stringify({ type: "ping", time: Date.now() }));
      } catch (error) {
        this.options.log.warn(`心跳发送失败：${error instanceof Error ? error.message : String(error)}`);
        this.closeSocket(socket);
      }
    }, this.options.heartbeatIntervalMs);
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer) {
      clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = null;
    }
  }

  private closeSocket(socket: SocketLike): void {
    try {
      socket.close(4000, "heartbeat timeout");
    } catch {
      // 忽略
    }
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise<void>((resolve) => {
    setTimeout(resolve, ms);
  });
}

// ═══════════════════════════ 入口 ═══════════════════════════

async function main(): Promise<void> {
  const log = new Logger();
  const config = await loadConfig(log);
  log.setLevel(config.logLevel);

  log.info(
    `抖音 Web sidecar 启动：server=${config.serverUrl} profile=${config.profileDir} ` +
      `headless=${config.headless} token=${config.token ? "已配置（不外显）" : "未配置（loopback 免验）"} node=${process.version}`,
  );

  const names = new ConversationNameCache();
  let selfUid: string | null = null;
  let selfUidProbeAt = 0;
  let link: ChannelLink | null = null;

  /** 拿自己的 uid；没有就顺手起一次探测（每 60 秒最多一次，别把页面打爆）。 */
  const currentSelfUid = (): string | null => {
    if (selfUid) {
      return selfUid;
    }

    if (Date.now() - selfUidProbeAt > 60_000) {
      selfUidProbeAt = Date.now();
      void browser
        .selfUid()
        .then((value) => {
          if (value) {
            selfUid = value;
            log.info(`自身 uid 补探测成功：${value}`);
          }
        })
        .catch(() => undefined);
    }

    return null;
  };

  // 发帧失败计数：偶发一次是断线重连的正常噪声，连着失败就是"通道不可用"（要响亮）
  let sendFailures = 0;
  const send = (outFrame: Frame): void => {
    try {
      link?.send(outFrame);
      sendFailures = 0;
    } catch (error) {
      sendFailures += 1;
      const message = error instanceof Error ? error.message : String(error);
      if (sendFailures >= 3) {
        log.warn(`帧发送连续失败 ${sendFailures} 次（type=${outFrame.type}）：${message}`);
      } else {
        log.debug(`帧发送失败（连接可能已断，type=${outFrame.type}）：${message}`);
      }
    }
  };

  // ── 浏览器 ──
  const browser = new DouyinBrowser({
    options: {
      profileDir: config.profileDir,
      headless: config.headless,
      viewport: { width: 1400, height: 950 },
      recycleHours: config.recycleHours,
      maxJsHeapMb: config.maxJsHeapMb,
      userAgent:
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/159.0.0.0 Safari/537.36",
    },
    log,
    onState: (state: BrowserStateSnapshot) => {
      send(makeFrame("browser.state", { ...state }));
    },
  });

  // ── 登录 ──
  const login = new LoginFlow({
    browser,
    log,
    emit: send,
    onOnline: (uid) => {
      selfUid = uid;
      log.info(`自身 uid 已确认：${uid ?? "(未探测到)"}`);

      // 登录是在首页扫码完成的：不回到 /chat 的话入站 WS 根本没挂上
      // （页面不在私信页 → 没有 frontier-im 连接 → 一条消息也收不到）
      void browser.ensureChatPage(SITE.chatUrl).catch((error: unknown) => {
        log.warn(`登录后打开私信页失败：${error instanceof Error ? error.message : String(error)}`);
      });
    },
  });

  // ── 入站 ──
  const protoPath = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "proto", "im.proto");
  const inbound = new InboundListener({
    log,
    protoPath,
    emit: send,
    names,
    selfUid: currentSelfUid,
    onSelfLearned: (learned) => {
      // 探测值（如 self-profile 的短 uid）与 IM uid 不一致时，以会话反推的为准
      if (selfUid !== learned) {
        log.warn(`自身 uid 以会话反推值为准：${learned}（原探测值 ${selfUid ?? "无"}）`);
        selfUid = learned;

        // 补发一次 hello：服务端只在 hello 里读 selfId，不补的话状态页一直显示错的那个
        send(
          makeFrame("hello", {
            version: PROTOCOL_VERSION,
            platform: PLATFORM,
            driver: DRIVER,
            selfId: selfUid,
            capabilities: [...CAPABILITIES],
          }),
        );
      }
    },
  });

  browser.onPage((page) => {
    void inbound.attach(page);
  });

  // ── 出站 ──
  const outbound = new OutboundSender({
    browser,
    log,
    names,
    workDir: config.workDir,
  });

  // ── 帧分发 ──
  const handleServerFrame = (inFrame: Frame): void => {
    switch (inFrame.type) {
      case "login.start":
        void login.startLogin().catch((error: unknown) => {
          log.error(`发起登录失败：${error instanceof Error ? error.message : String(error)}`);
        });
        return;

      case "login.logout":
        void login.logout().catch((error: unknown) => {
          log.error(`退出登录失败：${error instanceof Error ? error.message : String(error)}`);
        });
        return;

      case "login.challenge.answer": {
        const kind = str(inFrame.data?.kind);
        const value = str(inFrame.data?.value);
        if ((kind !== "sms" && kind !== "slider") || !value) {
          log.warn(`login.challenge.answer 帧不合法（kind=${kind ?? "-"} value=${value ?? "-"}），已忽略`);
          return;
        }

        void login.submitAnswer(kind, value).catch((error: unknown) => {
          log.warn(`回填挑战失败：${error instanceof Error ? error.message : String(error)}`);
        });
        return;
      }

      case "outbound":
        void handleOutbound(inFrame);
        return;

      case "query.contacts":
        void handleContacts(inFrame);
        return;

      case "query.groups":
        // 网页版私信页没有"群列表"入口（当前账号群列表本来就是 0 个）；
        // 明确回空 + 说明，不假装查到了（与既有 sidecar「取不到不阻塞」同口径）
        reply(inFrame, { ok: true, error: null, groups: [] });
        return;

      case "ping":
        send(makeFrame("pong", {}));
        return;

      case "heartbeat":
      case "pong":
        return;

      default:
        log.warn(`未知帧类型「${inFrame.type}」（已忽略）`);
    }
  };

  const reply = (inFrame: Frame, data: Record<string, unknown>): void => {
    const requestId = str(inFrame.data?.requestId);
    if (!requestId) {
      log.warn(`${inFrame.type} 缺少 requestId，结果无法配对（已忽略）`);
      return;
    }

    send(makeFrame("result", { requestId, ...data }));
  };

  const handleOutbound = async (inFrame: Frame): Promise<void> => {
    const requestId = str(inFrame.data?.requestId);
    if (!requestId) {
      log.warn("outbound 帧缺少 requestId：仍会尝试发送，但无法回 ack");
    }

    try {
      const plan = buildOutboundPlan(inFrame, { maxTextLength: config.maxTextLength });
      const result = await outbound.send(plan);

      log.info(
        `出站成功：session=${plan.session} 文本=${result.chunks} 图片=${result.images} ` +
          `降级=${result.degraded.join(",") || "无"} requestId=${requestId ?? "(无)"}`,
      );

      if (requestId) {
        send(
          makeFrame("ack", {
            requestId,
            ok: true,
            error: null,
            degraded: result.degraded.length > 0 ? result.degraded.join(",") : null,
          }),
        );
      }
    } catch (error) {
      const failure = classifyFailure(error);
      log.warn(`出站失败：requestId=${requestId ?? "(无)"} code=${failure.code} 原因=${failure.message}`);

      // 页面崩了：重建一次，下次发送就恢复正常（不在这里重试本条，避免重复发送）
      if (failure.code === "page") {
        void browser.recycle("出站遇到页面不可用", SITE.chatUrl).catch(() => undefined);
      }

      if (!requestId) {
        return;
      }

      send(makeFrame("ack", { requestId, ok: false, error: failure.message, code: failure.code }));
    }
  };

  const handleContacts = async (inFrame: Frame): Promise<void> => {
    try {
      const page = await browser.ensureChatPage(SITE.chatUrl);
      await openChatPage(page).catch((error: unknown) => {
        log.warn(`刷新私信页失败（继续用当前页）：${error instanceof Error ? error.message : String(error)}`);
      });

      const health = await pageHealth(page);
      const rows = await listConversations(page);
      log.info(
        `query.contacts 扫描：url=${health.url} rows=${rows.length} ` +
          `names=${rows.map((row) => row.name).join("|") || "(空)"} 输入框=${health.hasInput}`,
      );
      names.refreshFromRows(rows);

      const contacts = rows
        .filter((row) => row.ids.length > 0 && !row.name.includes("系统"))
        .map((row) => ({ userId: row.ids[row.ids.length - 1], nickname: row.name, remark: null }));

      log.info(`query.contacts 返回 ${contacts.length} 个会话候选（来源=私信列表）`);
      reply(inFrame, { ok: true, error: null, contacts });
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      log.warn(`query.contacts 失败：${message}`);
      reply(inFrame, { ok: false, error: message, contacts: [] });
    }
  };

  // ── 连接 ──
  link = new ChannelLink({
    url: config.serverUrl,
    token: config.token,
    heartbeatIntervalMs: config.heartbeatIntervalMs,
    deadAfterMs: config.deadAfterMs,
    log,
    buildHello: () =>
      makeFrame("hello", {
        version: PROTOCOL_VERSION,
        platform: PLATFORM,
        driver: DRIVER,
        selfId: selfUid,
        capabilities: [...CAPABILITIES],
      }),
    onFrame: handleServerFrame,
    onConnected: () => {
      // 登录事件往往早于连接建立：连上就补报一次，否则服务端一直显示 unknown
      login.emitState();
      send(makeFrame("browser.state", { ...browser.current }));
    },
  });

  // ── 定时体检：刷新会话名（入站补昵称用）+ 播状态 + DOM 兜底 ──
  const healthTimer = setInterval(() => {
    void (async () => {
      try {
        if (!browser.isStarted) {
          return;
        }

        // 崩了没人管就一直哑着：体检里顺手自愈（不依赖"下一条消息发失败"来触发）
        if (browser.current.page === "crashed") {
          log.warn("浏览器处于崩溃状态，自动重建");
          await browser.recycle("体检发现页面崩溃", SITE.chatUrl).catch(() => undefined);
        }

        const memoryMb = await browser.heapMb();
        send(makeFrame("browser.state", { ...browser.current, memoryMb, lastAction: "体检" }));

        const page = browser.page;
        const health = await pageHealth(page);
        if (!health.url.includes("/chat")) {
          return;
        }

        const rows = await listConversations(page).catch(() => []);
        if (rows.length > 0) {
          names.refreshFromRows(rows);
        }

        await inbound.scanDom(page);
      } catch (error) {
        log.debug(`体检失败（忽略）：${error instanceof Error ? error.message : String(error)}`);
      }
    })();
  }, HEALTH_INTERVAL_MS);

  const shutdown = (signal: string): void => {
    log.info(`收到 ${signal}，正在退出`);
    clearInterval(healthTimer);
    link?.stop();
    void browser.stop().finally(() => {
      log.info("抖音 Web sidecar 已退出");
      process.exit(0);
    });
  };

  process.once("SIGINT", () => shutdown("SIGINT"));
  process.once("SIGTERM", () => shutdown("SIGTERM"));
  process.on("unhandledRejection", (reason) => {
    log.error(`未处理的 Promise 拒绝：${reason instanceof Error ? reason.message : String(reason)}`);
  });

  // 先起浏览器（登录/收发都要它），再连 Server（Server 没起来也不致命，会一直退避重连）
  try {
    await browser.start();
  } catch (error) {
    log.error(`浏览器启动失败：${error instanceof Error ? error.message : String(error)}`);
  }

  // 已有 profile（登录态还在）就顺手把 selfId 探出来
  if (await browser.isLoggedIn().catch(() => false)) {
    selfUid = await browser.selfUid().catch(() => null);
    log.info(`检测到已有登录态：selfId=${selfUid ?? "(未探测到)"}`);

    // 走 startLogin：它发现已登录 → markOnline（上报 login.state=online）+ 打开 /chat。
    // 之前这里只 emitState()，状态还是 unknown —— 服务端/客户端一直显示"未知"（2026-11-20 真机发现）。
    void login.startLogin().catch((error: unknown) => {
      log.warn(`补报登录态失败：${error instanceof Error ? error.message : String(error)}`);
    });
  }

  await link.start();
}

main().catch((error: unknown) => {
  process.stderr.write(`[FATAL] 抖音 Web sidecar 启动失败：${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});
