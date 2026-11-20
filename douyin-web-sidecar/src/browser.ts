/**
 * Chromium 生命周期（**唯一**碰 Playwright 启动 / 关闭的地方）。
 *
 * 职责：
 *   ① 持久 profile 的启动与关闭（登录态就在 profile 里，不开新窗口就不能丢）
 *   ② 登录态判定（cookie 白名单）与自身 uid 探测
 *   ③ 二维码截图（复用 Server 已有的 `login.qr` 帧，客户端零改动）
 *   ④ 常驻回收（每 `recycleHours` 或渲染堆超阈值重建 context）与 crash 自愈
 *   ⑤ WebSocket 监听挂载点（入站旁听由 `inbound.ts` 注册）
 *
 * 为什么回收而不是重启进程：Server 侧只认 `/ws/channel` 连接，
 * 浏览器重建不影响那条连接 —— 重建期间 ack 会失败但不会把通道也带走（实施方案 T1.7）。
 */

import { chromium, type BrowserContext, type Page } from "playwright";
import { SITE } from "./site.js";
import type { QrBox } from "./site.js";

/** 浏览器运行参数（来自 sidecar `config.json` 的 `douyin.*`）。 */
export interface BrowserOptions {
  /** Chromium 持久 profile 目录（登录态的唯一载体，**必须备份、绝不入库**）。 */
  profileDir: string;
  /** 无头模式。计划任务以 SYSTEM 身份运行，没有交互桌面 → 只能 true。 */
  headless: boolean;
  /** 视口（固定值：布局相关选择器依赖它，见 `site.ts`）。 */
  viewport: { width: number; height: number };
  /** 多久回收一次 context（小时）。0 = 不按时间回收。 */
  recycleHours: number;
  /**
   * **渲染进程 JS 堆**超过这个值就提前回收（MB）。0 = 不按内存回收。
   *
   * ⚠️ 这是 `Performance.getMetrics → JSHeapUsedSize`，**不是**进程总内存
   * （PoC 测的「单实例 ≈800 MB」是 5 个子进程的 RSS 之和）。
   * 两者量级不同，所以阈值不能照抄 800 —— 实测一个私信页的 JS 堆通常 100~300 MB。
   * 主守卫是 `recycleHours`（6h 定时），这个只是"某天页面吃爆了"的兜底。
   */
  maxJsHeapMb: number;
  /** 用户代理（默认伪装成常规桌面 Chrome；网页版对非常规 UA 更敏感）。 */
  userAgent: string;
}

/** 浏览器对外播报的状态（进 `browser.state` 帧）。 */
export interface BrowserStateSnapshot {
  /** `ok` 可用 / `recycling` 正在重建 / `crashed` 崩了 / `closed` 已关。 */
  page: "ok" | "recycling" | "crashed" | "closed" | "unknown";
  memoryMb: number;
  lastAction: string;
  /** 上次回收 / 崩溃的原因（排查用）。 */
  note?: string;
}

/** 日志口子（与既有 sidecar 同风格：`info/warn/debug/error`）。 */
export interface BrowserLogger {
  info(message: string): void;
  warn(message: string): void;
  debug(message: string): void;
  error(message: string): void;
}

/** 登录态 cookie（**只用这三个判**：`passport_csrf_token` 匿名也有，判它会误判已登录）。 */
export const SESSION_COOKIES = ["sessionid", "sessionid_ss", "sid_tt"] as const;

/** 浏览器封装的构造参数。 */
export interface DouyinBrowserOptions {
  options: BrowserOptions;
  log: BrowserLogger;
  /** 状态变化回调（`browser.state` 帧的来源）。 */
  onState: (state: BrowserStateSnapshot) => void;
}

/** Chromium 生命周期管理器。 */
export class DouyinBrowser {
  private readonly options: BrowserOptions;
  private readonly log: BrowserLogger;
  private readonly onState: (state: BrowserStateSnapshot) => void;

  private context: BrowserContext | null = null;
  private chatPage: Page | null = null;
  private startedAt = 0;
  private state: BrowserStateSnapshot = { page: "unknown", memoryMb: 0, lastAction: "启动中" };

  /** 页面创建回调（入站监听要往每个新页面上挂 WebSocket 钩子）。 */
  private pageHooks: ((page: Page) => void)[] = [];

  private recycleTimer: ReturnType<typeof setInterval> | null = null;

  constructor(params: DouyinBrowserOptions) {
    this.options = params.options;
    this.log = params.log;
    this.onState = params.onState;
  }

  /** 当前私信页；没有就抛错（调用方会转成 `page` 类失败）。 */
  get page(): Page {
    if (!this.chatPage || this.chatPage.isClosed()) {
      throw new Error("浏览器页面不可用（尚未启动或已崩溃）");
    }

    return this.chatPage;
  }

  /** 是否已启动。 */
  get isStarted(): boolean {
    return this.context !== null;
  }

  /** 当前状态快照。 */
  get current(): BrowserStateSnapshot {
    return this.state;
  }

  /** 注册「新页面创建」钩子（在每次启动 / 重建后依然有效）。 */
  onPage(handler: (page: Page) => void): void {
    this.pageHooks.push(handler);
  }

  /** 启动（幂等：已启动则直接返回）。 */
  async start(): Promise<void> {
    if (this.context) {
      return;
    }

    this.log.info(`启动 Chromium（profile=${this.options.profileDir} headless=${this.options.headless}）`);

    this.context = await chromium.launchPersistentContext(this.options.profileDir, {
      headless: this.options.headless,
      viewport: this.options.viewport,
      locale: "zh-CN",
      userAgent: this.options.userAgent,
      // 关掉最明显的自动化特征（不追求"完美隐身"，只是别一眼被认出来）
      args: ["--disable-blink-features=AutomationControlled", "--no-first-run"],
      timeout: 60_000,
    });

    this.context.on("close", () => {
      // 主动 stop() 时也会走到这里：只在 unexpected 时标 crashed
      if (this.context) {
        this.log.warn("浏览器 context 意外关闭");
        this.context = null;
        this.chatPage = null;
        this.update({ page: "crashed", lastAction: "context 关闭", note: "context 意外关闭" });
      }
    });

    const page = this.context.pages()[0] ?? (await this.context.newPage());
    this.attachPage(page);
    this.chatPage = page;
    this.startedAt = Date.now();

    this.update({ page: "ok", lastAction: "已启动" });
    this.startRecycleTimer();
  }

  /** 打开（或复用）私信页。 */
  async ensureChatPage(url: string): Promise<Page> {
    await this.start();

    const page = this.page;
    if (!page.url().includes("/chat")) {
      await page.goto(url, { waitUntil: "domcontentloaded", timeout: 60_000 });
      // 等"能用的证据"而不是死等 5 秒：输入框 / 二维码画布 / 首屏内容任一出现即算就绪。
      // 固定 sleep 会把 A-1 的「2 秒出码」直接顶掉（审查 P1-7）。
      await page
        .waitForFunction(
          () =>
            document.readyState === "complete" &&
            (Boolean(document.querySelector('textarea, [contenteditable="true"]')) ||
              Boolean(document.querySelector("canvas, img"))),
          { timeout: 20_000 },
        )
        .catch(() => undefined);
    }

    this.update({ page: "ok", lastAction: "打开私信页" });
    return page;
  }

  /** 重建 context（profile 保留，登录态不丢）。 */
  async recycle(reason: string, url: string): Promise<void> {
    if (this.state.page === "recycling") {
      return; // 并发触发只做一次
    }

    this.log.warn(`回收浏览器：${reason}`);
    this.update({ page: "recycling", lastAction: "回收重建", note: reason });

    await this.closeContext();

    try {
      await this.ensureChatPage(url);
      this.log.info("浏览器已重建");
      this.update({ page: "ok", lastAction: "已重建", note: reason });
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      this.log.error(`浏览器重建失败：${message}`);
      this.update({ page: "crashed", lastAction: "重建失败", note: message });
      throw error;
    }
  }

  /** 抓登录二维码（返回 PNG 字节；找不到返回 `null`，不抛 —— 二维码还没渲染是常态）。 */
  async captureQrPng(box: QrBox): Promise<Buffer | null> {
    const page = this.page;
    try {
      return await page.screenshot({
        clip: {
          x: Math.max(0, box.x - 6),
          y: Math.max(0, box.y - 6),
          width: box.width + 12,
          height: box.height + 12,
        },
        type: "png",
      });
    } catch (error) {
      this.log.debug(`二维码截图失败：${error instanceof Error ? error.message : String(error)}`);
      return null;
    }
  }

  /** 当前已有的登录 cookie 名（空数组 = 未登录）。 */
  async sessionCookieNames(): Promise<string[]> {
    if (!this.context) {
      return [];
    }

    const cookies = await this.context.cookies("https://www.douyin.com");
    return cookies
      .filter(
        (cookie) =>
          (SESSION_COOKIES as readonly string[]).includes(cookie.name) &&
          Boolean(cookie.value) &&
          cookie.value.length > 10,
      )
      .map((cookie) => cookie.name);
  }

  /** 是否已登录。 */
  async isLoggedIn(): Promise<boolean> {
    return (await this.sessionCookieNames()).length > 0;
  }

  /**
   * 探测自己的 uid。
   *
   * 两条路：① 页面上下文直接 fetch `/aweme/v1/web/user/profile/self/`（带着登录 cookie）；
   * ② 退到 cookie（`uid_tt` / `uid_tt_ss` 里就是登录用户的数字 uid）。
   * 都拿不到返回 `null` —— 服务端会保留 `unknown`，**不编一个假 uid**。
   */
  async selfUid(): Promise<string | null> {
    if (!this.context) {
      return null;
    }

    try {
      const fromApi = await this.page.evaluate(async () => {
        try {
          const response = await fetch(`${location.origin}/aweme/v1/web/user/profile/self/`, {
            credentials: "include",
          });
          const text = await response.text();
          const data = JSON.parse(text) as { user?: { uid?: string }; user_uid?: string };
          return data.user?.uid ?? data.user_uid ?? null;
        } catch {
          return null;
        }
      });

      if (fromApi) {
        return String(fromApi);
      }
    } catch (error) {
      this.log.debug(`探测 selfUid 的接口调用失败：${error instanceof Error ? error.message : String(error)}`);
    }

    const cookies = await this.context.cookies("https://www.douyin.com");
    for (const name of ["uid_tt", "uid_tt_ss"]) {
      const hit = cookies.find((cookie) => cookie.name === name && cookie.value);
      if (hit) {
        const digits = hit.value.match(/\d{6,}/);
        if (digits) {
          return digits[0] ?? null;
        }
      }
    }

    return null;
  }

  /** 渲染堆占用（MB）。拿不到返回 0（调用方按"未知"处理，不据此回收）。 */
  async heapMb(): Promise<number> {
    const page = this.chatPage;
    if (!page || page.isClosed()) {
      return 0;
    }

    try {
      const session = await page.context().newCDPSession(page);
      const metrics = (await session.send("Performance.getMetrics")) as {
        metrics: { name: string; value: number }[];
      };
      await session.detach();

      const used = metrics.metrics.find((metric) => metric.name === "JSHeapUsedSize")?.value ?? 0;
      return Math.round(used / 1024 / 1024);
    } catch {
      return 0;
    }
  }

  /** 停止（关进程，不改 profile）。 */
  async stop(): Promise<void> {
    this.stopRecycleTimer();
    await this.closeContext();
    this.update({ page: "closed", lastAction: "已停止" });
  }

  // ─────────────────────────── 内部 ───────────────────────────

  private attachPage(page: Page): void {
    for (const hook of this.pageHooks) {
      try {
        hook(page);
      } catch (error) {
        this.log.warn(`挂载页面钩子失败：${error instanceof Error ? error.message : String(error)}`);
      }
    }

    page.on("crash", () => {
      this.log.error("页面崩溃（crash）");
      this.update({ page: "crashed", lastAction: "页面崩溃", note: "page crash" });
    });

    page.on("close", () => {
      if (this.chatPage === page) {
        this.chatPage = null;
        this.log.warn("私信页被关闭");
        this.update({ page: "crashed", lastAction: "页面被关闭", note: "page closed" });
      }
    });
  }

  private async closeContext(): Promise<void> {
    const context = this.context;
    this.context = null;
    this.chatPage = null;

    if (!context) {
      return;
    }

    try {
      await context.close();
    } catch (error) {
      this.log.debug(`关闭 context 失败（忽略）：${error instanceof Error ? error.message : String(error)}`);
    }
  }

  private startRecycleTimer(): void {
    this.stopRecycleTimer();

    const hours = this.options.recycleHours;
    const maxHeap = this.options.maxJsHeapMb;
    if (hours <= 0 && maxHeap <= 0) {
      return;
    }

    // 每 15 分钟检查一次：时间到 / 内存超阈值就重建 context
    this.recycleTimer = setInterval(() => {
      void this.checkRecycle();
    }, 15 * 60 * 1000);
  }

  private stopRecycleTimer(): void {
    if (this.recycleTimer) {
      clearInterval(this.recycleTimer);
      this.recycleTimer = null;
    }
  }

  private async checkRecycle(): Promise<void> {
    if (!this.context || this.state.page === "recycling") {
      return;
    }

    const hours = this.options.recycleHours;
    const maxHeap = this.options.maxJsHeapMb;
    const ageHours = (Date.now() - this.startedAt) / 3_600_000;
    const heap = maxHeap > 0 ? await this.heapMb() : 0;

    this.update({ memoryMb: heap });

    if (hours > 0 && ageHours >= hours) {
      await this.recycle(`运行 ${ageHours.toFixed(1)}h 达到 ${hours}h`, SITE.chatUrl).catch(
        () => undefined,
      );
      return;
    }

    if (maxHeap > 0 && heap >= maxHeap) {
      await this.recycle(`渲染堆 ${heap}MB 达到 ${maxHeap}MB`, SITE.chatUrl).catch(
        () => undefined,
      );
    }
  }

  private update(patch: Partial<BrowserStateSnapshot>): void {
    this.state = { ...this.state, ...patch };
    try {
      this.onState(this.state);
    } catch (error) {
      this.log.debug(`播报 browser.state 失败：${error instanceof Error ? error.message : String(error)}`);
    }
  }
}
