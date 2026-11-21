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

  // 等**真的能用**的证据：会话列表里出现至少一行。
  // 原来只等 "有 div/li"，那在空壳页面也立刻成立 → 列表还没渲染就返回 0 行
  // （2026-11-20 真机：query.contacts 返回 0，实际 8 秒后列表里就有「拉链」）。
  await waitForConversationList(page, 25_000);

  const url = page.url();
  if (!url.includes("/chat")) {
    // 未登录会被弹回首页 / 登录页：这不是"页面坏了"，是登录态问题
    throw new Error(`未能进入私信页（当前 ${url}）：可能未登录`);
  }
}

/**
 * 等会话列表渲染出来（**首行出现**即算就绪）。
 *
 * @returns 等到返回 `true`；超时返回 `false`（调用方决定报错还是降级，不静默）
 */
export async function waitForConversationList(page: Page, timeoutMs: number): Promise<boolean> {
  const deadline = Date.now() + Math.max(1_000, timeoutMs);

  while (Date.now() < deadline) {
    const rows = await listConversations(page).catch(() => []);
    if (rows.length > 0) {
      return true;
    }

    await page.waitForTimeout(500);
  }

  return false;
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
        // 上面已把空白折叠成一个空格，这里不再按换行切（原本 split("\n") 永远拿全长）
        name: text,
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

/**
 * 找「登录」入口并点开（首页不自动弹登录框时用）。
 *
 * @returns 点到了返回 `true`
 */
export async function openLoginDialog(page: Page): Promise<boolean> {
  const candidates = [
    page.locator('button:has-text("登录")').first(),
    page.locator('[role="button"]:has-text("登录")').first(),
    page.locator("text=登录").first(),
  ];

  for (const candidate of candidates) {
    try {
      if ((await candidate.count()) === 0) {
        continue;
      }

      await candidate.click({ timeout: 3_000 });
      await page.waitForTimeout(1_500);
      return true;
    } catch {
      // 换下一个候选
    }
  }

  return false;
}

/**
 * 短信验证码输入框。
 *
 * @throws 页面上找不到（**不许**退回到"页面第一个 input"——那会把验证码打进搜索框）
 */
export async function findSmsInput(page: Page) {
  const locator = page.locator(
    'input[placeholder*="验证码"], input[aria-label*="验证码"], input[autocomplete="one-time-code"]',
  );

  if ((await locator.count()) === 0) {
    throw new Error("找不到短信验证码输入框（页面可能已换版式）");
  }

  return locator.first();
}

/** 读当前页的昵称（`meta` 里能拿到就返回，拿不到 `null` —— 不编）。 */
export async function readPageNickname(page: Page): Promise<string | null> {
  return page.evaluate(() => {
    const meta = document.querySelector('meta[name="author"], meta[property="og:title"]');
    const value = meta?.getAttribute("content") ?? null;
    return value && value.length <= 40 ? value : null;
  });
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

  /**
   * 反向表：显示名 → id。
   *
   * 为什么不直接遍历 `names` 反查：一个会话可能有多个 id 形态（data-* + href 各一个数字串），
   * `names` 里会有同名多条 → 「唯一命中」永远不成立，DOM 兜底等于永久关闭（审查 N-3）。
   * 这里每行只记**一个**规范 id（优先级与 `openConversation` 一致：长的那个更像真 uid）。
   */
  private readonly idsByNames = new Map<string, string>();

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

      const canonical = pickCanonicalId(row.ids);
      if (canonical && row.name) {
        this.idsByNames.set(row.name, canonical);
      }
    }
  }

  get size(): number {
    return this.names.size;
  }

  /**
   * 名字 → id 反查（DOM 兜底用：只能看到会话标题，看不到 id）。
   *
   * @returns 找不到 / 同名多条时返回 `null`（宁可丢一条消息，也不要挂到错的会话上）
   */
  idForName(name: string | null | undefined): string | null {
    if (!name) {
      return null;
    }

    return this.idsByNames.get(name) ?? null;
  }
}

/** 一行的若干 id 里挑一个"最像 uid"的：最长优先（短的多半是消息 id / 短链）。 */
function pickCanonicalId(ids: string[]): string | null {
  if (ids.length === 0) {
    return null;
  }

  return [...ids].sort((a, b) => b.length - a.length)[0] ?? null;
}

/**
 * 重载私信页（**DOM 兜底的第一步**）。
 *
 * 为什么必须重载：`/chat` 的会话列表是前端在 `get_message_by_init` 之后才渲染的，
 * 页面放着不动一段时间后列表可能只剩一个「抖音聊天」标题（实测 2026-11-21）。
 * 重载一次就能把列表拉回来 —— 比"等一下再看"可靠得多。
 */
export async function reloadChatPage(page: Page): Promise<void> {
  await page.reload({ waitUntil: "domcontentloaded", timeout: 60_000 });
  await waitForConversationList(page, 25_000);
}

/**
 * 按「会话行预览里的一段文字」找会话行。
 *
 * 为什么用预览文本而不是昵称：昵称可能重名、可能为空，而**刚收到的消息正文**
 * 一定出现在行的预览里（入站帧里我们已经拿到了）。
 */
export async function findConversationRowByPreview(page: Page, needle: string): Promise<ConversationRow | null> {
  const probe = needle.replace(/\s+/g, "").slice(0, 12);
  if (!probe) {
    return null;
  }

  const rows = await listConversations(page);
  const hit = rows.find((row) => row.name.replace(/\s+/g, "").includes(probe));
  return hit ?? null;
}

/** 点开一行的中心（列表是虚拟滚动，坐标点击最稳）。 */
export async function clickConversationRow(page: Page, row: ConversationRow): Promise<void> {
  await page.mouse.click(row.x + row.width / 2, row.y + row.height / 2);
  await page.waitForTimeout(2_000);
}

/**
 * 在当前已打开的会话里发一条文本并回读确认。
 *
 * @returns 是否在末尾看到刚发的那条
 */
export async function sendTextInOpenConversation(page: Page, text: string): Promise<boolean> {
  const input = await findInput(page);
  await input.click({ timeout: SITE.actionTimeoutMs });
  await page.keyboard.type(text, { delay: 12 });
  await page.waitForTimeout(300);
  await page.keyboard.press("Enter");

  const probe = text.replace(/\s+/g, "").slice(0, 12);
  const deadline = Date.now() + 8_000;
  while (Date.now() < deadline) {
    const tail = (await page.locator("body").innerText()).replace(/\s+/g, " ");
    if (tail.includes(probe)) {
      return true;
    }

    await page.waitForTimeout(500);
  }

  return false;
}

/**
 * 当前页面是不是已经停在某个会话里（判断依据：页面里出现了给定的线索文本）。
 *
 * 用途：多段发送时不要每次都 `reload` —— 重载会把已经打开的会话关掉，
 * 而第二次发送的定位线索（会话行预览）此刻已经变成「我们刚发出去的那条」。
 */
export async function isConversationOpen(page: Page, needles: string[]): Promise<boolean> {
  const hasInput = (await page.locator('textarea, [contenteditable="true"]').count()) > 0;
  if (!hasInput) {
    return false;
  }

  const body = (await page.locator("body").innerText()).replace(/\s+/g, " ");
  return needles.some((needle) => {
    const probe = needle.replace(/\s+/g, "").slice(0, 12);
    return probe.length > 0 && body.includes(probe);
  });
}

/**
 * 在会话行里按**多个线索**逐个尝试（入站预览 → 我们刚发的那条 → 昵称）。
 *
 * 为什么要多个：发出去一条之后，行的预览就变成我们发的那条了 ——
 * 只用入站预览会导致"第二条发不出去"（2026-11-21 真机）。
 */
export async function findConversationRowByAny(
  page: Page,
  needles: string[],
): Promise<ConversationRow | null> {
  for (const needle of needles) {
    const row = await findConversationRowByPreview(page, needle);
    if (row) {
      return row;
    }
  }

  return null;
}

/**
 * 在当前已打开的会话里发一张图片（**DOM 通道**，2026-11-21 真机验证成功）。
 *
 * 实测三步（缺一不可）：
 * 1. 往页面里的 `input[type=file]` 塞文件
 * 2. 页面弹出确认框「发送给 XX / 文件名 / 取消 / **发送（N）**」
 * 3. 点「发送（N）」——不点它永远不会上传，也不会有 `message/send`
 *
 * 为什么图片走 DOM 而不走 API：API 路径要先做
 * `upload/config/v2 → ApplyUploadInner → TOS 上传（STS 签名）→ CommitUploadInner`，
 * 然后发 `messageType=27` + `resource_url{oid,skey,md5}`；TOS 那步签名成本高，
 * 而 DOM 通道已经稳定可用。
 *
 * @returns 是否点到了「发送（N）」并成功提交
 */
export async function sendImageInOpenConversation(page: Page, filePath: string): Promise<boolean> {
  const inputs = page.locator("input[type=file]");
  const count = await inputs.count();
  if (count === 0) {
    return false;
  }

  await inputs.nth(count - 1).setInputFiles(filePath);

  // 确认框的按钮文案是「发送（1）」「发送（2）」…（全角括号）
  const confirm = page.getByText(/^发送（\d+）$/).first();
  const deadline = Date.now() + 15_000;
  while (Date.now() < deadline) {
    if ((await confirm.count()) > 0) {
      break;
    }

    await page.waitForTimeout(500);
  }

  if ((await confirm.count()) === 0) {
    return false;
  }

  await confirm.click({ timeout: SITE.actionTimeoutMs });

  // 确认框消失 ≈ 已提交（上传在后台继续）
  const gone = Date.now() + 10_000;
  while (Date.now() < gone) {
    if ((await confirm.count()) === 0) {
      return true;
    }

    await page.waitForTimeout(500);
  }

  return false;
}

/**
 * 打开「最近的一个会话」。
 *
 * 为什么启动后必须做这一步：网页版私信页停在**会话列表**时，
 * 抖音不会把新消息推到我们旁听的 WS 上（2026-11-22 真机：重启后入站全哑，
 * 打开一个会话后立刻恢复）。所以启动/重建后主动点开最近一条。
 *
 * @returns 是否成功打开（列表为空 / 没渲染都会返回 false，调用方只记日志）
 */
export async function openMostRecentConversation(page: Page): Promise<string | null> {
  const ready = await waitForConversationList(page, 20_000);
  if (!ready) {
    return null;
  }

  // 列表里会混进顶部导航行（标题「抖音聊天」），它不是会话，点它等于白点
  const rows = (await listConversations(page)).filter(
    (row) => row.name.trim().length >= 1 && !row.name.includes("抖音聊天"),
  );
  const first = rows[0];
  if (!first) {
    return null;
  }

  await clickConversationRow(page, first);
  await page.waitForTimeout(1_500);
  return first.name;
}

/**
 * 用页面 SDK **只做上传**，返回加密后的 oid/skey（不点发送按钮）。
 *
 * 为什么要借页面：我们自己走 `ApplyUploadInner/CommitUploadInner` 时平台不回
 * `Encryption{Uri,SecretKey}`（缺页面 SDK 的加密开关），导致发出去的图对方渲染不出来。
 * 页面 SDK 上传完会弹「发送（N）」确认框 —— 我们只取它上传得到的
 * `Encryption.Uri / Encryption.SecretKey`，**点取消**，消息仍由我们用 API 发。
 *
 * @returns 上传结果；超时/失败返回 `null`（调用方回落纯 API 或 DOM 发送）
 */
export async function uploadImageViaPageApiAware(
  page: Page,
  filePath: string,
  timeoutMs = 30_000,
): Promise<{ oid: string; skey: string; md5: string; size: number; width: number; height: number } | null> {
  const inputs = page.locator("input[type=file]");
  const count = await inputs.count();
  if (count === 0) {
    return null;
  }

  let uploaded: { oid: string; skey: string; md5: string; size: number; width: number; height: number } | null = null;
  const onResponse = async (response: import("playwright").Response): Promise<void> => {
    if (!/CommitUploadInner/.test(response.url())) {
      return;
    }

    try {
      const json = (await response.json()) as {
        Result?: { Results?: { Uri?: string; Encryption?: { Uri?: string; SecretKey?: string; encryption_md5?: string; encryption_size?: number } }[] };
      };
      const item = json.Result?.Results?.[0];
      const encryption = item?.Encryption;
      if (encryption?.Uri && encryption.SecretKey) {
        uploaded = {
          oid: encryption.Uri,
          skey: encryption.SecretKey,
          md5: encryption.encryption_md5 ?? "",
          size: encryption.encryption_size ?? 0,
          // 宽高在发消息的 content 里要用：这里从确认框里的缩略图量不出来，
          // 交给调用方用本地文件补齐（见 outbound），先占位 0。
          width: 0,
          height: 0,
        };
      }
    } catch {
      // 不是 JSON / 结构不对：忽略，让超时兜底
    }
  };

  page.on("response", onResponse);
  try {
    await inputs.nth(count - 1).setInputFiles(filePath);

    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline && !uploaded) {
      await page.waitForTimeout(500);
    }

    // 关掉确认框：这条消息由 API 发，页面不要再发一遍
    await page.getByText(/^取消$/).first().click({ timeout: 3_000 }).catch(() => undefined);

    return uploaded;
  } finally {
    page.off("response", onResponse);
  }
}
