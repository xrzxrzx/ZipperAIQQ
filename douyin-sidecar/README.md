# 抖音 sidecar（douyin-sidecar）

把 [`douyin-im`](https://github.com/xrzxrzx/douyin-im)（Node 侧抖音协议库）接进 ZipperAIQQ 的桥进程。

```
douyin-im（登录 / 事件 / 发送）
        │  ① 归一化 → 翻译层（inbound 帧）
        ▼
   bridge.ts  ──WS 连入──▶  Server  /ws/channel
        ▲                        │
        └──── outbound / ack ────┘
```

- 设计依据：[`docs/03-设计/抖音接入设计.md`](../docs/03-设计/抖音接入设计.md)（§2 拓扑、§3 协议、§4 映射、§6.3 配置、§10 风险）
- **sidecar 主动连入 Server**（反向连接）：Server 常驻、sidecar 可重启，省掉端口分配，断了自动重连
- ⚠️ 本文档所有"我没验证过"的地方都写在 [§8 未真实账号验收的 API](#8-未真实账号验收的-api) 里 —— **先看那一节再排错**

---

## 1. 装什么

| 依赖 | 版本 | 说明 |
|---|---|---|
| **Node.js** | **≥ 22.5** | 需要全局 `WebSocket` / 内置 `fetch`；`douyin-im` 本身要求 `node:sqlite` |
| `douyin-im` | **`1.1.0`（或你的 fork）** | 默认锁 npm 包 `1.1.0`；若用 fork，见下面的"替换依赖"步骤，**不用 submodule / 不用 vendor/** |
| `typescript` + `@types/node` | devDependencies | 编译（`npm run build`） |
| `tsx` | devDependency | **只用于 `npm run dev`**（本项目只选 tsx，不用 ts-node） |

### 1.1 安装（用户手工步骤，CI / 助手不代劳）

```powershell
cd douyin-sidecar

# ① 可选：改用你的 fork + 固定 commit（默认已锁 npm 包 1.1.0）
#    "douyin-im": "github:xrzxrzx/douyin-im#<commit-sha>"
#    （务必钉 commit：分支名会漂，出问题无法复现）
notepad package.json

# ② 安装依赖
npm install

# ③ 编译
npm run build      # tsc → dist/bridge.js
```

> **为什么不用 git submodule / vendor/**：仓库有 CI 守卫拦 gitlink；而且登录态与依赖都该留在本机，
> 不该把第三方源码塞进主仓。拉取 fork 是**你手工的一步**，助手不代为下载。

---

## 2. 怎么配

配置文件：**`douyin-sidecar/config.json`**（相对**启动时的工作目录**，即 `cd douyin-sidecar` 后启动）。
该文件**已在 `.gitignore` 里**（含 token，绝不入库）。样例：

```json
{
  "serverUrl": "ws://127.0.0.1:5180/ws/channel",
  "token": "与 Server 端 douyin.token 完全一致；loopback 部署可留空",
  "dataDir": "data",
  "maxTextLength": 16000,
  "heartbeatIntervalMs": 30000,
  "deadAfterMs": 90000,
  "logLevel": "info"
}
```

| 键 | 默认 | 说明 |
|---|---|---|
| `serverUrl` | `ws://127.0.0.1:5180/ws/channel` | Server 的 `/ws/channel` 端点（设计 §6.3） |
| `token` | 空 | 与 Server 的 `douyin.token` 一致。**loopback 免验**，同机部署可留空 |
| `dataDir` | `data` | 登录态目录（Cookie / 设备身份）—— **必须备份**，见 §5 |
| `maxTextLength` | `16000` | 单条文本上限；超长自动分段（设计 §1.3 / §4.2） |
| `heartbeatIntervalMs` | `30000` | 心跳间隔 |
| `deadAfterMs` | `90000` | 超过这么久**没收到任何帧**就判定连接已死，主动断开重连 |
| `logLevel` | `info` | `debug` / `info` / `warn` / `error` |

### 2.1 环境变量覆盖（优先级高于文件）

方便容器 / 计划任务里注入，不必把 token 写进文件：

| 变量 | 覆盖 |
|---|---|
| `ZIPPER_DOUYIN_CONFIG` | 配置文件路径（**必须存在**，否则启动即失败） |
| `ZIPPER_DOUYIN_SERVER_URL` | `serverUrl` |
| `ZIPPER_DOUYIN_TOKEN` | `token` |
| `ZIPPER_DOUYIN_DATA_DIR` | `dataDir` |
| `ZIPPER_DOUYIN_LOG_LEVEL` | `logLevel` |

### 2.2 鉴权：**token 走 query 参数**

```
ws://127.0.0.1:5180/ws/channel?token=<url-encoded-token>
```

**为什么不是 header**：Node 内置的全局 `WebSocket`（WHATWG 标准 API）**不支持自定义请求头**，
用 `Authorization: Bearer` 就得额外引入 `ws` 依赖。而 loopback 部署本来免 token（设计 §6.2）。

**要改成 header 只需动一处**：`src/bridge.ts` 的 `ChannelLink.connectOnce()` 里创建 socket 的那几行
（换成 `ws` 包的 `new WebSocket(url, { headers: { Authorization: ... } })`），`buildUrl()` 去掉 query 即可 ——
心跳 / 重连 / 翻译层都不受影响。

> ⚠️ token 出现在 URL 里意味着：**别把完整 URL 打进日志**（代码里 `redactToken()` 已抹成 `token=***`），
> 也别让 Server 端把 `/ws/channel` 的 query 记到访问日志里。

---

## 3. 怎么起

```powershell
cd douyin-sidecar

npm run build      # 编译（tsc）
npm start          # 跑 dist/bridge.js
# 开发时（免编译，变更即生效）：
npm run dev        # tsx src/bridge.ts —— 本项目只认 tsx，不装 ts-node
npm run typecheck  # 只做类型检查（tsc --noEmit）
```

启动顺序：**先登录 douyin-im**（可能阻塞到有人完成人机交互，见 §4）→ **再连 Server**
（Server 没起来也不致命，会一直按 1s→2s→4s…封顶 60s 的退避重连，退避带 ±20% 抖动）。

### 3.1 常驻守护（示例，任选其一）

**Windows 计划任务**（开机自启、崩溃重启）：

```powershell
$action  = New-ScheduledTaskAction -Execute "node.exe" `
           -Argument "dist/bridge.js" -WorkingDirectory "D:\Works\ZipperAIQQ\douyin-sidecar"
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName "ZipperAIQQ-DouyinSidecar" -Action $action -Trigger $trigger -Settings $settings -RunLevel Highest
```

**systemd**（Linux）：

```ini
# /etc/systemd/system/zipperaiqq-douyin-sidecar.service
[Unit]
Description=ZipperAIQQ douyin sidecar
After=network-online.target

[Service]
WorkingDirectory=/opt/zipperaiqq/douyin-sidecar
ExecStart=/usr/bin/node dist/bridge.js
Restart=always
RestartSec=5
Environment=ZIPPER_DOUYIN_LOG_LEVEL=info

[Install]
WantedBy=multi-user.target
```

> 日志：诊断日志走 **stderr**，人机交互提示走 **stdout**（见 §4）。
> 计划任务 / systemd 都会把两股流收走，排错时都要看。

### 3.2 优雅退出

`SIGINT` / `SIGTERM`（Windows 下 Ctrl+C）→ 关闭 WS、停 douyin-im、退出。
被强杀也没关系：Server 端的 pending ack 会超时清理（设计 §3.4），重连后照常工作。

---

## 4. 登录要人机交互（重要）

抖音登录**不能全自动**：扫码 / 短信验证码 / 滑块都可能出现（设计 §10 #2）。
sidecar 的做法是把**验证地址打到 stdout**，由人在浏览器里完成：

```
========================================================================
【需要人工登录】抖音登录要人机交互（扫码 / 短信 / 滑块），sidecar 不能自己完成。
原因 / 提示：login.verify
验证地址：https://<某验证页>
远程部署时：把上面这个地址复制到本机浏览器打开，登录完成后 sidecar 会自动继续。
登录态保存在 data/ 目录 —— 该目录必须备份，且绝不入库。
========================================================================
```

**首次部署的操作顺序**：

1. 用**带终端的方式**启动（`npm start`，或先 `systemctl status` / 计划任务日志里抓 stdout）
2. 等到上面这段横幅出现，**复制验证地址到本机浏览器**完成扫码 / 验证
3. 登录成功后 sidecar 自动继续，连上 Server，日志出现 `已连上 Server，hello 已发送`
4. 之后重启一般**免交互**（登录态在 `data/`）

**远程宿主怎么"代理"这个交互**：

| 方式 | 做法 |
|---|---|
| 最简单 | SSH / 远程桌面里直接看终端输出，URL 复制到**你自己电脑**的浏览器打开 |
| 抓日志 | `journalctl -u zipperaiqq-douyin-sidecar -f`（Linux）／计划任务日志（Windows），把 URL 抄出来 |
| 免手抄 | stdout 里只有这一段需要人看，可直接 `| Select-String 验证地址` 过滤（别把整段输出贴到聊天里） |

> 票据会过期（`markActive` 依赖真实用户活动，设计 §10 #3）：7×24 跑一段时间后可能重新要求登录。
> 届时同样看 stdout，不要让进程在无人值守下"静默挂着不登录"。

---

## 5. `data/` 必须备份（登录态）

`data/` 里是 **Cookie、设备身份、会话票据**：拿到它基本等于拿到这个抖音号。

- ✅ **必须备份**：停机后整目录复制（`Copy-Item data backup\data-2026xxxx -Recurse`）；换机器时整目录拷过去，可免重新登录
- 🚫 **绝不入库**：`.gitignore` 已排除 `data/`（含 `**/data/`），任何情况下不要 `git add -f`
- 🚫 **绝不外发**：不要贴进聊天、Issue、日志附件、截图
- 🚫 **不要多开共享**：同一个 `dataDir` 同时被两个进程用，登录态会互相踩（先停旧进程再起新的）
- 丢了怎么办：删掉 `data/` 重新走一遍 §4 的人机登录即可（不影响 Server 侧数据）

---

## 6. 帧收发（与设计文档的对应关系）

帧外壳统一 `{type, time, data}`（设计 §3.1），时间一律 **UTC Unix 毫秒**。

| 帧 `type` | 方向 | 触发 | 设计出处 |
|---|---|---|---|
| `hello` | sidecar → Server | WS 连上后**立即**发 | §3.2 / §3.3 |
| `inbound` | sidecar → Server | douyin-im 收到私聊 / 群消息 | §3.2 / §3.3 / §4.1 |
| `outbound` | Server → sidecar | Server 要发一条消息（带 `requestId`） | §3.2 / §3.3 |
| `ack` | sidecar → Server | `outbound` 处理完（成功 / 失败原因） | §3.2 / §3.3 |
| `ping` | Server → sidecar **或** sidecar → Server | 心跳 | §3.2 |
| `pong` | 对端回 | 收到 `ping` 立即回 | §3.2 |
| `query.contacts` | Server → sidecar | 「人员与名单」拉好友候选 | §3.2 / §3.5 |
| `query.groups` | Server → sidecar | 拉群候选 | §3.2 / §3.5 |
| `result` | sidecar → Server | 查询结果（`requestId` 配对） | §3.2 / §3.5 |

### 6.1 示例

```jsonc
// hello（连上即发；selfId 取不到时为空串）
{"type":"hello","time":1730000000000,"data":{
  "version":"1","platform":"douyin","selfId":"<uid>",
  "capabilities":["text","image","mention","reply"]}}

// inbound（私聊 → p<uid>；群聊 → g<gid>；stranger 直接丢弃）
{"type":"inbound","time":1730000000000,"data":{
  "session":"douyin:g123",
  "chatType":"group",
  "messageId":"<serverMessageId>",       // 平台没给时是 null
  "sender":{"userId":"<uid>","nickname":"<nick>","displayName":"<name>"},
  "segments":[{"type":"text","text":"hi"},{"type":"mention","userId":"123"}],
  "replyTo":null,
  "time":1730000000000}}                 // 内层 time = 平台时间，不取 UtcNow

// outbound
{"type":"outbound","time":0,"data":{
  "session":"douyin:g123",
  "segments":[{"type":"text","text":"你好"}],
  "replyTo":"<serverMessageId>",         // 可选
  "requestId":"<uuid>"}}

// ack
{"type":"ack","time":1730000000000,"data":{"requestId":"<uuid>","ok":true,"error":null}}
{"type":"ack","time":1730000000000,"data":{"requestId":"<uuid>","ok":false,"error":"conversation-risk 拒绝发送"}}

// query.contacts → result
{"type":"result","time":1730000000000,"data":{
  "requestId":"<uuid>","ok":true,"error":null,
  "contacts":[{"userId":"123","nickname":"老王","remark":"同事"}]}}

// query.groups → result
{"type":"result","time":1730000000000,"data":{
  "requestId":"<uuid>","ok":true,"error":null,
  "groups":[{"groupId":"456","name":"打工人","memberCount":32}]}}
```

### 6.2 消息映射（设计 §4）

| douyin-im | Server 侧 | 备注 |
|---|---|---|
| `message.private` | `ChatRef{douyin, Private, threadId}` → session `douyin:p<uid>` | |
| `message.group` | `ChatRef{douyin, Group, threadId}` → session `douyin:g<gid>` | |
| `message.stranger` | **丢弃** | 记 Debug，不打扰 |
| `senderUid` / 昵称 | `sender.userId` / `nickname` | |
| `content.kind = text` | `{"type":"text"}` | |
| `content.kind = image` | `{"type":"image","url"}` | |
| `mentions[]` | `{"type":"mention","userId"}` | uid **必须数字**，否则降级成文本 `@名字`（§10 #7） |
| `referenceInfo` | `replyTo` | |
| `serverMessageId` | `messageId`（入站去重键） | 缺失时 `null`，Server 侧不做去重 |
| 平台时间戳 | `data.time` | 缺了才用本地时间兜底，并记 warn |
| `video/emoji/file/...` | 占位文本 `[视频]` / `[表情]` / `[文件]` | 第一版不解析 |

出站：

| Server 段 | sidecar 动作 |
|---|---|
| `text` | 拼接后按 `maxTextLength` 分段；纯文本优先走 `sendText` |
| `mention` | 进 `mentions[]`（uid 非数字则忽略并告警） |
| `image` | **sidecar 自己下载 URL** → `uploadImage` → 图片段（设计 §4.2） |
| `reply` / `replyTo` | 走 `sendMessage` 的 reply 段 |
| 超长文本 | 自动分段，逐段发送；任一段失败 → `ack.ok=false` + 原因 |

---

## 7. 与 Server 侧对齐的假设（**需要 Server 实现者确认**）

设计文档定死了帧类型，但有几处只给了类型、没给字段，本实现的选择如下：

| # | 假设 | 本实现 | 若 Server 要改，动哪里 |
|---|---|---|---|
| 1 | `result` 的载荷形状 | `{requestId, ok, error, contacts}` / `{requestId, ok, error, groups}` | `handleQuery()` 一个函数 |
| 2 | 鉴权方式 | **query `?token=`**（理由见 §2.2） | `ChannelLink.buildUrl()` + `connectOnce()` |
| 3 | `hello` 由 **sidecar** 发 | 连上立即发（`/ws/ext` 是 Server 发 hello，**语义不同，未复用**） | `DouyinImAdapter.buildHelloFrame()` |
| 4 | `capabilities` 用小写数组 | `["text","image","mention","reply"]`（设计 §3.3 原文） | `CAPABILITIES` 常量 |
| 5 | 入站 `messageId` 可空 | 缺 `serverMessageId` 时发 `null` | `toInboundFrame()` |
| 6 | 查询失败语义 | `ok:false` + **空列表** + `error`，**不阻塞**（设计 §3.5） | `handleQuery()` |
| 7 | 心跳是**双向**的 | 收到 `ping` 回 `pong`；自己也定时发 `ping`；`deadAfterMs` 内无帧则重连 | `ChannelLink.startHeartbeat()` |

---

## 8. 未真实账号验收的 API

> 设计 §10 #1：**`douyin-im` 大量接口"未真实账号验收"**。下面每一项都只在文档 / README 层面得到过依据，
> **没有用真实账号跑过**。跑不通时按这张表逐条核对，用 `npm run dev` + `logLevel: "debug"` 看日志。

| # | API / 字段 | 本实现怎么用 | 不匹配时的现象 |
|---|---|---|---|
| 1 | 客户端导出名（`createClient` / `DouyinClient` / `default`） | `createClientFromModule()` 三种写法轮试 | 启动即报"没有导出客户端工厂" |
| 2 | 构造参数（`{dataDir}` / 位置参数 / `new`） | 同上轮试 | 启动即报"创建 douyin-im 客户端失败" |
| 3 | `start()` / `login()` 名称与是否需要参数 | `firstFunction(["start","login","connect","init"])` | 日志 warn"跳过自动登录" |
| 4 | 事件订阅 `on(name, handler)` 与事件名（`message` / `message.private` / `message.group` …） | 候选名一次全订 | **收不到消息**，debug 日志出现"忽略非消息事件" |
| 5 | 事件字段（`threadId` / `senderUid` / `serverMessageId` / `content.kind` / `mentions` / `referenceInfo`） | `normalizeInbound()` 多别名容错 | 消息内容缺字段、`messageId:null` |
| 6 | `sendText({threadId, conversationShortId, text, mentions})` | 纯文本路径 | `ack.ok=false`（方法不存在 → 改走 `sendMessage`） |
| 7 | `sendMessage({threadId, conversationShortId, segments, mentions})` | 图片 / 引用路径 | `ack.ok=false`："没有 sendMessage()" |
| 8 | `uploadImage()` | 图片出站前下载 + 上传 | `ack.ok=false`："没有 uploadImage()" |
| 9 | `getFriendList()` | `query.contacts` | `result.ok=false` + 空列表 |
| 10 | `getGroupList()` | `query.groups` | 同上 |
| 11 | 登录验证事件里的 URL 字段（`verifyUrl` / `url` / 二维码字段） | `extractLoginPrompt()` 多键探测 | 登录时**看不到验证 URL**（那就只有手动在宿主上跑 douyin-im 登录） |
| 12 | `conversationShortId` 是否必需 | 入站时缓存 `threadId → conversationShortId`，出站带上 | **主动**给"从没收到过消息的会话"发消息可能失败（缓存里没有） |
| 13 | 群 `@` 的 uid 必须数字（§10 #7） | 非数字降级为纯文本 `@名字` | 不会报错，但 @ 不生效 |
| 14 | `conversation-risk` 拒绝发送（§10 #6） | 异常转成 `ack.error` 回填给模型 | 消息发不出，但**不会静默吞** —— 模型能看到原因 |

**已知未做（第一版范围外，设计 §1.2）**：评论区、刷视频、表情收藏、视频消息、文件消息、多账号。

---

## 9. 排错

| 现象 | 多半是 | 怎么办 |
|---|---|---|
| `404`（握手就被拒） | Server 侧 `douyin.enabled=false`（关掉时端点直接 404） | 打开 Server 的 `douyin.enabled` |
| `401 / 403` | token 不一致 | 对齐 `config.json` 的 `token` 与 Server 的 `douyin.token`（注意 shell 转义 / 多了空格） |
| `ECONNREFUSED 127.0.0.1:5180` | Server 没起 / 端口不对 | 先起 Server；或改 `serverUrl` |
| 日志不停"重连（第 N 次）" | 上面两条之一；看退避间隔从 1s 涨到 60s 就说明一直没连上 | 修好网络 / 鉴权后**自动恢复**，不用重启 |
| 收不到消息 | 事件名 / 字段不匹配（§8 #4 #5） | `logLevel:"debug"`，看是否只有"忽略非消息事件" |
| `ack.ok=false ... sendMessage` | douyin-im 没有该方法（§8 #7） | 按 §8 表核对 fork 版本；纯文本仍可走 `sendText` |
| `ack.ok=false conversation-risk` | 平台风控拒绝（§10 #6） | 换个措辞 / 降频；这是**平台行为**，不是 bug |
| 登录横幅不出现 | 验证事件字段不匹配（§8 #11） | 在宿主上手动跑一次 douyin-im 登录，让 `data/` 就位 |
| 反复要求登录 | 票据过期（§10 #3）或 `data/` 不可写 | 检查目录权限；必要时删 `data/` 重登 |

---

## 10. 安全与合规

- **token 不进仓库、不进日志、不回显**：`config.json` 已 gitignore，日志里 URL 经 `redactToken()` 脱敏
- **`data/` 是账号级机密**：见 §5，永不入库、永不外发
- **小号 + 低频**：逆向协议**违反抖音 ToS，有封号风险**；用户已在设计中确认自担（设计 §10 #4）
- 本进程只连三样东西：本机 Server 的 `/ws/channel`、抖音服务、以及**出站图片 URL**（Server 下发的图片地址）
- 不监听任何端口（反向连接的意义就在这里，设计 §2.2）

---

## 附：目录结构

```
douyin-sidecar/
├── package.json        # type:module / engines >=22.5 / douyin-im git URL / tsc 与 tsx
├── src/bridge.ts       # 全部逻辑（翻译层 + 适配层 + 连接层 + 编排）
├── .gitignore          # data/ · node_modules/ · dist/ · config.json
├── README.md           # 本文
├── dist/               # 编译产物（不入库）
├── data/               # 登录态（不入库，必须备份）
└── config.json         # 本地配置含 token（不入库）
```

`src/bridge.ts` 的分层（便于以后补单测）：

| 层 | 导出 | 说明 |
|---|---|---|
| 翻译层（纯函数） | `normalizeInbound` / `toInboundFrame` / `buildOutboundPlan` / `chunkText` / `mapContacts` / `mapGroups` / `parseSessionId` / `sessionIdFor` / `parseFrame` | 不碰网络、不碰 douyin-im，可直接喂假数据 |
| 适配层 | `DouyinImAdapter` | **唯一** import douyin-im 的地方 |
| 连接层 | `ChannelLink` | **唯一**碰 WebSocket 的地方 |
| 编排 | `handleServerFrame` 等（模块内） | `outbound→ack`、`query.*→result`、`ping→pong` |
