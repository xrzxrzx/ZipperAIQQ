# 抖音 sidecar 部署与登录

> 给**第一次部署抖音接入**的人：照着从头做一遍就能连上；连不上就翻 [§7 排错表](#7-排错表)。
> 协议 / 帧格式 / 风险看[抖音接入设计](../03-设计/抖音接入设计.md)；sidecar 自身的实现细节看
> [douyin-sidecar/README](../../douyin-sidecar/README.md)（1631 行的 `src/bridge.ts` 是协议实现本体）。
> ⚠️ **当前状态：代码已落地、真机未验收** —— [§8](#8-未真机验收清单联调时逐条确认) 那批假设必须在小号联调时逐条过。

---

## 0. 先搞清关系（看这一节就够，别急着敲命令）

```
┌────────────────────────────┐
│ Node sidecar（独立进程）    │  自己登录抖音（扫码 / 短信验证码）
│  douyin-sidecar/           │  自己收消息 → 翻译成帧
│  ├ douyin-im（协议库）      │
│  ├ data/      ← 登录态      │
│  └ config.json← token/地址  │
└──────────┬─────────────────┘
           │ ① sidecar 主动连入（反向连接，它不开端口）
           │    ws://<server-ip>:5180/ws/channel
           ▼
┌────────────────────────────┐
│ Server（.NET）              │  不碰账号密码：它只认"连进来的人带了正确的 token"
│  /ws/channel ← sidecar      │  帧 ↔ 平台无关模型（DouyinChannel）
│  MessageRouter / Agent      │
└──────────┬─────────────────┘
           ▲ ② 客户端只是改配置（POST /api），不负责拉起 sidecar
           │
      WinUI3 客户端「第三方平台接入」→ 抖音
```

| 谁 | 干什么 | **不**干什么 |
|---|---|---|
| **sidecar** | 登录抖音、收消息、发消息；主动连入 Server；断线自己按 1s→60s 退避重连 | 不开监听端口；不落业务数据；不知道 Agent / 人设 |
| **Server** | 鉴权、收帧、路由、调模型、回发 | **不碰抖音账号密码**、不管登录、不主动连 sidecar |
| **客户端** | 改 `enabled` / token / 超时（写库、立即生效）；显示连通状态 | **不拉起 sidecar**（页面原话："本页只改配置，不负责拉起它"） |

> 📌 为什么必须 sidecar：`douyin-im` 是 Node 22.5+ / ESM / `node:sqlite`，.NET 调不动
> （[抖音接入设计 §1.3](../03-设计/抖音接入设计.md)）。

---

## 1. 前置条件

| 需要 | 说明 | 怎么确认 |
|---|---|---|
| **Node.js ≥ 22.5** | 全局 `WebSocket` / 内置 `fetch` / `node:sqlite` 都要它 | `node -v` → 打印 `v22.5.0` 或更高 |
| **npm** | 跟 Node 一起来 | `npm -v` |
| **抖音账号** | 建议**小号**：逆向协议违反抖音 ToS，有封号风险（[设计 §10 #4](../03-设计/抖音接入设计.md)） | —— |
| **手机能收码 / 能扫码** | 登录要人机交互，可能是扫码、短信验证码或滑块 | 手边有那个号的手机 |
| **一个 fork 的 commit 号** | `douyin-im` 用 `github:USER/douyin-im#COMMIT` 钉死版本 | 见下面 §1.1 第 ① 步 |

### 1.1 装依赖（这一步是人手工做的，助手不代劳）

```powershell
cd D:\Works\ZipperAIQQ\douyin-sidecar

# ① 把 package.json 里的 douyin-im 占位改成你的 fork，并钉死 commit（分支名会漂，出问题无法复现）
#    "douyin-im": "github:USER/douyin-im#COMMIT"  →  "github:<你的用户名>/douyin-im#<commit-sha>"
notepad package.json

# ② 装依赖（会去 GitHub 拉 fork，需要能访问 github）
npm install

# ③ 编译 TypeScript → dist/bridge.js
npm run build
```

> ⚠️ 本仓**不用** git submodule / vendor：CI 有 gitlink 守卫，且第三方源码不该塞进主仓
> （[sidecar README §1.1](../../douyin-sidecar/README.md)）。

---

## 2. 服务端先打开（不开就是握手 404）

### 2.1 两条路，选一条

**A. 客户端（推荐，运行时生效）**

「第三方平台接入」→ **抖音** Tab → 打开 **「启用抖音通道」** → 填好 token（§3）→ **保存抖音配置**。

保存后**不用重启服务端**：`/ws/channel` 每次请求都读最新配置
（`WsChannelEndpoint.cs:48`），sidecar 下一次重连就按新值判。

**B. 配置文件 `config/server.yaml`（首次部署 / 无客户端时）**

```yaml
douyin:
  enabled: true
  token: "<自己生成的随机串，见 §3>"
  sendTimeoutMs: 15000     # 出站等 ack 的超时
  maxTextLength: 16000     # 单条文本上限（超长自动分段，不截断）
  queryTimeoutMs: 10000    # query.contacts / query.groups 超时
```

> 📌 **优先级**：库里存的 > `server.yaml`（`DouyinOptionsService.cs:90`）。
> 也就是说：只要客户端把某一项写进过库（`douyin.*` 键一旦存在），再改 YAML 就**不生效** —— 要改用客户端改回去。

### 2.2 "关掉"到底是什么行为（别被 UI 文案带偏）

| UI 文案 | 实际行为（代码） |
|---|---|
| "启用抖音通道（关掉时 /ws/channel 不监听）" | 端点**在监听**，只是对 `/ws/channel` 一律回 **404**（`WsChannelEndpoint.cs:51-56`）—— 不泄露"这里有个端点" |

所以 sidecar 日志里出现 `404` 就是 `enabled=false`，不是网络问题。

### 2.3 各参数的准确含义

| 参数 | 默认 | 影响什么 | 出问题的表现 |
|---|---|---|---|
| `enabled` | false | 总开关；关掉后：连接 404、**入站帧直接丢**、出站抛"抖音接入已停用" | 连不上（404）/ 收不到消息 / 发不出去 |
| `token` | 空 | `/ws/channel` 的专用 token（§3） | 401 |
| `sendTimeoutMs` | 15000 | 每帧等 `ack` 的上限；超时抛 `抖音发送超时：15000ms 内没收到 ack（第 N/M 帧）` | 模型看到"发送超时" |
| `maxTextLength` | 16000 | 超长文本**按段发**（不截断，不切 emoji 代理对）；任一段失败 → 整体失败 | 长回复拆成多条 |
| `queryTimeoutMs` | 10000 | 「人员与名单」拉抖音好友/群候选的超时 | 候选列表空 + 超时提示 |

还有两个只在 sidecar 侧（`config.json`）的：`heartbeatIntervalMs`（30000）、`deadAfterMs`（90000，多久没收到任何帧就判定死连接并重连）。

---

## 3. token 怎么对（最容易错的一步）

### 3.1 生成一个随机串（PowerShell，5.1 / 7 都能跑）

```powershell
$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
```

得到一串 64 位十六进制（例如 `3f9c…`，**别用真值写进文档/聊天**）。
只用 ASCII、不带空格和引号，省得后面转义踩坑。

### 3.2 三处要共用同一个值

| 位置 | 填在哪 | 备注 |
|---|---|---|
| **Server** | 客户端「第三方平台接入 → 抖音 → **Sidecar Token**」<br>或 `config/server.yaml` 的 `douyin.token` | 保存后界面只显示 **"已配置（不回显；要换就重填，留空 = 不改）"** —— 明文永不回显 |
| **sidecar** | `douyin-sidecar/config.json` 的 `token`，或环境变量 `ZIPPER_DOUYIN_TOKEN`（优先） | 环境变量优先于文件，适合计划任务 / systemd |
| （可选，非 loopback 才用） | Server 的 `ZIPPER_HTTP_TOKEN` | 见 §3.4 |

### 3.3 sidecar 怎么把 token 发出去

两种都支持（`WsChannelEndpoint.cs:109-123`）：

```
ws://<server-ip>:5180/ws/channel?token=<token>        ← 当前 sidecar 默认走这个（query）
Authorization: Bearer <token>                        ← 也可（手改 sidecar 时用）
```

> 📌 为什么默认是 query：Node 内置 `WebSocket` 不支持自定义请求头，用 header 得额外引 `ws` 依赖
> （[sidecar README §2.2](../../douyin-sidecar/README.md)）。**token 会出现在 URL 里**，
> 所以 `bridge.ts` 打印 URL 时用 `redactToken()` 抹成 `token=***` —— 你自己截图/贴日志时也别忘了抹。

`config.json` 样例（**值全是占位符**）：

```json
{
  "serverUrl": "ws://<server-ip>:5180/ws/channel",
  "token": "<与服务端 Sidecar Token 完全一致的随机串>",
  "dataDir": "data",
  "maxTextLength": 16000,
  "heartbeatIntervalMs": 30000,
  "deadAfterMs": 90000,
  "logLevel": "info"
}
```

> ⚠️ `config.json` 与 `data/` 都在 `.gitignore` 里 —— **不要** `git add -f`，也不要外发。

### 3.4 鉴权的**准确**规则（这段要读完，不然后面排错会走弯路）

服务端对 `/ws/channel` 的判定是（`WsChannelEndpoint.cs:64-76`）：

```
放行 = HttpAccessPolicy 放行  OR  (douyin.token 非空 且 完全相等)
```

而 `HttpAccessPolicy` 只看**服务端自己的监听地址**（`HttpAccessPolicy.cs:22`）：

| 服务端监听 | `HttpAccessPolicy` | 结论 |
|---|---|---|
| `127.0.0.1:5180`（**默认**） | **免验**（谁连都放行） | **这层鉴权实际不生效**：`douyin.token` 配了也不参与判定 |
| `<server-ip>:5180`（非 loopback） | 必须带 `ZIPPER_HTTP_TOKEN`，否则**启动即失败** | 两条通行证：<br>① `Bearer <ZIPPER_HTTP_TOKEN>`（客户端 / 一切调用方）<br>② `<douyin.token>`（只给 sidecar 用的专用 token，不必暴露全局 token） |

**所以"未配置 = 不校验"的真实含义**：loopback 部署时这个端点本来就不校验；一旦把服务端暴露到
非 loopback，**必须**配 `ZIPPER_HTTP_TOKEN`（否则起不来），**并强烈建议**同时配 `douyin.token`，
让 sidecar 用专用 token 而不是全局 token。局域网也别裸奔 —— 能连上 5180 的人就能冒充 sidecar 往你库里灌消息喵。

> 说明：sidecar README §2 写的"loopback 部署可留空"与此一致；上表是把"为什么"摊开。

---

## 4. 启动与登录

### 4.1 启动顺序（重要）

**先登录抖音（可能阻塞到有人操作）→ 登录完成后才发 `hello` → 才连 Server。**
Server 没起来不致命：sidecar 会按 1s→2s→4s…封顶 60s（±20% 抖动）一直重连。

```powershell
cd D:\Works\ZipperAIQQ\douyin-sidecar

npm start            # 跑 dist/bridge.js（正式方式）
# 开发调试（免编译，改 TS 立即生效）：
npm run dev          # tsx src/bridge.ts
npm run typecheck    # 只做类型检查
```

### 4.2 登录发生在 **sidecar 的控制台**，不是客户端

抖音登录不能全自动（扫码 / 短信验证码 / 滑块）。sidecar 会往 **stdout** 打一段横幅，
里面有验证 URL：

```
========================================================================
【需要人工登录】抖音登录要人机交互（扫码 / 短信 / 滑块），sidecar 不能自己完成。
原因 / 提示：login.verify
验证地址：https://<某验证页>
远程部署时：把上面这个地址复制到本机浏览器打开，登录完成后 sidecar 会自动继续。
登录态保存在 data/ 目录 —— 该目录必须备份，且绝不入库。
========================================================================
```

**首次部署照着做**：

1. 用**带终端**的方式启动（`npm start`；或用计划任务/systemd 时去日志里抓 stdout）
2. 等这段横幅出现 → **把验证地址复制到你自己电脑的浏览器**，完成扫码 / 验证
3. 登录成功后 sidecar 自动继续，日志出现 `已连上 Server，hello 已发送`
4. 之后重启一般**免交互**（登录态在 `data/`）

> 远程宿主：SSH / 远程桌面里直接看终端；或用
> `Get-Content <日志> -Wait | Select-String 验证地址`（Windows 计划任务）／
> `journalctl -u zipperaiqq-douyin-sidecar -f`（Linux）。

### 4.3 登录态放哪、过期怎么办

| 项 | 说明 |
|---|---|
| 位置 | `douyin-sidecar/data/`（`dataDir` 可改；**相对启动时的工作目录**） |
| 里面是什么 | Cookie / 设备身份 / 会话票据 —— **拿到它基本等于拿到这个抖音号** |
| 备份 | 停机后整目录复制，例如 `Copy-Item data backup\data-2026xxxx -Recurse`；换机器整目录拷过去可免重登 |
| 过期 | 票据会过期（`markActive` 依赖真实用户活动，7×24 跑久了可能重新要求登录）→ **重走 §4.2**，日志里会再出现那段横幅 |
| 丢了 | 删掉 `data/` 重登即可（不影响 Server 侧数据） |
| ⚠️ 不要多开 | 同一个 `dataDir` 同时被两个进程用，登录态会互相踩 —— **先停旧进程再起新的** |

### 4.4 常驻守护

计划任务（Windows）/ systemd（Linux）的完整示例在
[sidecar README §3.1](../../douyin-sidecar/README.md) —— 直接照抄，把路径换成你自己的。
两个要点：

- **stdout 与 stderr 都要收日志**：交互提示走 stdout，诊断日志走 stderr
- 崩溃重启后**不需要重新登录**（登录态在 `data/`），也不需要重启 Server（自动重连）

---

## 5. 怎么判断成功（三处证据，缺一不可）

### ① sidecar 控制台

```
[INFO] 正在连接 ws://127.0.0.1:5180/ws/channel?token=***
[INFO] 已连上 Server，hello 已发送
```

（URL 里的 token 被抹成 `***` 是正常的。）

### ② 客户端

「第三方平台接入」页**顶部「通道」卡片**里，`抖音` 那一条会变成：

```
抖音  已连入（selfId=<你的抖音 uid>）
```

其他状态的含义（`PlatformsViewModel.cs:381-387`）：

| 显示 | 含义 |
|---|---|
| `已连入（selfId=…）` | 握手完成，`hello` 里带了账号 uid ✅ |
| `已连入` | 连上了，但 `hello` 没带 selfId（登录态可能不完整） |
| `未连入（等待 sidecar）` | 服务端已启用，但没有任何 sidecar 连进来 |
| `未启用` | `douyin.enabled = false` |
| `状态未知（旧服务端）` | 客户端比服务端新，`getPlatforms` 没返回抖音块 |

抖音 Tab 里的状态卡会同时显示 `sidecar 已连入 · selfId <uid>` / `未连入（sidecar 还没连上来 / 登录态可能过期）`。

### ③ 服务端日志

文件：`logs/zipper-YYYYMMDD.log`（相对**服务端工作目录**；同时输出到控制台）。连上时应出现：

```
[INF] 抖音配置已加载：启用=是 token=已配置 白名单 0 条
[INF] sidecar 已连入 /ws/channel（remote=127.0.0.1）
[INF] 抖音 sidecar 已连接
[INF] 抖音自身账号已确认：<uid>
```

断开时会看到 `sidecar 已断开 /ws/channel`；重连会看到 `抖音 sidecar 重连：替换旧连接`。

### ④ （可选）命令行自查

```powershell
$body = @{ action = "getPlatforms"; payload = @{} } | ConvertTo-Json -Depth 5
$r = Invoke-RestMethod -Uri 'http://127.0.0.1:5180/api' -Method Post `
        -ContentType 'application/json' -Body $body
$r.data.douyin | Format-List
```

预期 `connected : True`、`selfId : <uid>`、`tokenConfigured : True`、`endpoint : /ws/channel`。

> 服务端监听在非 loopback 时，`POST /api` 也要带全局 token（`/api` **只认** header）：
> `-Headers @{ Authorization = 'Bearer <ZIPPER_HTTP_TOKEN>' }`。

---

## 6. 第一次真机自测（三步，别跳过）

1. **名单**：「人员与名单」→ 平台选 **抖音** → 把要用的会话加进白名单（格式与取值见 §7.2 的第 2 条）
2. **让它收**：用另一个号给这个抖音号发一条私聊（或群里 @ 一下）
3. **看回复**：AI 回复出现在抖音；同时客户端会话列表里多出 `douyin:p<uid>` / `douyin:g<gid>` 会话

---

## 7. 排错表

> 先说一个**通用坑**：通道层"丢弃"类日志是 **Debug** 级，而服务端最低级别写死
> `Information`（`ObservabilityRegistration.cs:42`）—— 默认**看不到**"因为名单/未映射而丢弃"的记录。
> 排查时优先看 **sidecar 的** `logLevel: "debug"`，以及"消息数量对不上"这个现象本身。

### 7.1 连不上

| 现象 | 多半是 | 怎么办 |
|---|---|---|
| 握手就 `404` | 服务端 `douyin.enabled = false`（关掉时端点直接 404） | 打开开关（§2.1）；**不用重启服务端**，sidecar 会自己重连上来 |
| `401` / 握手失败 | token 不一致（多了空格 / shell 转义 / 一边改了另一边没改） | 对齐 §3.2 那三处；注意客户端"留空 = 不改"，**清空 token 要显式清** |
| `ECONNREFUSED 127.0.0.1:5180` | 服务端没起 / 端口不是 5180 | 先起 Server；或改 sidecar 的 `serverUrl`（端口见 `server.yaml` / `ZIPPER_HTTP_ADDR`） |
| 一直 `重连（第 N 次）`，退避涨到 60s | 上面某一条 | 修好之后**自动恢复**，不用重启 sidecar |
| 远程机器连不上 | 防火墙 / 服务端只听 `127.0.0.1` | 服务端监听改到 `<server-ip>`（此时**必须**配 `ZIPPER_HTTP_TOKEN`，见 §3.4）+ 放行 5180 |
| sidecar 起不来，`配置文件解析失败` / `ZIPPER_DOUYIN_CONFIG 指向的文件不存在` | `config.json` 不存在或不是合法 JSON；`ZIPPER_DOUYIN_CONFIG` 指错 | 修 config.json；注意 `config.json` 是**相对启动时工作目录** |

### 7.2 连上了，但收不到消息

按顺序查（前两条占九成）：

1. **服务端 `enabled` 是不是真的开着** —— 关掉时入站帧**在通道层直接丢**（`DouyinChannel.cs:418`），不落库、不进模型。
2. **名单过滤（当前权威口径：人员与名单）**
   - 配置位置：「人员与名单」→ 平台选 **抖音** → 白名单 / 黑名单（存 `access.douyin.whitelist` / `blacklist`）
   - 判定顺序：**主人范围放行 → 黑名单拒绝 → 白名单非空则只收名单内 → 其余放行**（`ChatFilter.cs:151-170`）
   - ⚠️ **匹配是整条 SessionId 精确比对**（`douyin:p<uid>` / `douyin:g<gid>`）—— **不支持 `douyin:p*` 这种通配**，写进去只会是一个永远匹配不上的字面量（该提示文案待修）
   - 两份名单都空 = 全放行；**白名单非空 = 只收名单内的**（deny-by-default）
3. **sidecar 登录态**：`data/` 过期 → 消息根本不进来。看 sidecar 日志有没有那段"需要人工登录"横幅（§4.2）。
4. **事件名 / 字段不匹配**（`douyin-im` 未验收项）：sidecar 侧 `logLevel: "debug"`，若只刷"忽略非消息事件"就是这类（[README §8 #4 #5](../../douyin-sidecar/README.md)）。
5. **陌生人盒消息**：`message.stranger` 按设计**直接丢弃**（Debug 记录），不是 bug。
6. **`allowedSessions` 字段是遗留**：「第三方平台接入 → 抖音 → 允许的会话」这个输入框在 2026-10-28 之后**不再参与收发判定**（名单统一到"人员与名单"）；填它不会生效，别在它身上找原因。

### 7.3 能收不能发 / 发出去没反应

| 现象 | 多半是 | 怎么办 |
|---|---|---|
| 模型说"发送超时" | `sendTimeoutMs` 内没等到 `ack`（sidecar 没处理 / 断线 / `id` 对不上） | 看 sidecar 日志与 `ack.ok`；先把 `sendTimeoutMs` 调大确认不是慢 |
| 模型说"抖音接入已停用" | `enabled=false` | 打开（§2.1） |
| 模型说"抖音通道只能发送 douyin 会话" | 路由 bug（把 QQ 会话发给抖音通道） | 报 bug，别绕过 |
| `ack.ok=false` + `conversation-risk` | 抖音风控拒发（平台行为，不是 bug） | 换措辞 / 降频 |
| `ack.ok=false` + "没有 sendMessage()/uploadImage()" | fork 的 `douyin-im` 缺该方法 | 按 [README §8 #6/#7/#8](../../douyin-sidecar/README.md) 核对 fork 版本 |
| 主动发给"从没收到过消息的会话"失败 | `conversationShortId` 缓存里没有（未知是否必需） | 先让对方发一条，让缓存就位（§8 第 7 条） |
| 长回复被拆成多条 | `maxTextLength`（默认 16000）分段——**预期行为**，不是 bug | 嫌碎就调小/调大 |

---

## 8. 未真机验收清单（联调时逐条确认）

> **一句话前提**：`douyin-im` 的所有接口都是照着 README / 源码写的，
> **没有任何一条跑过真实账号**（[设计 §11 前言](../03-设计/抖音接入设计.md)）。
> 下面 8.1 是设计文档的清单，8.2 是 sidecar 实现层的补充；真机联调时**逐条打勾**。

### 8.1 设计 §11.3 的 10 条（协议 / 行为层）

| # | 待确认 | 不匹配时的现象 |
|---|---|---|
| 1 | 客户端导出名（`createClient` / `DouyinClient` / default）与构造参数形状 | 启动即报"没有导出客户端工厂" |
| 2 | `start()` / `login()` 的方法名与返回值 | 日志 warn"跳过自动登录"，登录不发生 |
| 3 | 事件订阅名（`message` / `message.private` / `message.group`…）与事件字段（`threadId` / `senderUid` / `serverMessageId` / `content.kind` / `mentions` / `referenceInfo`） | **收不到消息**；或消息缺字段、`messageId:null` |
| 4 | `sendText({threadId, conversationShortId, text, mentions})` / `sendMessage(...)` / `uploadImage()` | `ack.ok=false`（方法不存在） |
| 5 | `getFriendList()` / `getGroupList()` | 「人员与名单」候选为空 + `result.ok=false` |
| 6 | 登录验证 URL 的字段名（`verifyUrl` / `url` / 二维码） | 登录横幅**不出现**（看不到验证地址） |
| 7 | `conversationShortId` 是否必需 | **主动**发给"从未收到过消息"的会话可能失败 |
| 8 | 群 `@` 要求数字 uid | 不报错但 @ 不生效（已降级纯文本） |
| 9 | `conversation-risk` 拒发 | 消息发不出；原因已回填模型（不静默） |
| 10 | 票据过期掉线（`markActive` 依赖真实用户活动） | 7×24 稳定性未知，需重登（§4.3） |

### 8.2 sidecar README §8 的 14 条（实现层补充）

| # | 假设 | 不匹配时的现象 |
|---|---|---|
| 1 | 导出客户端工厂的三种写法轮试 | 启动即报"没有导出客户端工厂" |
| 2 | 构造参数（`{dataDir}` / 位置参数 / `new`）轮试 | 启动即报"创建 douyin-im 客户端失败" |
| 3 | `start` / `login` / `connect` / `init` 候选名 | warn"跳过自动登录" |
| 4 | 事件名一次全订 | **收不到消息**，debug 里只有"忽略非消息事件" |
| 5 | 事件字段多别名容错 | 消息内容缺字段、`messageId:null` |
| 6 | `sendText` 纯文本路径 | `ack.ok=false`（改走 `sendMessage`） |
| 7 | `sendMessage`（图片 / 引用路径） | `ack.ok=false`："没有 sendMessage()" |
| 8 | `uploadImage()` | `ack.ok=false`："没有 uploadImage()" |
| 9 | `getFriendList()` | `result.ok=false` + 空列表 |
| 10 | `getGroupList()` | 同上 |
| 11 | 登录验证事件里的 URL 字段探测 | 登录时**看不到验证 URL** |
| 12 | `conversationShortId` 缓存（入站学、出站用） | 主动发给陌生会话失败 |
| 13 | 群 `@` 非数字 uid 降级 | 不报错但 @ 不生效 |
| 14 | `conversation-risk` → `ack.error` | 消息发不出，但模型能看到原因 |

> 📌 有些地方把这份清单记作「13 项」（`docs/07-记忆/进度.md`、`多平台接入实施设计.md` §S3-E）——
> 指的是同一批假设的口径差异。**以本节 8.1（10 条）+ 8.2（14 条）为准**，逐条过完就算真机验收。

---

## 9. 安全与合规（别踩）

- ⚠️ **封号风险**：逆向协议违反抖音 ToS —— **用小号**、**低频**，风险自担（[设计 §10 #4](../03-设计/抖音接入设计.md)）
- 🚫 **`data/` 是账号级机密**：永不入库（已 gitignore）、永不外发、不贴聊天/Issue/截图
- 🚫 **token 不进仓库、不进日志、不回显**：`config.json` 已 gitignore；日志里 URL 抹成 `token=***`
- 🚫 **不要把完整连接 URL 贴到聊天/工单里**（带了 token）
- ✅ sidecar 只连三样：本机（或你指定的）Server 的 `/ws/channel`、抖音服务、以及**出站图片 URL**
- ✅ 它**不监听任何端口**（反向连接的意义就在这里）

---

## 附：文件地图（改代码前先知道谁是谁）

| 路径 | 干什么 |
|---|---|
| `douyin-sidecar/README.md` | sidecar 全部操作细节（安装 / 配置 / 登录 / 帧 / 排错） |
| `douyin-sidecar/src/bridge.ts` | 协议实现本体（翻译层 / 适配层 / 连接层） |
| `douyin-sidecar/config.json` · `data/` | 本地配置（含 token）· 登录态 —— **都不入库** |
| [`ZipperAIQQ.Server/External/WsChannelEndpoint.cs`](../../ZipperAIQQ.Server/External/WsChannelEndpoint.cs) | `/ws/channel`：404 / 401 / 双向 token 取法 / 1 MB 帧上限 |
| [`ZipperAIQQ.Server/Channels/DouyinChannel.cs`](../../ZipperAIQQ.Server/Channels/DouyinChannel.cs) | 连接替换、帧分发、`hello`→selfId、出站 ack 配对、名单拦截 |
| [`ZipperAIQQ.Server/Channels/DouyinOptions.cs`](../../ZipperAIQQ.Server/Channels/DouyinOptions.cs) | 配置形状与默认值 |
| [`ZipperAIQQ.Server/Channels/DouyinOptionsService.cs`](../../ZipperAIQQ.Server/Channels/DouyinOptionsService.cs) | 配置读写（库 > YAML）、token 加密落库、热更新 |
| [`ZipperAIQQ.Server/Api/Handlers/PlatformActions.cs`](../../ZipperAIQQ.Server/Api/Handlers/PlatformActions.cs) | `getPlatforms` / `saveDouyin`（客户端页面的后端） |
| [`ZipperAIQQ.Client/Views/PlatformsPage.xaml`](../../ZipperAIQQ.Client/Views/PlatformsPage.xaml) | 「第三方平台接入」页（状态卡 + 抖音配置卡） |

### 本文依据的源码位置（便于以后核对）

| 结论 | 依据 |
|---|---|
| 关掉 `enabled` → 404；每次请求读最新配置 | `WsChannelEndpoint.cs:48-56` |
| token 取法：Bearer / `?token=`；401 判定 | `WsChannelEndpoint.cs:64-76`、`:109-123` |
| loopback 免验 / 非 loopback 强制 `ZIPPER_HTTP_TOKEN` | `HttpAccessPolicy.cs:18-54` |
| 默认端口 5180 | `ServerOptions.cs:72` |
| 库 > YAML；token 加密；热更新 | `DouyinOptionsService.cs:90-122`、`:137-226` |
| 入站：停用丢弃 → 解析 → 名单拦截 | `DouyinChannel.cs:414-445` |
| 名单判定顺序与"精确匹配无通配" | `ChatFilter.cs:151-170` |
| 发送超时文案 / 未连接抛异常 | `DouyinChannel.cs:533-543` |
| `hello` → selfId → 客户端显示 | `DouyinChannel.cs:392-410`、`PlatformsViewModel.cs:526-531` |
| 顶部「通道」chip 文案 | `PlatformsViewModel.cs:381-387` |
| 日志级别写死 Information；日志文件路径 | `ObservabilityRegistration.cs:42`、`:52-62` |
| sidecar 连接 / 重连 / 登录横幅文案 | `bridge.ts:1237`、`:1273`、`:1315`、`:146` 附近 |
