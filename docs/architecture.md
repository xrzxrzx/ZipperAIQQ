# 架构说明（贡献者版）

> **这份文档面向贡献者**：讲清「它怎么分层」和「你为什么不能随便跨层」。
>
> ⚠️ 更详细的设计文档属于**项目内部资料，不随本仓库公开** —— 所以这里**只讲结论与约束**，
> 不讲内部权衡过程。想要更深的背景，可以直接问 maintainer。

---

## 1. 这个项目是什么

一个**对接 QQ 与 LLM 的中间件**。服务端要连三样东西：

| 连接对象 | 协议 | 说明 |
|---|---|---|
| **NapCat** | OneBot 11 over WebSocket | 负责和 QQ 打交道；正向 WS（我们做客户端）与反向 WS（我们做服务端）**双支持** |
| **LLM** | OpenAI 兼容 HTTP API | **不绑死某一家**——只要兼容 OpenAI 格式，换端点只改配置 |
| **管理客户端** | HTTP（命令）+ WebSocket（事件） | WPF 桌面程序，看状态 / 看日志 / 发消息 |

**一句话**：QQ 消息进来 → 交给 agent（带工具、带上下文）→ 回复发回 QQ；
同时把「正在想什么、调了什么工具」实时推给桌面客户端。

---

## 2. 分层与七个项目

```
ZipperAIQQ.sln
├── src/
│   ├── ZipperAIQQ.Core/                  # ⓪ 进程内公共基础设施（配置 / 日志）
│   ├── ZipperAIQQ.Transport/             # ① WebSocket 传输封装（★服务端 + 客户端复用）
│   ├── ZipperAIQQ.Agent/                 # ② AI 编排（★最核心）
│   ├── ZipperAIQQ.Channels.NapCat/       # ③ NapCat / OneBot 11 通道
│   ├── ZipperAIQQ.Storage/               # ④ SQLite 持久化
│   ├── ZipperAIQQ.Server/                # ⑤ 服务端宿主（Kestrel，HTTP + WS）
│   └── ZipperAIQQ.Client/                # ⑥ WPF 客户端（⚠️ 唯一 Windows-only）
└── tests/
    ├── ZipperAIQQ.Core.Tests/
    ├── ZipperAIQQ.Transport.Tests/
    ├── ZipperAIQQ.Agent.Tests/
    ├── ZipperAIQQ.Channels.NapCat.Tests/
    └── ZipperAIQQ.Server.Tests/
```

| # | 项目 | 职责 | 关键类型 |
|---|---|---|---|
| ⓪ | **Core** | **横切关注点**（所有模块都要用的背景设施）：配置装载 / 校验 / 原子保存，日志装配，**进程内事件总线**。**⚠️ 准入规则见下** | `AddZipperConfiguration`、`IStartupCheck`、`IReloadable`、`IEventBus`、`LogSafe` |
| ① | **Transport** | WebSocket 的**通用能力**：建连、收发、分帧、心跳、重连、背压。**接口用 `JsonNode`，与业务无关** | `IWebSocketTransport` + 服务端 / 客户端两个适配器 |
| ② | **Agent** | AI 编排：**自动工具调用循环** + 会话隔离、权限门禁、事件流、上下文压缩、重试 | `IAgentRuntime`、`AgentEvent`、`GuardedAIFunction`、`IToolRegistry`、`ISessionStore` |
| ③ | **Channels.NapCat** | OneBot 11 协议：事件解析、API 调用、echo 请求-响应配对。**QQ 通道抽象也在这里** | `IQQChannel`、`NapCatChannel`、`OneBotEventParser`、`DTO/` |
| ④ | **Storage** | 会话、消息、记忆、配置的持久化 | `SqliteStore`、`SessionRepository` |
| ⑤ | **Server** | 宿主：DI 装配、Kestrel 端点（`/api` + `/events`）、消息编排、主动行为调度 | `Program.cs`、`ApiEndpoint`、`EventBroadcaster`、`PermissionGate`、`DTO/` |
| ⑥ | **Client** | WPF 界面（MVVM）。**自己维护一份同名 DTO** | `Views/` `ViewModels/` `Services/` `DTO/` |

> ### ⚠️ `Core` 的准入规则（**放东西进去之前请先读**）
>
> **两条都要满足：**
>
> **判据一 —— 能说出第二个消费者是谁。** 说不出来就别放。
>
> **判据二 —— 它是「横切关注点」，不是「运行时能力」。**
>
> | | 是什么 | 例子 | 进 Core？ |
> |---|---|---|---|
> | **横切关注点** | **每个模块都要用**，本身**不是功能**，是所有功能都需要的背景设施 | 配置、日志、事件总线 | ✅ |
> | **运行时能力** | **某些模块为了干某件事**才需要，它本身**是一个功能** | `Transport`（连网络）、`Storage`（存数据） | ❌ |
>
> > ⭐ **最准的一句判据**：**「把所有业务模块删光，这东西还需要吗？」**
> > 配置要、日志要、事件总线要；**WebSocket 连接和数据库就不要了** —— 所以后两个**不进 Core**。
>
> | ✅ 可以放 | ❌ 不许放 |
> |---|---|
> | 跨模块共用的**横切设施** | 有业务语义的东西（会话、消息、人格…） |
> | **无业务语义**的抽象与薄适配 | 只服务单一模块的代码 |
> | 通用守卫 / 结果类型（出现 ≥2 个消费者 + 是横切） | **跨进程 wire 契约**（HTTP / WS 的 DTO —— 归各模块自己的 `DTO/`） |
>
> **为什么定这条**：`Core` / `Common` 这类项目**天然会变成垃圾桶** —— 谁都觉得"这个挺公共的"就塞进来。
> 没有准入规则，半年后它会依赖所有人、也被所有人依赖，改一行炸一片。
>
> （**2026-09-22 补记**：判据二是后加的一条 —— 原本只有判据一，结果发现 `Transport` 也能通过，规则太宽。）

> 📌 **为什么没有 `ZipperAIQQ.Contracts` 项目**：客户端与服务端**各自维护 DTO**，
> 一致性靠 [`docs/contracts/`](contracts/) 的样例 + **两端各写一个断言测试**来保证（"共享数据，不共享代码"）。
> 代价是编译器帮不上忙，收益是省掉一层最容易写漏的映射代码。

---

## 3. ⭐ 三个不可破坏的接缝

这三条是这个架构**存在的理由**。改动时如果发现自己在绕过它们，**先停下来开 issue 讨论**。

### 接缝 A：表现层 ↔ 编排层 —— 必须走 HTTP / WS

**不许进程内直连。**

> **为什么**：这个项目有两种部署形态 —— ① **本机单体**（客户端和服务端同一台机器）
> ② **服务端分离**（服务端跑在别处）。
> 两者**共用同一套代码**，靠的就是"A 永远走网络"。
> 一旦有人为了"省一次序列化"直连进去，形态 ② 立刻坏掉，而且是**运行期才发现**。

### 接缝 B：QQ 通道 ↔ 编排层 —— 必须经 `IQQChannel`

**上层不得直接依赖 NapCat / OneBot 的类型。**

> **为什么**：NapCat 是**第三方协议端**，它会变、也可能被换掉。
> `Agent` 只需要知道"有消息进来、有事件出去"，**它不该知道 QQ 的存在**。

### 接缝 C：工具执行 → 权限门禁 —— 门禁在服务端，默认仅管理员

**危险操作（执行命令、写 / 删文件、访问外部服务）默认只有管理员能触发。**

> **为什么**：LLM 是不可信输入源 —— 群友可以让它"帮忙删个临时文件"。
> 所有工具一律经 `GuardedAIFunction` 包装，在那里做**权限判定 / 超时 / 异常隔离 / 输出限长**。
> ⚠️ **不许存在绕过门禁的注册路径**（包括以后接 MCP 工具）。

---

## 4. 依赖方向（硬约束）

```
  第 0 层（零依赖）      ⓪ Core              横切关注点：配置 / 日志 / 事件总线
                        ① Transport          WebSocket 封装（接口用 JsonNode，与业务无关）

  第 1 层                ③ Channels.NapCat   ④ Storage
                         （③ 依赖 ①）

  第 2 层                ② Agent（不依赖任何内部业务项目；可用 ⓪ Core 的基础设施）

  第 3 层（组合根）      ⑤ Server（→ 全部）       ⑥ Client（→ ① Transport）
```

**五条不可破坏的约束**：

1. **`Core` 不得引用任何其他内部项目** —— 它是基础设施层，引用任何业务项目都会立刻循环
2. **除 `Client` 外，任何项目都不得引用 WPF / Windows 专有程序集**
   ⟶ 这是「**服务端跨平台**」的物理保障。**Linux 上 `dotnet build` 必须过。**
3. **`Agent` 不得依赖 `Channels.NapCat`** —— agent 不知道 QQ 的存在
   （但它**可以**依赖 `Core`：配置与日志属基础设施，不算内部业务依赖）
4. **上层不得依赖 `Transport` 的具体实现**（服务端 / 客户端两个适配器），只依赖 `IWebSocketTransport`
5. **客户端与服务端各自维护 DTO** —— 契约一致性**靠纪律保证，不能靠编译器**

> ⚠️ **第 4 条是这个项目最容易踩的坑**：客户端和服务端各写一份同名 DTO，改了一边，
> **编译期不会提醒另一边**（契约是 JSON，跑到那一步才发现）。
> **补法**：[`docs/contracts/`](contracts/) 里的 JSON 样例 + **两端各写一个断言测试**。
> 改契约的正确顺序是**先改样例** → 两个测试同时红 → 强制两端同步。

---

## 5. 通信契约

**职责分工**：HTTP **只发命令**（客户端主动，请求-响应）；WebSocket **只收事件**（服务端主动推送）。**两者不重叠。**

```jsonc
// ① HTTP 命令：POST /api
//    Header: Authorization: Bearer <token>   ← 仅当监听非 loopback 时要求
请求  { "action": "<动作名>", "payload": { ... } | null }
响应  { "code": 0, "message": "ok", "data": { ... } | null }

// ② WebSocket 事件：WS /events 上的每一帧
{ "type": "<事件类型>", "seq": <int64>, "time": <int64 unix ms>, "data": { ... } | null }
```

- **不用 REST 路径风格**（没有 `GET /sessions/{id}` 这种），一律 `POST /api` + payload 里的 `action`
- `code = 0` 表示成功，非 0 为业务错误（`message` 带说明）
- `seq` **服务端单调递增**，客户端用它判断是否丢帧
- **鉴权**：loopback 免验；**非 loopback 必须配 token，未配则拒绝启动**

**权威清单**：[`docs/contracts/`](contracts/) 里的样例文件 + 那份 README。

---

## 6. 一条 QQ 消息的旅程

```
QQ 用户发消息
   │
   ▼
[NapCat] ──OneBot 11 WS──▶ ③ NapCatChannel
   │
   ▼
⑤ Server 的消息编排
   ├─ 解析：谁发的、群还是私聊、@ 了没
   ├─ 定位会话：SessionId = "g<群号>" 或 "p<QQ号>"
   ├─ 权限判定：QQ 号 → 角色
   └─ 触发主动 / 被动逻辑
   │
   ▼
② IAgentRuntime.RunAsync
   ├─ 取该会话的上下文（超预算先压缩）
   ├─ 组装 system(人格) + 历史 + 新输入
   └─ 工具循环：LLM ⇄ 工具
        ├─ 每轮 LLM 调用（失败会重试）
        ├─ 自动发起工具调用 → 经 GuardedAIFunction 门禁
        └─ 产出 AgentEvent 流
   │
   ├────────────▶ （旁路）⑤ EventBroadcaster ──WS /events──▶ ⑥ WPF 客户端
   │                实时展示：思考 / 工具调用 / 回答
   ▼
⑤ Server 拿最终文本
   │
   ▼
③ NapCatChannel 调用发送 API
   │
   ▼
[NapCat] ──▶ QQ 用户收到回复
```

---

## 7. 技术栈

| 项 | 选择 | 备注 |
|---|---|---|
| 语言 / 运行时 | **C# / .NET 10**（`net10.0`，LTS） | 除 `Client` 外必须能在 Linux 构建 |
| 服务端宿主 | `Microsoft.Extensions.Hosting` + ASP.NET Core (Kestrel) | 同进程内提供 HTTP + WS |
| 客户端 | **WPF** + `CommunityToolkit.Mvvm` | 仅表现层 |
| IoC | `Microsoft.Extensions.DependencyInjection` | **禁止静态单例、禁止服务定位器** |
| 配置 | `Microsoft.Extensions.Configuration` + `IOptions<T>` | 单一入口；机密只走环境变量 / 本地覆盖文件 |
| 日志 | **Serilog**（容器里注册 `Serilog.ILogger`，直接注入） | 结构化；**写盘前脱敏** |
| AI | **MEAI（`Microsoft.Extensions.AI`）打底 + 部分自研** | 自动工具循环用 `FunctionInvokingChatClient` |
| JSON | **`System.Text.Json`**（全项目统一） | 与 MEAI 同栈；**不要引入第二个 JSON 库** |
| 存储 | SQLite | |
| 测试 | xUnit | |

> 完整技术基线见 [`AGENTS.md`](../AGENTS.md) 第二节。**改基线要先讨论。**

---

## 8. 我要改 XX，该动哪里？

| 你想做的事 | 动哪里 | 注意 |
|---|---|---|
| 加一个**给 AI 用的工具** | `Agent/Tools/` 新增一个类 + 一行注册 | 🟢 **最适合新手**；`description` 要写"**何时使用**"，不是复述工具名 |
| **加一个配置项** | 对应模块的 `XxxOptions` + 在 `Server` 的配置骨架里加默认值 | ⚠️ **含密的项绝不许写进 `appsettings.json`**（那文件进仓库）；并在日志里**脱敏** |
| 加一个 **HTTP 命令** | `Server/ApiEndpoint` + `Server/DTO/` + **`Client/DTO/`** | ⚠️ 别忘了 `docs/contracts/` 加样例 |
| 加一个 **WS 事件** | `Server/EventBroadcaster` + 两端 DTO | 同上 |
| 改 **QQ 协议相关** | `Channels.NapCat/` | 别把 OneBot 类型泄到上层 |
| 改 **AI 行为 / 人格 / 上下文策略** | `Agent/` | 会话隔离与压缩策略改动**影响面大，先开 issue** |
| 改 **桌面界面** | `Client/` | 🟡 中等门槛；VM 要能单测（不依赖 UI 类型） |
| 改 **WebSocket 收发 / 心跳 / 重连** | `Transport/` | 🔴 **高危**：六个坑（分帧、心跳、背压、并发发送、关闭握手、重连），**先讨论** |
| 改 **权限门禁** | `GuardedAIFunction` / `PermissionGate` | 🔴 **安全相关，必须 maintainer review** |
| 改 **日志 / 配置装载** | `Core/` | 🔴 **安全相关**（脱敏、机密回显都在这里）；且先读 §2 的**准入规则** |
| **想往 `Core` 加东西** | —— | ⚠️ **先说出第二个消费者是谁**，说不出来就别放（见 §2） |

**危险区清单**见 [`CONTRIBUTING.md`](../CONTRIBUTING.md)。

---

## 9. 术语表

| 词 | 含义 |
|---|---|
| **NapCat** | 第三方 QQ 协议端（**非官方**），对外暴露 OneBot 11 接口 |
| **OneBot 11** | 一套 QQ 机器人应用层协议：事件用 WS 推、调用用 WS/HTTP 发，靠 `echo` 字段配对请求-响应 |
| **正向 / 反向 WS** | 正向 = 我们主动连 NapCat；反向 = NapCat 主动连我们 |
| **MEAI** | `Microsoft.Extensions.AI`，微软的 AI 抽象层（`IChatClient` 等） |
| **`FunctionInvokingChatClient`** | MEAI 提供的中间件：**自动完成"调 LLM → 执行工具 → 回填结果 → 再调 LLM"这个循环** |
| **接缝（seam）** | 见 §3 —— 三条不许绕过的边界 |
| **会话（Session）** | 一段独立的对话上下文，`SessionId` 形如 `g<群号>`（群）或 `p<QQ号>`（私聊） |
| **门禁（Gate）** | 工具执行前的权限判定，见接缝 C |
| **`Core`** | 横切关注点项目（配置 / 日志 / 事件总线）。**有准入规则**，见 §2 |
| **横切关注点** | 每个模块都要用、本身不是功能的背景设施（配置、日志、事件总线） |
| **事件总线** | 进程内发布订阅（`IEventBus`）。让 `Channels.NapCat` 发 QQ 事件时**不需要知道谁在听** |
| **Options** | `XxxOptions` 类，一个配置节的强类型映射；**跟着它的消费者模块走**，不集中在 `Core` |
| **原子写** | 先写临时文件再替换，避免"写一半崩了把配置写坏" |
