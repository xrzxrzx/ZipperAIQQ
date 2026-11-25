#!/usr/bin/env node
/**
 * 邮件 HTML 渲染器：一次性 CLI（用完即退，不起常驻进程）。
 *
 * 用途：把一封 HTML 邮件的源码渲染成「图片」，供模型看版式（表格 / 按钮 / 隐藏文本 / 跟踪像素）。
 * 归属：B3（邮件双通道）的通道 A；按 G4 的「外置件」口径 —— 缺失即降级，服务端照常启动。
 *
 * 用法：
 *   node render.mjs --html <file> --out <dir> [--max-width 1000] [--max-images 4]
 *                   [--segment-height 1400] [--timeout 30] [--allow-remote]
 *                   [--playwright <含 node_modules 的目录>]
 *
 * 输出：**单行 JSON** 到 stdout。成功 `{"ok":true,"engine":"chromium|msedge|chrome",...}`；
 *       失败 `{"ok":false,"error":"..."}`。C# 侧只认这一个约定（不要往 stdout 打日志）。
 *
 * 三条安全默认（对齐设计 §1 的验收口径）：
 *   1. **默认禁脚本**：`javaScriptEnabled: false` —— 邮件里的 JS 一行都不跑；
 *   2. **默认拦外链**：所有 http(s) 请求直接 abort（跟踪像素 / 远程 CSS 不外发）；
 *      需要放行时显式 `--allow-remote`；
 *   3. 体积上限：分段高度 × 图片张数封顶，长邮件宁可只给前几段，也不一次吐 50 MB 图。
 */

import { createRequire } from "node:module";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT_DIR = dirname(fileURLToPath(import.meta.url));

/** 默认从仓库里的抖音 web sidecar 借 playwright（同一个 Chromium 安装，不重复下载）。 */
const DEFAULT_PLAYWRIGHT_DIR = resolve(SCRIPT_DIR, "..", "..", "douyin-web-sidecar");

/** 分段高度上限：与 max-images 一起决定整封邮件的最大像素量。 */
const MAX_SEGMENT_HEIGHT = 4000;

/**
 * 视口高度上限。
 *
 * 为什么要放大视口：Playwright 的 `clip` 是**页面坐标**，但「结果图」只有视口那么大 ——
 * 视口外的那一块会直接报 "Clipped area is either empty or outside the resulting image"。
 * 所以先量出整页高度、把视口撑到能覆盖所有要截的段，再按段 clip。
 * 16000 是 Chromium 能接受的量级，再高就按上限截断（长邮件本来也不该整封喂给模型）。
 */
const MAX_VIEWPORT_HEIGHT = 16000;

/**
 * 视口宽度上限：与高度同理 —— 邮件 HTML 常写死 `width:1200px`，视口窄于页面时
 * body 盒会超出视口，clip 的右半边就落在结果图外面。
 */
const MAX_VIEWPORT_WIDTH = 2400;

const options = parseArgs(process.argv.slice(2));

main()
  .then((result) => {
    process.stdout.write(JSON.stringify(result) + "\n");
    process.exit(result.ok ? 0 : 1);
  })
  .catch((error) => {
    process.stdout.write(JSON.stringify({ ok: false, error: describe(error) }) + "\n");
    process.exit(1);
  });

async function main() {
  if (!options.html) {
    return { ok: false, error: "缺少 --html（待渲染的 HTML 文件路径）" };
  }

  if (!options.out) {
    return { ok: false, error: "缺少 --out（截图输出目录）" };
  }

  const html = await readFile(options.html, "utf8");
  if (!html.trim()) {
    return { ok: false, error: "HTML 文件是空的" };
  }

  const playwright = loadPlaywright(options.playwrightDir);
  const launched = await launch(playwright, options.timeoutMs);
  const { browser, engine } = launched;

  try {
    const context = await browser.newContext({
      viewport: { width: options.maxWidth, height: 900 },
      deviceScaleFactor: 1,
      javaScriptEnabled: false,
      bypassCSP: false,
    });

    // 外链拦截：默认全拦（防跟踪像素 / 远程 CSS）。data: 与 about: 放行。
    if (!options.allowRemote) {
      await context.route("**/*", (route) => {
        const url = route.request().url();
        const scheme = url.slice(0, url.indexOf(":") + 1).toLowerCase();
        if (scheme === "data:" || scheme === "about:" || scheme === "blob:") {
          return route.continue();
        }
        return route.abort();
      });
    }

    const page = await context.newPage();
    page.setDefaultTimeout(options.timeoutMs);

    await page.setContent(html, { waitUntil: "load", timeout: options.timeoutMs });

    // 用 Playwright 的 DOM.getBoxModel（不是页面 JS）量高度：JS 关着也能量。
    const box = await page.locator("body").boundingBox();
    const measuredHeight = Math.max(1, Math.ceil((box?.height ?? 900) + (box?.y ?? 0)));
    const fullWidth = Math.max(1, Math.ceil(box?.width ?? options.maxWidth));

    const coveredHeight = Math.min(measuredHeight, MAX_VIEWPORT_HEIGHT);
    const segments = planSegments(coveredHeight, options.maxImages, options.segmentHeight);

    // 撑视口：让每个 clip 都落在「结果图」之内（见 MAX_VIEWPORT_HEIGHT 的说明）。
    // 宽度也要撑：邮件 HTML 常写死 width:1200px，视口只有 1000 时 body 盒会超出视口，
    // 于是 clip 的右半边落在结果图外面 —— 和高度是同一类坑。
    const lastSegment = segments[segments.length - 1];
    const viewportHeight = Math.max(1, Math.min(coveredHeight, lastSegment.y + lastSegment.height));
    const viewportWidth = Math.min(Math.max(fullWidth, options.maxWidth), MAX_VIEWPORT_WIDTH);
    await page.setViewportSize({ width: viewportWidth, height: viewportHeight });

    await mkdir(options.out, { recursive: true });

    const images = [];
    for (const segment of segments) {
      const buffer = await page.screenshot({
        type: "jpeg",
        quality: 80,
        clip: {
          x: 0,
          y: segment.y,
          // 再夹一次宽度：页面比视口还宽（超宽表）时，越界部分不该让整次渲染报错
          width: Math.min(fullWidth, viewportWidth),
          height: segment.height,
        },
      });

      const file = join(options.out, `mail-${segment.index + 1}.jpg`);
      await writeFile(file, buffer);
      images.push({
        path: file,
        index: segment.index,
        width: Math.min(fullWidth, viewportWidth),
        height: segment.height,
        bytes: buffer.length,
      });
    }

    return {
      ok: true,
      engine,
      pageHeight: measuredHeight,
      pageWidth: fullWidth,
      // plannedSegments 是「按几何本该有几张」，跟实际给几张分开报 —— 被上限砍掉时模型要知道后面还有。
      plannedSegments: Math.max(1, Math.ceil(measuredHeight / options.segmentHeight)),
      truncated: coveredHeight < measuredHeight || segments.length < Math.ceil(coveredHeight / options.segmentHeight),
      images,
    };
  } finally {
    await browser.close().catch(() => {});
  }
}

/** 解析命令行参数（不引第三方 CLI 库：就这几个键，手写更省事）。 */
function parseArgs(argv) {
  const result = {
    html: null,
    out: null,
    maxWidth: 1000,
    maxImages: 4,
    segmentHeight: 1400,
    timeoutMs: 30_000,
    allowRemote: false,
    playwrightDir: process.env.MAIL_RENDER_PLAYWRIGHT || DEFAULT_PLAYWRIGHT_DIR,
  };

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    switch (arg) {
      case "--html":
        result.html = argv[++i];
        break;
      case "--out":
        result.out = argv[++i];
        break;
      case "--max-width":
        result.maxWidth = clampInt(argv[++i], 240, 2000, result.maxWidth);
        break;
      case "--max-images":
        result.maxImages = clampInt(argv[++i], 1, 12, result.maxImages);
        break;
      case "--segment-height":
        result.segmentHeight = clampInt(argv[++i], 200, MAX_SEGMENT_HEIGHT, result.segmentHeight);
        break;
      case "--timeout":
        result.timeoutMs = clampInt(argv[++i], 5, 120, result.timeoutMs / 1000) * 1000;
        break;
      case "--allow-remote":
        result.allowRemote = true;
        break;
      case "--playwright":
        result.playwrightDir = argv[++i];
        break;
      default:
        break;
    }
  }

  return result;
}

function clampInt(raw, min, max, fallback) {
  const value = Number.parseInt(raw ?? "", 10);
  if (!Number.isFinite(value)) {
    return fallback;
  }

  return Math.min(max, Math.max(min, value));
}

/**
 * 载入 playwright：按「目录 → 该目录下的 package.json」解析，而不是靠当前工作目录。
 *
 * 为什么要这么绕：ESM 的模块解析看的是**脚本文件所在目录**，不是 cwd；
 * 脚本在 tools/mail-render 下，直接 `import "playwright"` 会找不到包。
 */
function loadPlaywright(dirOrFile) {
  const anchor = dirOrFile.endsWith(".json") ? dirOrFile : join(dirOrFile, "package.json");
  const require = createRequire(anchor);

  try {
    return require("playwright");
  } catch (error) {
    throw new Error(
      `找不到 playwright（解析目录 ${dirOrFile}）：${describe(error)}。` +
        "可以先把 douyin-web-sidecar 的依赖装好，或用 --playwright 指定目录。",
    );
  }
}

/** 依次尝试 playwright 自带 Chromium → 系统 Edge → 系统 Chrome。 */
async function launch(playwright, timeoutMs) {
  const candidates = [
    { engine: "chromium", options: {} },
    { engine: "msedge", options: { channel: "msedge" } },
    { engine: "chrome", options: { channel: "chrome" } },
  ];

  const failures = [];
  for (const candidate of candidates) {
    try {
      const browser = await playwright.chromium.launch({
        headless: true,
        timeout: timeoutMs,
        ...candidate.options,
      });
      return { browser, engine: candidate.engine };
    } catch (error) {
      failures.push(`${candidate.engine}: ${describe(error).split("\n")[0]}`);
    }
  }

  throw new Error("没有可用的浏览器内核（" + failures.join("；") + "）");
}

/** 按「整页高度 → 分段」规划截图区间（最后一段取剩余高度，不越界）。 */
function planSegments(totalHeight, maxImages, segmentHeight) {
  const planned = Math.max(1, Math.ceil(totalHeight / segmentHeight));
  const count = Math.min(planned, maxImages);

  const segments = [];
  for (let index = 0; index < count; index++) {
    const y = index * segmentHeight;
    const height = Math.min(segmentHeight, totalHeight - y);
    if (height <= 0) {
      break;
    }

    segments.push({ index, y, height });
  }

  return segments;
}

function describe(error) {
  if (!error) {
    return "未知错误";
  }

  return String(error.message ?? error).replace(/\s+/g, " ").trim();
}
