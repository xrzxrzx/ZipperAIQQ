/**
 * 登录编排（二维码 / 短信 / 滑块）。
 *
 * 与既有 `douyin-sidecar` 的差异：那边登录由 `douyin-im` 内部完成，这里**全是页面动作**：
 *   ① 打开首页 → 找「扫码登录」弹窗里的二维码元素 → 截图 → `login.qr`（**复用既有帧，客户端零改动**）
 *   ② 每 `QR_REFRESH_MS` 复核一次：内容变了才重发（客户端也有"内容不变不解码"的判据）
 *   ③ 短信 / 滑块：检测到就发 `login.challenge`，答案由服务端转 `login.challenge.answer` 回来
 *   ④ 登录成功判据：**只认 `sessionid` / `sessionid_ss` / `sid_tt`**
 *      （`passport_csrf_token` 匿名也有，用它判会把未登录误判成已登录 —— PoC 踩过）
 *
 * 这一层不碰 WebSocket、不碰 Server：所有输出都走注入的 `emit`，便于单测替身。
 */

import type { DouyinBrowser } from "./browser.js";
import type { BrowserLogger } from "./browser.js";
import { createHash } from "node:crypto";
import {
  detectChallenge,
  dragSlider,
  findQrBox,
  findSmsInput,
  openLoginDialog,
  readPageNickname,
  SITE,
  type ChallengeDetection,
} from "./site.js";
import { frame, type Frame } from "./protocol.js";

/** 登录状态（字符串直接进 `login.state` 帧，与 Server 侧 `DouyinLoginState` 对齐）。 */
export type LoginState = "online" | "waitingQr" | "loggedOut" | "error" | "unknown";

/** 二维码刷新间隔（8 秒：官方二维码约 60 秒过期，刷新频率够密又不会把页面刷爆）。 */
const QR_REFRESH_MS = 8_000;

/** 单次登录会话的最长等待（10 分钟；超过就报错交回人工）。 */
const LOGIN_TIMEOUT_MS = 10 * 60 * 1000;

export interface LoginFlowOptions {
  browser: DouyinBrowser;
  log: BrowserLogger;
  /** 发一帧给 Server（`login.qr` / `login.state` / `login.challenge`）。 */
  emit: (frame: Frame) => void;
  /** 登录成功后的回调（main 用它把 selfId 记下来，供入站 `resolveThreadId` 用）。 */
  onOnline: (selfId: string | null, nickname: string | null) => void;
}

/** 登录编排器。 */
export class LoginFlow {
  private readonly browser: DouyinBrowser;
  private readonly log: BrowserLogger;
  private readonly emit: (frame: Frame) => void;
  private readonly onOnline: (selfId: string | null, nickname: string | null) => void;

  private state: LoginState = "unknown";
  private message: string | null = null;
  private selfId: string | null = null;
  private nickname: string | null = null;

  /** 正在跑的那次登录（用于取消 / 幂等）。 */
  private running: Promise<void> | null = null;
  private cancel: AbortController | null = null;

  /** 等待用户回填的挑战答案。 */
  private pendingAnswer:
    | { kind: "sms" | "slider"; resolve: (value: string) => void; reject: (err: Error) => void }
    | null = null;

  /** 上一次发给服务端的二维码内容哈希（内容变才重发；比字节长度可靠）。 */
  private lastQrHash = "";

  constructor(options: LoginFlowOptions) {
    this.browser = options.browser;
    this.log = options.log;
    this.emit = options.emit;
    this.onOnline = options.onOnline;
  }

  /** 当前登录态快照（`getDouyinLogin` 里的值来源之一）。 */
  get snapshot(): { state: LoginState; selfId: string | null; nickname: string | null; message: string | null } {
    return { state: this.state, selfId: this.selfId, nickname: this.nickname, message: this.message };
  }

  /** 连上 Server 后补报一次当前状态（登录事件往往早于连接建立）。 */
  emitState(): void {
    this.emit(
      frame("login.state", {
        state: this.state,
        selfId: this.selfId,
        nickname: this.nickname,
        message: this.message,
      }),
    );
  }

  /**
   * 发起登录（客户端点「登录」时走这里）。
   *
   * 幂等：已经在等扫码就直接重发当前状态，不叠第二条循环。
   * 已登录则只补报 `online`，**不会**把用户踢下线。
   */
  async startLogin(): Promise<void> {
    try {
      await this.browser.start();

      if (await this.browser.isLoggedIn()) {
        await this.markOnline();
        return;
      }
    } catch (error) {
      // 浏览器起不来 = 用户永远等不到二维码：**必须**把错误报回去，
      // 否则服务端一直显示「等待扫码」，界面上一条信息都没有。
      const message = error instanceof Error ? error.message : String(error);
      this.log.error(`发起登录失败（浏览器启动阶段）：${message}`);
      this.setState("error", `浏览器底座不可用：${message}`);
      return;
    }

    if (this.running) {
      this.log.info("登录流程已在跑，重发当前状态");
      this.emitState();
      return;
    }

    this.setState("waitingQr", null);
    this.cancel = new AbortController();
    this.running = this.runLoginLoop(this.cancel.signal).finally(() => {
      this.running = null;
      this.cancel = null;
    });
  }

  /** 退出登录：只清状态，**不删 profile 文件**（下一次登录还是同一套身份文件）。 */
  async logout(): Promise<void> {
    this.cancel?.abort();
    this.pendingAnswer?.reject(new Error("登录已退出"));
    this.pendingAnswer = null;
    this.lastQrHash = "";
    this.selfId = null;
    this.nickname = null;
    this.setState("loggedOut", null);

    const page = this.browser.isStarted ? this.browser.page : null;
    if (page) {
      try {
        // 清 cookie：网页版的「退出登录」入口文案随版本变，直接清 cookie 最稳
        await page.context().clearCookies();
        await page.goto(SITE.homeUrl, { waitUntil: "domcontentloaded", timeout: 30_000 });
      } catch (error) {
        this.log.warn(`退出登录时清理页面失败（忽略）：${error instanceof Error ? error.message : String(error)}`);
      }
    }
  }

  /**
   * 回填挑战答案（`login.challenge.answer` 帧 → 这里）。
   *
   * @throws 没有在等答案时抛错（服务端会把它回填给用户，不静默丢）
   */
  async submitAnswer(kind: "sms" | "slider", value: string): Promise<void> {
    const pending = this.pendingAnswer;
    if (!pending) {
      throw new Error("当前没有等待回填的验证挑战（可能已超时或已自动通过）");
    }

    if (pending.kind !== kind) {
      throw new Error(`挑战类型不匹配：当前在等 ${pending.kind}，收到 ${kind}`);
    }

    pending.resolve(value);
    this.pendingAnswer = null;
  }

  // ─────────────────────────── 内部 ───────────────────────────

  private async runLoginLoop(signal: AbortSignal): Promise<void> {
    const deadline = Date.now() + LOGIN_TIMEOUT_MS;

    try {
      const page = await this.browser.ensureChatPage(SITE.homeUrl);
      let qrMisses = 0;

      while (!signal.aborted && Date.now() < deadline) {
        if (await this.browser.isLoggedIn()) {
          await this.markOnline();
          return;
        }

        const challenge = await detectChallenge(page).catch(() => null as ChallengeDetection);
        if (challenge) {
          await this.handleChallenge(page, challenge, signal);
          continue;
        }

        const box = await findQrBox(page).catch(() => null);
        if (box) {
          const png = await this.browser.captureQrPng(box);
          if (png) {
            const hash = createHash("sha1").update(png).digest("hex");
            if (hash !== this.lastQrHash) {
              this.lastQrHash = hash;
              this.emit(frame("login.qr", { qrcodeBase64: png.toString("base64") }));
              this.log.info(`已推送登录二维码（${png.length} 字节）`);
            }
          }

          this.setState("waitingQr", null);
          qrMisses = 0;
        } else if (qrMisses >= 1) {
          // 连续两轮都没找到二维码 → 首页大概没自动弹登录框，点一次「登录」入口
          // （原实现判 `state === "unknown"`，而进循环时状态已经是 waitingQr，那段是死代码）
          const opened = await openLoginDialog(page);
          this.log.info(opened ? "已点击登录入口，等扫码弹窗" : "没找到登录入口，继续等待弹窗");

          if (!opened) {
            qrMisses = 0; // 别每轮都点一遍，等一个周期再来
          }
        } else {
          qrMisses += 1;
        }

        await sleep(QR_REFRESH_MS, signal);
      }

      if (!signal.aborted) {
        this.setState("error", "登录超时：10 分钟内没扫上码（请重新点「登录」）");
      }
    } catch (error) {
      if (signal.aborted) {
        return;
      }

      const message = error instanceof Error ? error.message : String(error);
      this.log.error(`登录流程失败：${message}`);
      this.setState("error", message);
    }
  }

  private async handleChallenge(
    page: import("playwright").Page,
    challenge: NonNullable<ChallengeDetection>,
    signal: AbortSignal,
  ): Promise<void> {
    if (challenge.kind === "sms") {
      this.log.info("检测到短信验证码输入框，等用户回填");
      const value = await this.askUser("sms", challenge.hint, signal);
      if (value === null) {
        return;
      }

      const box = await findSmsInput(page);
      await box.click({ timeout: 10_000 });
      await page.keyboard.type(value, { delay: 60 });
      await page.waitForTimeout(300);
      await page.keyboard.press("Enter");
      await page.waitForTimeout(2_500);
      return;
    }

    const anchor = challenge.bgBox;
    if (anchor) {
      // 背景图 + 拼图块都给：只有背景图时用户还得自己找缺口在哪
      const bg = await this.browser.captureQrPng(anchor).catch(() => null);
      const piece = challenge.pieceBox
        ? await this.browser.captureQrPng(challenge.pieceBox).catch(() => null)
        : null;

      this.emit(
        frame("login.challenge", {
          kind: "slider",
          hint: challenge.hint,
          bgBase64: bg ? bg.toString("base64") : null,
          pieceBase64: piece ? piece.toString("base64") : null,
        }),
      );
    } else {
      this.emit(frame("login.challenge", { kind: "slider", hint: challenge.hint }));
    }

    const answer = await this.askUser("slider", challenge.hint, signal);
    if (answer === null) {
      return;
    }

    const offset = Number(answer);
    if (!Number.isFinite(offset) || offset <= 0 || offset > 600) {
      throw new Error(`滑块偏移不合法：${answer}（应在 1~600 之间）`);
    }

    await dragSlider(page, offset, anchor);
    await page.waitForTimeout(2_000);
  }

  /** 播 `login.challenge` 并等答案；被取消返回 `null`。 */
  private async askUser(
    kind: "sms" | "slider",
    hint: string,
    signal: AbortSignal,
  ): Promise<string | null> {
    if (kind === "slider") {
      // 滑块已在调用处发过（带图），这里只等答案
    } else {
      this.emit(frame("login.challenge", { kind, hint }));
    }

    return new Promise<string | null>((resolve) => {
      const onAbort = (): void => {
        this.pendingAnswer = null;
        resolve(null);
      };

      signal.addEventListener("abort", onAbort, { once: true });

      this.pendingAnswer = {
        kind,
        resolve: (value) => {
          signal.removeEventListener("abort", onAbort);
          resolve(value);
        },
        reject: () => {
          signal.removeEventListener("abort", onAbort);
          resolve(null);
        },
      };
    });
  }

  private async markOnline(): Promise<void> {
    const selfId = await this.browser.selfUid().catch(() => null);
    const nickname = await this.readNickname().catch(() => null);

    this.selfId = selfId;
    this.nickname = nickname;
    this.setState("online", null);
    this.log.info(`抖音已登录：selfId=${selfId ?? "(未探测到)"} nickname=${nickname ?? "(未知)"}`);

    this.onOnline(selfId, nickname);
  }

  /** 昵称：从页面 meta 读；读不到就 `null`（不编）。 */
  private async readNickname(): Promise<string | null> {
    return readPageNickname(this.browser.page);
  }

  private setState(state: LoginState, message: string | null): void {
    const changed = state !== this.state || message !== this.message;
    this.state = state;
    this.message = message;

    if (changed) {
      this.emitState();
    }
  }
}

function sleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve) => {
    const timer = setTimeout(() => {
      signal.removeEventListener("abort", onAbort);
      resolve();
    }, ms);

    const onAbort = (): void => {
      clearTimeout(timer);
      resolve();
    };

    signal.addEventListener("abort", onAbort, { once: true });
  });
}
