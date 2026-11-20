/**
 * 抖音网页版的**站点知识（选择器 / 交互）集中地**。
 *
 * 为什么单独一个文件：DOM 改版是这条底座长期最大的风险（实施方案 §8 R-3）。
 * 改版时要动的只有这里 —— 别把 `querySelector` 撒到 `main.ts` / `outbound.ts` 里去。
 *
 * 所有函数都**只做页面动作**，不判断"该不该发"（那是 outbound / main 的职责），
 * 也不吞错误：拿不到就抛带中文原因的错，由 `protocol.classifyFailure` 分类。
 *
 * ⚠️ 这些选择器来自 2026-11-18 的 PoC 实测（`tmp/poc-douyin/poc-*.mjs`），
 * 不是官方文档。改版后先跑 README「自检」一节的清单再改这里。
 */

import type { Page } from "playwright";

/** 站点常量：URL 与等待时间。 */
export const SITE = {
  homeUrl: "https://www.douyin.com/",
  chatUrl: "https://www.douyin.com/chat",
  /** 页面首屏加载（domcontentloaded 之后还要等前端水合）。 */
  hydrateMs: 5_000,
  /** 单个 DOM 动作超时。 */
  actionTimeoutMs: 15_000,
} as const;

/** 会话列表里的一行。 */
export interface ConversationRow {
  /** 会话标题（私聊 = 对方昵称，群聊 = 群名）。 */
  name: string;
  /** 行上能拿到的所有 id 形态（data-* 属性 / href 里出现的数字串）。 */
  ids: string[];
  /** 视口内坐标（点击用：网页版列表是虚拟滚动，用坐标点最稳）。 */
  x: number;
  y: number;
  width: number;
  height: number;
}

/** 读到的消息气泡。 */
export interface ChatBubble {
  text: string;
  hasImage: boolean;
  x: number;
  y: number;
}

/** 登录二维码的位置（CSS 像素，视口坐标）。 */
export interface QrBox {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** 网页版登录挑战（短信 / 滑块）。 */
export type ChallengeDetection =
  | { kind: "sms"; hint: string }
  | { kind: "slider"; hint: string; bgBox: QrBox | null; pieceBox: QrBox | null }
  | null;

/** 进入私信页并等前端水合完。 */
export async function openChatPage(page: Page): Promise<void> {
  await page.goto(SITE.chatUrl, { waitUntil: "domcontentloaded", timeout: 60_000 });
  await page.waitForTimeout(SITE.hydrateMs);

  const url = page.url();
  if (!url.includes("/chat")) {
    // 未登录会被弹回首页 / 登录页：这不是"页面坏了"，是登录态问题
    throw new Error(`未能进入私信页（当前 ${url}）：可能未登录`);
  }
}

/**
 * 扫会话列表。
 *
 * 判据（纯启发式，来自 PoC 实测）：左栏、宽 200~360、高 40~110、含文本。
 * 额外把行内所有 `data-*` 属性值与 href 里的数字串收进 `ids`，
 * 这样能用 uid 直接匹配时就不必再靠昵称。
 */
export async function listConversations(page: Page): Promise<ConversationRow[]> {
  return page.evaluate(() => {
    const rows: ConversationRow[] = [];
    const seen = new Set<string>();

    for (const el of Array.from(document.querySelectorAll<HTMLElement>("div, li, a"))) {
      const rect = el.getBoundingClientRect();
      if (rect.width < 180 || rect.width > 380) continue;
      if (rect.height < 40 || rect.height > 130) continue;
      if (rect.x > 420) continue; // 左栏
      if (rect.y < 0 || rect.y > window.innerHeight) continue;

      const text = (el.innerText || "").replace(/\s+/g, " ").trim();
      if (!text || text.length > 120) continue;

      // 只留「最内层」那一行：子元素也满足条件时，取面积最小的
      const key = `${Math.round(rect.x)}:${Math.round(rect.y)}`;
      if (seen.has(key)) continue;
      seen.add(key);

      const ids: string[] = [];
      const collect = (node: Element): void => {
        for (const attr of Array.from(node.attributes)) {
          if (!attr.name.startsWith("data-")) continue;
          const digits = (attr.value || "").match(/\d{6,}/g);
          if (digits) ids.push(...digits);
        }
        const href = node.getAttribute("href");
        if (href) {
          const digits = href.match(/\d{6,}/g);
          if (digits) ids.push(...digits);
        }
      };

      collect(el);
      for (const child of Array.from(el.querySelectorAll("*")).slice(0, 12)) {
        collect(child);
      }

      rows.push({
        name: text.split("\n")[0] ?? text,
        ids: Array.from(new Set(ids)),
        x: Math.round(rect.x),
        y: Math.round(rect.y),
        width: Math.round(rect.width),
        height: Math.round(rect.height),
      });
    }

    // 面积最小的优先（外层容器也会命中上面的尺寸条件）
    rows.sort((a, b) => a.width * a.height - b.width * b.height);

    // 同一个 y 只留一个（最内层）
    const byY = new Map<number, ConversationRow>();
    for (const row of rows) {
      const bucket = Math.round(row.y / 8);
      if (!byY.has(bucket)) byY.set(bucket, row);
    }

    return Array.from(byY.values()).sort((a, b) => a.y - b.y);
  });
}

/**
 * 打开一个会话。
 *
 * 匹配顺序：① 行内 id 含目标 threadId（最可靠） → ② 昵称完全相等 → ③ 昵称包含。
 * 都对不上就抛错，**不点第一个凑数**（点错人会发错消息，这是最贵的错）。
 */
export async function openConversation(
  page: Page,
  target: { threadId: string; nameHint?: string },
): Promise<ConversationRow> {
  const rows = await listConversations(page);

  const byId = rows.find((row) => row.ids.includes(target.threadId));
  const byName = target.nameHint
    ? rows.find((row) => row.name === target.nameHint)
      ?? rows.find((row) => row.name.includes(target.nameHint as string))
    : undefined;

  const hit = byId ?? byName;
  if (!hit) {
    throw new Error(
      `找不到会话（threadId=${target.threadId}${target.nameHint ? `，昵称=${target.nameHint}` : ""}）：` +
        `列表里共 ${rows.length} 行，先让对方发一条消息或刷新私信页`,
    );
  }

  await page.mouse.click(hit.x + hit.width / 2, hit.y + hit.height / 2);
  await page.waitForTimeout(1_500);
  return hit;
}

/** 当前会话的标题（用于给入站消息补昵称；拿不到返回 `null`）。 */
export async function currentConversationName(page: Page): Promise<string | null> {
  const value = await page.evaluate(() => {
    // 私信页顶部标题：右栏顶部、宽度适中、居中
    for (const el of Array.from(document.querySelectorAll<HTMLElement>("div, span, h1, h2"))) {
      const rect = el.getBoundingClientRect();
      if (rect.width < 60 || rect.width > 320) continue;
      if (rect.height < 18 || rect.height > 48) continue;
      if (rect.y < 40 || rect.y > 220) continue;
      if (rect.x < 380) continue;
      const text = (el.innerText || "").trim();
      if (!text || text.length > 40) continue;
      return text;
    }

    return null;
  });

  return value;
}

/** 私信输入框（`textarea` 或 contenteditable，取最靠下那个）。 */
export async function findInput(page: Page) {
  const locator = page.locator('textarea, [contenteditable="true"]');
  const count = await locator.count();
  if (count === 0) {
    throw new Error("找不到私信输入框（页面可能没进到会话里 / 登录态失效 / DOM 改版）");
  }

  return locator.nth(count - 1);
}

/**
 * 发一条文本。
 *
 * ⚠️ 只用 `type` 逐字输入 + `Enter` 提交，**不用 `fill`**：
 * 网页版的输入框对 `fill` 不触发前端事件，发出去会是空的（PoC 实测）。
 */
export async function sendText(page: Page, text: string): Promise<void> {
  const input = await findInput(page);
  await input.click({ timeout: SITE.actionTimeoutMs });
  await page.keyboard.type(text, { delay: 12 });
  await page.waitForTimeout(300);
  await page.keyboard.press("Enter");
  await page.waitForTimeout(800);
}

/**
 * 发一张图。
 *
 * 通道优先级：① 页面里已有的 `input[type=file]`（最稳） → ② 点「图片」按钮后再找一次。
 * 两条都不成就抛 `upload` 类错误，不假装发成功。
 */
export async function sendImage(page: Page, filePath: string): Promise<void> {
  let fileInput = page.locator('input[type="file"]');

  if ((await fileInput.count()) === 0) {
    // 图片按钮：文字 / aria-label / title 里带「图片」「相册」「image」的按钮
    const button = page
      .locator(
        '[aria-label*="图片"], [title*="图片"], [aria-label*="相册"], button:has-text("图片")',
      )
      .first();

    if ((await button.count()) > 0) {
      await button.click({ timeout: SITE.actionTimeoutMs }).catch(() => undefined);
      await page.waitForTimeout(800);
      fileInput = page.locator('input[type="file"]');
    }
  }

  if ((await fileInput.count()) === 0) {
    throw new Error("找不到图片上传入口（input[type=file] 与图片按钮都没命中）");
  }

  await fileInput.first().setInputFiles(filePath);
  await page.waitForTimeout(2_500);
}

/**
 * 读当前会话末尾的若干条气泡（用于**发送后回读校验**）。
 *
 * 为什么必须回读：网页版点回车 ≠ 发出去了（风控 / 空内容 / 网络断都会静默吞掉）。
 * 只有「回读到刚发的那条」才算 `ack.ok=true`（实施方案 B1 出站关键设计）。
 */
export async function readTail(page: Page, limit = 6): Promise<ChatBubble[]> {
  const bubbles = await page.evaluate(() => {
    const out: { text: string; hasImage: boolean; x: number; y: number }[] = [];

    for (const el of Array.from(document.querySelectorAll<HTMLElement>("div"))) {
      const rect = el.getBoundingClientRect();
      if (rect.height < 18 || rect.height > 260) continue;
      if (rect.width < 40 || rect.width > 700) continue;
      if (rect.x < 360) continue;

      const text = (el.innerText || "").replace(/\s+/g, " ").trim();
      const hasImage = el.querySelector("img") !== null;
      if (!text && !hasImage) continue;
      if (text.length > 400) continue;

      out.push({ text, hasImage, x: Math.round(rect.x), y: Math.round(rect.y) });
    }

    // 只留最内层：形状相同的嵌套块取面积最小的
    out.sort((a, b) => (b.x - a.x) - (a.x - b.x) || b.y - a.y);
    const unique: typeof out = [];
    for (const item of out) {
      const duplicate = unique.some(
        (other) => other.text === item.text && Math.abs(other.y - item.y) < 6,
      );
      if (!duplicate) unique.push(item);
    }

    return unique.sort((a, b) => a.y - b.y);
  });

  return bubbles.slice(-limit);
}

/** 在登录页找「扫码登录」弹窗里的二维码（最大方形 canvas / img）。 */
export async function findQrBox(page: Page): Promise<QrBox | null> {
  return page.evaluate(() => {
    const anchors = Array.from(document.querySelectorAll<HTMLElement>("*")).filter(
      (el) => el.children.length === 0 && (el.textContent || "").trim() === "扫码登录",
    );

    let scope: HTMLElement | null = null;
    for (const anchor of anchors) {
      let node: HTMLElement | null = anchor;
      for (let depth = 0; depth < 12 && node; depth += 1) {
        if (node.querySelector && node.querySelector("canvas, img")) {
          const rect = node.getBoundingClientRect();
          if (rect.width > 300 && rect.width < 1000) {
            scope = node;
            break;
          }
        }
        node = node.parentElement;
      }
      if (scope) break;
    }

    const root: ParentNode = scope ?? document.body;
    const candidates: { x: number; y: number; width: number; height: number }[] = [];

    for (const el of Array.from(root.querySelectorAll<HTMLElement>("canvas, img"))) {
      const rect = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      if (style.visibility === "hidden" || style.display === "none") continue;
      candidates.push({
        x: Math.round(rect.x),
        y: Math.round(rect.y),
        width: Math.round(rect.width),
        height: Math.round(rect.height),
      });
    }

    const best = candidates
      .filter(
        (item) =>
          item.width >= 150 && item.height >= 150 && Math.abs(item.width - item.height) < 60,
      )
      .sort((a, b) => b.width * b.height - a.width * a.height)[0];

    return best ?? null;
  });
}

/**
 * 检测登录挑战（短信 / 滑块）。
 *
 * 判据尽量「宁漏不错」：只在**确定看到**验证码输入框 / 滑块容器时才报，
 * 因为误报会让服务端把状态切到 `waitingInput`，用户白等。
 */
export async function detectChallenge(page: Page): Promise<ChallengeDetection> {
  return page.evaluate(() => {
    const visible = (el: Element): boolean => {
      const rect = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      return (
        rect.width > 0 &&
        rect.height > 0 &&
        style.visibility !== "hidden" &&
        style.display !== "none"
      );
    };

    // ① 短信验证码：placeholder / label 里带「验证码」，或 one-time-code
    for (const el of Array.from(
      document.querySelectorAll<HTMLInputElement>('input[autocomplete="one-time-code"], input'),
    )) {
      if (!visible(el)) continue;
      const placeholder = el.getAttribute("placeholder") ?? "";
      const label = el.getAttribute("aria-label") ?? "";
      if (/验证码|校验码|code/i.test(placeholder + label)) {
        return { kind: "sms" as const, hint: "输入手机收到的验证码" };
      }
    }

    // ② 滑块：抖音的验证码容器 id 形如 captcha_verify_container
    const container =
      document.querySelector("#captcha_verify_container") ??
      document.querySelector('[class*="captcha"][class*="container"]');
    if (container && visible(container)) {
      const boxes = Array.from(container.querySelectorAll<HTMLElement>("canvas, img, div"))
        .filter(visible)
        .map((el) => {
          const rect = el.getBoundingClientRect();
          return {
            x: Math.round(rect.x),
            y: Math.round(rect.y),
            width: Math.round(rect.width),
            height: Math.round(rect.height),
          };
        })
        .filter((box) => box.width >= 200 && box.height >= 100);

      // 背景图通常最宽；拼图块较窄
      const bg = boxes.sort((a, b) => b.width * b.height - a.width * a.height)[0] ?? null;
      const piece =
        boxes
          .filter((box) => box.width < (bg?.width ?? 0) - 40 && box.width > 20)
          .sort((a, b) => b.width * b.height - a.width * a.height)[0] ?? null;

      return {
        kind: "slider" as const,
        hint: "给缺口水平偏移（px）",
        bgBox: bg,
        pieceBox: piece,
      };
    }

    return null;
  });
}

/**
 * 按给定水平偏移拖滑块。
 *
 * 轨迹做人化处理（先快后慢 + 小幅抖动 + 到位回拉），因为匀速直线拖动是最典型的风控特征。
 */
export async function dragSlider(page: Page, offset: number, anchor?: QrBox | null): Promise<void> {
  const handle = page
    .locator('[class*="captcha"] [class*="slider"], [class*="captcha"] [class*="btn"], [class*="verify"] [class*="slider"]')
    .first();

  let fromX: number;
  let fromY: number;

  if ((await handle.count()) > 0) {
    const box = await handle.boundingBox();
    if (!box) {
      throw new Error("滑块手柄没有可见区域（可能还没渲染完）");
    }
    fromX = box.x + box.width / 2;
    fromY = box.y + box.height / 2;
  } else if (anchor) {
    // 退路：滑块条在背景图正下方
    fromX = anchor.x + 24;
    fromY = anchor.y + anchor.height + 26;
  } else {
    throw new Error("找不到滑块手柄（既没有 captcha 滑块元素，也没有可参考的背景图位置）");
  }

  await page.mouse.move(fromX, fromY);
  await page.mouse.down();

  const steps = 24;
  for (let i = 1; i <= steps; i += 1) {
    const progress = i / steps;
    // 先快后慢：前 70% 走 90% 的距离
    const eased = progress < 0.7 ? progress * 1.28 : 0.9 + (progress - 0.7) * 0.34;
    const x = fromX + offset * Math.min(1, eased) + (Math.random() - 0.5) * 2;
    const y = fromY + (Math.random() - 0.5) * 3;
    await page.mouse.move(x, y);
    await page.waitForTimeout(12 + Math.floor(Math.random() * 18));
  }

  // 到位后回拉 2px（真人手抖的收尾）
  await page.mouse.move(fromX + offset - 2, fromY);
  await page.waitForTimeout(120);
  await page.mouse.up();
  await page.waitForTimeout(1_500);
}

/** 页面健康快照（`browser.state` 与状态页用）。 */
export async function pageHealth(page: Page): Promise<{
  url: string;
  title: string;
  hasInput: boolean;
}> {
  const hasInput = (await page.locator('textarea, [contenteditable="true"]').count()) > 0;
  return { url: page.url(), title: await page.title().catch(() => ""), hasInput };
}

/**
 * 会话 id → 显示名 的缓存。
 *
 * 两个用处：① 出站时用昵称帮 `openConversation` 定位（列表行不一定带 uid）
 * ② 入站私信补发送者昵称（协议里 `content` 不带昵称，只有会话标题有）
 *
 * 只放内存：重启后靠下一次扫列表 / 下一条消息补回来，不需要持久化。
 */
export class ConversationNameCache {
  private readonly names = new Map<string, string>();

  remember(threadId: string, name: string | null | undefined): void {
    if (!threadId || !name) {
      return;
    }

    this.names.set(threadId, name);
  }

  get(threadId: string): string | undefined {
    return this.names.get(threadId);
  }

  /** 用一次列表扫描整体刷新（同名不会覆盖成空）。 */
  refreshFromRows(rows: ConversationRow[]): void {
    for (const row of rows) {
      for (const id of row.ids) {
        this.remember(id, row.name);
      }
    }
  }

  get size(): number {
    return this.names.size;
  }
}
