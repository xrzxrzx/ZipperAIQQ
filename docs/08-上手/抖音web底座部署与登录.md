# 抖音 Web 底座部署与登录

> 给**要把抖音从 `douyin-im` 底座换到浏览器底座**的人：照着走一遍就能收发；连不上翻 [§6 排错表](#6-排错表)。
> 背景与决策见 [`问题记录-06` 附录 C](../问题记录/问题记录-06-邮件-抖音视频-全家桶-密钥-破甲.md#附录-c--抖音对接环境取舍讨论稿2026-11-18)；
> 实施方案见 [`问题记录-06-实现方案`](../待办/问题记录-06-实现方案.md) 的 B1。
> sidecar 自身实现细节看 [`douyin-web-sidecar/README`](../../douyin-web-sidecar/README.md)。

---

## 0. 为什么有两个 sidecar

```
                    ┌──────────────────────────────┐
   （旧）douyin-im  │ douyin-sidecar/              │  driver=im
                    │  data/  ← 登录态              │  出站被平台按版本拒（10509）
                    └──────────┬───────────────────┘
                               │  都反连 /ws/channel
                    ┌──────────▼───────────────────┐     ┌──────────────────┐
                    │ Server：/ws/channel           │◀───│ douyin.driver     │
                    │ DouyinChannel（帧 ↔ 模型）     │     │ web | im（只收一个）│
                    └──────────▲───────────────────┘     └──────────────────┘
                               │
                    ┌──────────┴───────────────────┐
   （新）浏览器底座   │ douyin-web-sidecar/          │  driver=web
                    │  Playwright + 持久 profile    │  能发私信、能读评论（PoC 已验）
                    └──────────────────────────────┘
```

**两个底座共用同一套帧协议**，服务端靠 `hello.driver` 区分，**只接受与 `douyin.driver` 一致的那一个**
（不一致的连接会被关掉，原因写进日志与客户端状态卡）。所以：

- 「换底座」= 改 `douyin.driver` + 起对应的 sidecar；
- 「回退」= 把 `douyin.driver` 改回 `im`，旧 sidecar 重新连上即可 —— **不需要卸载任何东西**。

---

## 1. 前置条件（都在服务器上）

| 需要 | 说明 | 怎么确认 |
|---|---|---|
| **Node.js ≥ 22.5** | 全局 `WebSocket` / `fetch` 都要它（PoC 实测服务器是 v22.23.4） | `node -v` |
| **磁盘 ~400 MB** | Playwright 自带 Chromium + profile（实测 profile 94 MB） | `dir D:\App\ZipperAIQQ` |
| **内存余量 ≥ 1 GB** | 单实例 headless 约 800 MB（PoC 实测） | 任务管理器看空闲内存 |
| **计划任务身份 = SYSTEM** | 没有交互桌面 → **只能 headless**；有头浏览器在 SYSTEM 任务里起不来 | `schtasks /query /tn <task> /fo list` |
| **抖音账号** | 建议小号（浏览器自动化属于非官方路径，有风控风险） | —— |

---

## 2. 部署（三条命令）

脚本模板：`scripts/douyin-web-sidecar-deploy.template.ps1`
→ 复制成 `scripts/douyin-web-sidecar-deploy.local.ps1`（该名字已被 `.gitignore` 排除），把 `-Server` 填成真值。

```powershell
# ① 服务器上装依赖（会下载 Playwright 的 Chromium，约 400 MB —— 会先提示确认）
.\douyin-web-sidecar-deploy.local.ps1 -Action install

# ② 编译（TypeScript → dist/）
.\douyin-web-sidecar-deploy.local.ps1 -Action build

# ③ 建计划任务（ZipperAIQQ-DouyinWeb，onstart / SYSTEM）并启动
.\douyin-web-sidecar-deploy.local.ps1 -Action start -Token <与服务端一致的 token>
```

> **token 口径**：服务端 `douyin.enabled=true` 且客户端填了 Sidecar Token 时，sidecar 必须带同一个 token
> （`?token=` query）。loopback 部署可以都不填 —— 但**非 loopback 强制**。

---

## 3. 切换到浏览器底座（客户端，两处）

1. 「第三方平台接入 → 抖音」→ 打开 **「使用浏览器底座」** → **保存抖音配置**
   （服务端写入 `douyin.driver=web`；**旧 sidecar 会被拒**，这是预期的）
2. 点 **「登录（显示二维码）」**

之后：

| 你会看到 | 含义 |
|---|---|
| 二维码出现，且可扫 | A-1a / A-1b 通过（图片来源已是浏览器） |
| 扫码后状态变「已登录」 | A-2 前半 |
| 重启 sidecar 后仍是「已登录」 | A-2 后半（profile 生效） |
| 「登录需要人工校验」面板 + 验证码框 / 滑块图 | 平台要求二次验证：填验证码，或给滑块偏移像素后提交 |

滑块只做「**图给你 + 你给偏移**」：面板会显示背景图，你量一下缺口离左边的像素数填进去即可。
（更自动的做法要跟风控长期搏斗，收益为负 —— 见实施方案 §G2。）

---

## 4. 验收清单（B1，逐条过）

| # | 判据 | 怎么验 |
|---|---|---|
| **A-1a** | **页面已就绪时**点「登录」→ **≤2 秒**出二维码 | 先在抖音页停一下再点登录；日志 `已推送登录二维码` |
| **A-1b** | **冷启动**（含 Chromium 启动 + 首次导航）→ **≤15 秒**出二维码 | 服务重启后第一次点登录（Chromium 冷启动本身就要几秒） |
| **A-2** | 扫码后 `login.state=online`；重启 sidecar 仍在线上 | `-Action restart` 后看状态卡 |
| **A-3** | 私聊发文本成功；发图片成功；引用降级有标注 | 让 AI 回一条；日志 `出站成功：… 降级=reply_to` |
| **A-4** | 收到对方消息 → `inbound` 只进一次（重连后不重放） | 让对方发两条；日志里 `入站私信` 不重复 |
| **A-5** | `douyin.driver=im` 切回可收发（回退可用） | 关掉浏览器底座开关 + 起旧 sidecar |

---

## 5. 回退

```
客户端：抖音 → 关掉「使用浏览器底座」→ 保存   （douyin.driver 回到 im）
服务器：把旧的 douyin-sidecar 计划任务跑起来    （ZipperAIQQ-DouyinSidecar）
```

浏览器底座 sidecar 可以先停（`-Action stop`），**profile 不用删** —— 里面是登录态，
下次切回 web 时直接复用，不必重新扫码。

---

## 6. 排错表

| 现象 | 先查什么 |
|---|---|
| 客户端一直「未连入」 | ① 服务端 `douyin.enabled` 是否打开（关着时 `/ws/channel` 回 404）② token 是否两边一致 ③ sidecar.log 里有没有 `连接 /ws/channel 失败` |
| 状态卡显示「已拒绝一条 sidecar 连接」 | `douyin.driver` 与 sidecar 自报不一致：旧 sidecar 自报 `im`、新 sidecar 自报 `web`。改配置或起对应那个 |
| 发消息失败、`code=readback` | 发出去了但**回读不到**：多半被风控静默吞了，或 DOM 改版。看 `site.ts` 的 `readTail` |
| 发消息失败、`code=not_found` | 找不到会话：列表里没有该 uid（列表行不一定带 uid）→ 让对方先发一条（会学到昵称） |
| 发消息失败、`code=auth` | 登录态失效：重新扫码 |
| 发消息失败、`code=risk` | 风控（滑块 / 频率）：等一会儿，别连续发 |
| 收不到消息 | 日志有没有 `已旁听入站 WS`；页面是否还在 `/chat`（切走会断流）。WS 静默 5 分钟后会走 DOM 兜底并打 `DOM 兜底转发`；日志里「DOM 兜底放弃」= 认不出当前会话（先打开那个会话） |
| **两只 sidecar 同时在线** | 驱动闸门只保证**不会长期跑错底座**：被拒的连接会被关掉，合法那条要等它自己重连（1~15 s）。切换期**只开一只**最省心 |
| 内存一直涨 | 看 `browser.state` 的 `memoryMb`；`recycleHours`（主）/ `maxJsHeapMb`（兜底，渲染堆 ≠ 进程内存）会触发重建（profile 保留） |
| 页面崩了 | 体检每 60 秒会自愈重建；日志 `浏览器处于崩溃状态，自动重建` |

---

## 7. 未真机验收清单（联调时逐条确认）

| # | 假设 | 状态 |
|---|---|---|
| U-1 | 网页版私信页的会话行能拿到 uid（否则只能靠昵称定位） | **未验**（`site.ts` 的 `listConversations` 尽力而为） |
| U-2 | 图片发送入口是 `input[type=file]` 或可点的「图片」按钮 | **未验**（PoC 没发过图） |
| U-3 | 短信验证码输入框能被 `placeholder*="验证码"` 命中 | **未验** |
| U-4 | 滑块容器 `#captcha_verify_container` 在当前版本仍是这个名字 | **未验** |
| U-5 | WS 帧在重连后不会重放旧消息（服务端还有 `Messages` 唯一索引兜底） | **未验** |
| U-6 | 群聊入站（`conversationType=2`）的 threadId 推导 | **未验**（当前账号群列表为 0） |

> 这六条都是"跑起来就能确认"的，不需要额外实验：**按 §4 验收清单走一遍，顺手记结果就行**。
