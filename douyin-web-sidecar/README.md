# douyin-web-sidecar

抖音**浏览器底座** sidecar：Playwright 驱动真实 Chromium 打开抖音网页版，
把私信收发接进 ZipperAIQQ 的 `/ws/channel`。

> **为什么有这个项目**：原 `douyin-sidecar`（`douyin-im` 底层，已移除）自 2026-11-17 起出站被平台按版本拒绝
> （`status=3 check=10509 请前往「抖音聊天」官网，升级最新版本`），而 npm 上 `douyin-im` 只有 1.1.0、
> 仓库没有 releases —— **没有可升目标**。网页版实测能收发私信、能读评论（PoC P-4/P-5），
> 于是把「出站 + 主站能力」交给浏览器（决策 O4，见 `docs/问题记录/问题记录-06-*.md` 附录 C）。

---

## 它做什么 / 不做什么

| 做 | 说明 |
|---|---|
| 登录（二维码 / 短信 / 滑块） | 二维码截图走既有 `login.qr` 帧，**客户端零改动** |
| 出站私信（文本 / 图片） | 发送后**回读校验**才算成功；引用降级为普通消息并标注 |
| 入站私信 | 旁听页面自己的 `frontier-im` WebSocket 帧（不解签名、不裸调接口） |
| 会话候选查询 | `query.contacts` 用私信列表尽力而为 |
| 视频媒体（B4） | `media.video` / `media.frames` / `media.audio`：详情 → 下载 → **浏览器内抽帧 / 抽音**，不用 ffmpeg |

| 不做 | 理由 |
|---|---|
| 自己逆向签名 / 裸调 `imapi` | 要跟设备指纹长期搏斗，收益为负 |
| DOM 轮询做入站主路 | `/chat` 是虚拟滚动，离开视口的节点会被卸载 → 必漏消息 |
| 群列表 | 网页版私信页没有群列表入口（当前账号群列表本来就是 0） |
| 视频转码 / 上传第三方 | 抽帧抽音全在本机（同一 Chromium）；识别交给服务端走 DeepSeek ASR |

---

## 目录

```
src/protocol.ts   纯函数：帧解析 / 会话 ID / 分段 / 出站计划 / 入站归一化 / LRU / 失败分类
src/browser.ts    Chromium 生命周期（持久 profile、回收、crash 自愈、二维码截图）
src/site.ts       站点知识（**选择器只许放这里**）+ 会话名缓存
src/login.ts      登录编排（二维码 8s 刷新 / 短信 / 滑块）
src/outbound.ts   出站（定位会话 → 输入 → 发送 → 回读校验）
src/inbound.ts    入站（WS 旁听 + proto 解码 + 幂等 + DOM 兜底）
src/media.ts      视频媒体（详情 / 下载 / canvas 抽帧 / WebAudio 抽音，按窗口回传）
src/main.ts       编排：配置 → 连 Server → 分发帧 → 定时体检
proto/im.proto    最小协议子集（字段号照抄上游，有单测锁住）
test/             node:test 单测（不联网、不开浏览器）
```

---

## 视频媒体帧（B4）

服务端 → sidecar：

| 帧 | data | 说明 |
|---|---|---|
| `media.video` | `{requestId, itemId, timeoutMs?}` | 取视频详情（时长 / 文案 / 播放地址） |
| `media.frames` | `{requestId, itemId, timesMs[], maxWidth?, maxPayloadBytes?, timeoutMs?}` | 按给定时间点抽帧，回 JPEG base64 |
| `media.audio` | `{requestId, itemId, startMs, endMs, maxPayloadBytes?, timeoutMs?}` | 取一段 16 kHz 单声道 WAV（base64） |

sidecar → 服务端：一律是 `result` 帧（`{requestId, ok, error, code, ...}`）。

- **为什么抽音要按窗口**：`/ws/channel` 单帧上限 **1 MiB**（`WsChannelEndpoint.MaxFrameBytes`），
  3 分钟 16 kHz 单声道 WAV 就有 5.7 MiB。窗口大小由**服务端**定（默认 20 秒），服务端自己拼回整段再交 ASR。
- **视频本体按 `itemId` 缓存 10 分钟**（LRU 2 条）：一条 5 分钟的视频抽音要十几个窗口，不缓存等于反复下载。
- **页面里缓存解码结果**（`window.__zipperVideoAudio`）：第 2 个窗口起不再重新下载/解码；
  页面重建后缓存会没，sidecar 会自动补一次带本体数据的请求（回 `needData` 的那条路）。
- **抽帧/抽音的实测依据**：`docs/06-调研/抖音网页版底座实测.md` §7.3 / §7.4。


---

## 配置

读 `config.json`（可用 `ZIPPER_DOUYIN_WEB_CONFIG` 指到别处）。**没有配置文件也能跑**（全默认值）。

```jsonc
{
  "serverUrl": "ws://127.0.0.1:5180/ws/channel",
  "token": null,                 // 非 loopback 部署时必填（与服务端 douyin.token 一致）
  "profileDir": "data/douyin-web-profile", // ⚠️ 登录态就在这里：要备份、绝不入库
  "workDir": "data/tmp-media",   // 出站图片临时目录（发完即删）
  "headless": true,              // SYSTEM 计划任务没有交互桌面 → 只能 true
  "recycleHours": 6,             // 每 6 小时重建一次 context（profile 保留）
  "maxJsHeapMb": 600,            // 渲染进程 JS 堆超过 600 MB 提前重建（≠ 进程内存）
  "maxTextLength": 16000,
  "heartbeatIntervalMs": 30000,
  "deadAfterMs": 90000,
  "logLevel": "info"
}
```

环境变量：`ZIPPER_DOUYIN_WEB_CONFIG` / `ZIPPER_DOUYIN_WEB_SERVER_URL` / `ZIPPER_DOUYIN_WEB_TOKEN` /
`ZIPPER_DOUYIN_WEB_PROFILE` / `ZIPPER_DOUYIN_WEB_LOG_LEVEL`。

> 服务端侧还有一道闸门 `douyin.driver`（`im` / `web`）：只有自报 `driver` 与配置一致的 sidecar 才被接受。
> 回退就是把 `douyin.driver` 改回 `im` 并起旧 sidecar（实施方案 B1 验收 A-5）。

---

## 安装与运行

```powershell
# 1) 依赖（会顺带下载 Playwright 自带的 Chromium，约 400 MB）
npm install
npx playwright install chromium

# 2) 构建 + 单测
npm run build
npm test

# 3) 跑起来
npm start
```

首次登录：客户端「第三方平台接入」页点「登录」→ 服务端下发 `login.start` → 本 sidecar 打开登录页、
把二维码推回去；扫完码后登录态落进 `profileDir`，**之后重启不用再扫**。

---

## 排障：先看这三条

| 现象 | 先查 |
|---|---|
| 客户端一直「未连入」 | `serverUrl` / `token` 对不对；服务端 `douyin.enabled` 是否打开（关着时 `/ws/channel` 返回 404） |
| 能连上但发不出去 | 日志里 `code=` 是什么：`auth` 要重扫、`risk` 要冷却、`not_found` 是选择器或会话名不匹配、`readback` 是被静默吞了 |
| 收不到消息 | 日志里有没有「已旁听入站 WS」；页面是否还停在 `/chat`（切走会断流）。WS 静默 5 分钟后会走 DOM 兜底并打 `DOM 兜底转发`；兜底认不出会话时会明说「放弃」而不是挂到假会话上 |
| 日志里「陌生人盒消息」 | 陌生人私信第一版不响应（与旧底座同口径），但**会记录**，不是坏了 |

`site.ts` 是 DOM 选择器的唯一入口 —— 页面改版时**只改那一个文件**，改完按下面的清单自检：

1. 会话列表能扫出来（`query.contacts` 有返回）
2. 能打开目标会话（不点错人）
3. 发一条文本能回读到自己那条
4. 二维码能截出来（扫码框可扫）
