# tools/mail-render

邮件 HTML 渲染器（**一次性 CLI**，不起常驻进程）。B3「邮件双通道」的通道 A：
把一封 HTML 邮件的源码渲染成图片，让模型能看版面（表格 / 按钮 / 隐藏文本 / 跟踪像素）。

## 为什么是外置进程

- 服务端要跨平台、且不该为了截一张图把 Chromium 焊进 .NET 进程；
- 仓库里已经有 Playwright（`douyin-web-sidecar`），**共用同一份浏览器安装**，不必再下一次；
- 对齐方案 G4：外置件缺失 → 工具不出现 + 原因，服务端照常启动。

## 用法

```bash
node tools/mail-render/render.mjs \
  --html <mail.html> --out <输出目录> \
  [--max-width 1000] [--max-images 4] [--segment-height 1400] \
  [--timeout 30] [--allow-remote] [--playwright <含 node_modules 的目录>]
```

- 输出：**单行 JSON** 到 stdout（stdout 上不许有别的东西）。
  - 成功：`{"ok":true,"engine":"chromium|msedge|chrome","pageHeight":…,"plannedSegments":…,"truncated":…,"images":[{"path","index","width","height","bytes"}]}`
  - 失败：`{"ok":false,"error":"…"}`，退出码 1。
- 浏览器内核按 **playwright 自带 Chromium → 系统 Edge → 系统 Chrome** 顺序尝试；
  实测本机（无 Playwright Chromium）走的是 `msedge`。

## 三条默认行为（别随手改）

| 行为 | 默认 | 理由 |
|---|---|---|
| JavaScript | **关** | 邮件里的脚本一行都不跑 |
| 外链资源（http/https） | **全拦**（`route.abort()`） | 跟踪像素 / 远程 CSS 不把「这封邮件被打开过」告诉第三方 |
| 像素上限 | 分段 ≤1400 px × ≤4 张、视口 ≤16000 px | 长邮件宁可只给前几段，也不一次吐几十 MB 图 |

## 坑（踩过的）

1. **`clip` 是页面坐标，但结果图只有视口那么大** —— 视口外的段会直接报
   `Clipped area is either empty or outside the resulting image`。所以脚本先量整页高度、
   把视口撑到覆盖所有要截的段，再逐段 `clip`。
   **宽也一样**：邮件 HTML 常写死 `width:1200px`，视口只有 1000 时 body 盒超出视口，
   右半边同样越界 —— 所以宽度也要撑（上限 2400），clip 宽度再夹一次。
2. **ESM 模块解析看脚本所在目录，不看 cwd** —— 脚本在 `tools/mail-render/`，
   直接 `import "playwright"` 会 `ERR_MODULE_NOT_FOUND`；必须用
   `createRequire(<douyin-web-sidecar>/package.json)` 解析。
3. **量高度不能用页面 JS**（脚本已关），用 Playwright 的 `locator.boundingBox()`
   （走 CDP 的 DOM.getBoxModel）。
4. **Chromium 要在自己的临时目录里建 profile**：受限环境（或安全策略收紧的部署机）里
   系统 TEMP 下 `mkdtemp` 会被拒，浏览器起不来只报 `EPERM: mkdtemp`，看着像"没装浏览器"。
   Server 侧已经把子进程的 `TEMP/TMP/TMPDIR` 指到本次工作目录。

## 自测

```bash
# 造一封长邮件（含表格 / 按钮 / 隐藏文本 / 远程像素）
node tools/mail-render/render.mjs --html <某封邮件.html> --out ./tmp/out --max-images 4
# 看 JSON 里的 images 数是否等于 ceil(pageHeight/1400)，再打开图确认版面
```
