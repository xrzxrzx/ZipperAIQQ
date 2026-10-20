# Transport 实现指南（甲方案：Kestrel + 共用核心）

> **定位**：本文是 [`transport-design.md`](transport-design.md) 的**落地指南** —— 把"结构怎么定"变成"代码怎么写、坑在哪"。
> **依据**：[`transport-design.md`](transport-design.md) §2（复用切口）、[`overview.md`](overview.md) §2.3（依赖约束）、[`composition-design.md`](composition-design.md) §4（注册与生命周期）
> **状态**：待用户确认（含 §12 待决策项）
> 起草：2026-10-01

---

## 0. 一句话结论

> **服务端 WS 由宿主（Kestrel）接受，把现成的 `WebSocket` 交给 Transport；**
> **`Attach` 的实现放在<u>共用基类</u> `WebSocketDuplexBase` 里，两个子类各写一遍是错的。**

---

## 1. 为什么是「甲（Kestrel）」而不是「乙（HttpListener）」

### 1.1 设计原本就写的是 Kestrel

`transport-design.md` §2 第 42 行**明确写着**：

```csharp
WebSocket socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
```

是 **`HttpContext`**（ASP.NET Core），不是 `HttpListenerContext`。

> ⚠️ **一条自我修订**：助手曾在评审中说"这条是我文档没说清" —— **不准确**。
> 细节文档一直是准的；只有 `overview.md` §3.2 那个被压缩过的**摘要**写得含糊（只写了"服务端 `AcceptWebSocketAsync()`"）。
> **所以改用 Kestrel 不是"改设计"，而是"回到设计原本的样子"。**

### 1.2 官方明确不推荐 `HttpListener` 用于新开发

微软官方文档（[HttpListener 类](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener)）：

> ⚠️ **We recommend that you don't use the `HttpListener` class for new development.** `HttpListener` receives only **limited servicing**, and its **behavior and protocol support vary by platform**... on Windows it's built on `HTTP.sys`, while **on Linux and macOS it uses a managed implementation that doesn't support HTTPS** and offers a narrower set of features. **Use ASP.NET Core instead**...

> ⚠️ **Top-level wildcard bindings (`http://*:8080/` and `http://+:8080`) should not be used.** ... can open up your app to security vulnerabilities.

> 💡 **一条已证的澄清**：`HttpListener` 在 Unix 上**确实支持 WebSocket**（`Managed/WebSockets/HttpWebSocket.Managed.cs` 存在，不是 `PlatformNotSupported`）。
> **问题不在"能不能跑"，而在"行为随平台而异 + 不支持 HTTPS + 官方不维护"** —— 而本项目有「服务端必须 Windows + Linux 兼容」的**硬约束**。

### 1.3 两者对比

| 维度 | 乙（`HttpListener`） | **甲（Kestrel）** |
|---|---|---|
| **Transport 拥有什么** | 监听器 **+** 连接 | **只有连接** |
| 进程里几个 HTTP 服务器 | **2 个**（Kestrel 管 `/api`、HttpListener 管 `/events`） | **1 个** |
| 端口 | 两个 | 一个 |
| **鉴权（token 校验）** | ❌ **不覆盖 WS** | ✅ 中间件统一生效 |
| 限流 / 日志 / 压缩 / CORS | ❌ 都要另做 | ✅ 白拿 |
| 跨平台 | ⚠️ Windows=HTTP.sys，Linux/macOS=托管实现（**无 HTTPS**） | ✅ 一致 |
| **能否给 Client 复用** | ❌ **不能**（见 §3） | ✅ **能，这就是它的价值** |
| `Transport` 的依赖 | 引 `System.Net.HttpListener` | ✅ 只要 BCL 的 `System.Net.WebSockets` |
| **"重连"** | 🔴 难（监听器与连接挤在一个对象里） | ✅ 简单（见 §7.3） |

---

## 2. 职责边界

| 谁 | 负责 |
|---|---|
| **宿主**（`Server` 的 `Program.cs`） | 建 Kestrel、`UseWebSockets()`、映射端点、**接受 WS**、**鉴权与中间件** |
| **`WebSocketDuplexBase`**（Transport 内，abstract） | 拿到 `WebSocket` 之后的**一切**：分帧、消息上限、**发送串行化**、心跳、背压、状态事件、关闭顺序、`Dispose` |
| **`ServerSideTransport`** | 只多一件事：**收到消息后做 API 分发**（`IServerTransport` 特有） |
| **`ClientSideTransport`** | 只多一件事：**建连 + 重连退避** |
| **`Channels.NapCat`** | **OneBot 语义**：`action` / `echo` / `post_type` 的解析（通过注入委托） |
| **`Server` / `Client` 的组合根** | 决定"每个连接 new 一个 transport 实例"还是复用（见 §12 D1） |

---

## 3. ⭐ 为什么必须有一个共用基类（**"放 Server 里 Client 就复用不了"的正解**）

### 3.1 问题

如果 `Attach` 的实现写在 `ServerTransport` 里：

| 后果 | 说明 |
|---|---|
| 🔴 **客户端必须复制一份** | `ClientTransport` 需要同样的 `Attach`（存 socket、发状态、启接收循环）→ 要么复制、要么事后重构 |
| 🔴 **两边必然漂移** | 复制之后，一处修 bug 另一处忘 → **这是本项目已经踩过的坑**（配置模块那 5 个"第二份真相"字段） |
| 🔴 **"一份实现、两端复用"落空** | 设计里 §2 整节讲的就是这件事 |

### 3.2 正解：`Attach` 写在基类，**两个子类都继承**

这正是 `transport-design.md` §2.2 那张图的含义，也是 §2.1 第 85 行那句话：

> **关键**：子类**只提供 `Socket`**（以及客户端多一个重连策略），**其余全部继承**。

**两端的差异只有三处**（其余 100% 继承）：

| # | 差异 | 服务端 | 客户端 |
|---|---|---|---|
| 1 | **socket 从哪来** | 宿主 accept 后**调 `Attach`** | 自己 `ConnectAsync` 后**内部调 `Attach`** |
| 2 | **重连** | ❌ 不需要（是被动方） | ✅ 需要（指数退避 + 抖动，见 §7.3） |
| 3 | **收到消息怎么办** | API 分发（`_apiHandlers`） | 事件派发（推给上层/UI） |

> ⭐ **所以 `Attach` 不是"子类各自的实现"，而是"基类的唯一实现 + 子类只决定何时调用它"。**
> 服务端：**宿主**调（公开的 `Attach`）；客户端：子类**内部**调。**实现只有一份。**

### 3.3 `WebSocketDuplexBase` 该承载什么（清单）

| # | 能力 | 说明 | 设计依据 |
|---|---|---|---|
| 1 | `Attach(WebSocket)` | 存 socket、状态事件、启动接收循环 | 本文 §5 |
| 2 | 接收循环 | `while` + `ReceiveAsync` | `transport-design.md` §6.1 |
| 3 | **分帧拼装** | 循环读到 `EndOfMessage` | 坑 #1 |
| 4 | **`MaxMessageBytes` 上限** | 超限**抛异常，不静默截断** | 坑 #1 |
| 5 | 消息编解码 | `JsonNode` ↔ UTF-8 JSON | §2.1 |
| 6 | **发送串行化** | `SemaphoreSlim(1,1)` | 坑 #3、§6.4 |
| 7 | 心跳 / 保活 | 应用层帧 + "收到任何帧"的超时判定 | 坑 #2、§6.2 |
| 8 | 背压 / 有界队列 | 满则暂停读取 | 坑 #4、§6.3 |
| 9 | 关闭握手 | 完整握手 + 超时强制释放 + `CloseAsync` 幂等 | 坑 #5、§6.5 |
| 10 | **关闭顺序** | Cancel → 等循环收敛 → Dispose（见 §6） | 本文 §6 |
| 11 | `IsConnected` | `_socket?.State == WebSocketState.Open` | —— |
| 12 | `StateChanged` | 四状态；**触发线程不固定**（要写进注释） | —— |
| 13 | **消息出口** | 虚方法 / 事件 —— **子类决定收到消息做什么** | 本文 §3.2 差异 3 |
| 14 | **分类注入** | `ClassifyMessage` / `ExtractCorrelationId` 委托 | §4 |

> ❌ **不承载**：`action` / `echo` / `post_type` 等 **OneBot 语义** ——
> 设计 §3（[`transport-design.md`](transport-design.md) 第 120/133 行）明确点名批评过旧项目把 `rawJSON["post_type"]` 焊进传输层。

---

## 4. 类型与文件结构

```
ZipperAIQQ.Transport/
├── IWebSocketTransport.cs          公开契约（已有）
├── TransportOptions.cs             🆕 MaxMessageBytes / 心跳 / 重连退避 / 发送并发
├── WebSocketDuplexBase.cs          🆕 ⭐ 抽象基类：§3.3 那张清单的载体
├── TransportMessage.cs             🆕（可选）分类结果 DTO
├── Server/
│   ├── IServerTransport.cs         已有
│   └── ServerSideTransport.cs      = 基类 + API 分发
└── Client/
    ├── IClientTransport.cs         🆕
    └── ClientSideTransport.cs      = 基类 + 建连 + 重连
```

**依赖**：`Transport` **不应引用 `ZipperAIQQ.Core`**（当前 csproj 里那个引用**实测 0 处使用**）。
核心只用 BCL 的 `System.Net.WebSockets` + Serilog 包 → 保持设计里「**第 0 层（零依赖）**」。
ASP.NET Core 只在**服务端组合根**（`Server` 项目）里出现。

---

## 5. `Attach` 的实现步骤（**写在基类里**）

### 步骤 0 ⚠️ 先定语义：**重复 `Attach` 怎么办？**

| 路线 | 含义 | 代价 |
|---|---|---|
| **甲-1** ⭐ | 一个实例 = **一个连接** → 第二次 `Attach` **抛异常** | 客户端重连 = **新建实例** → DI 生命周期要配合 |
| 甲-2 | 一个实例可**换连接** → 第二次 `Attach` 替换 | 要处理"旧循环还在跑" → 复杂度回来了 |

**建议甲-1** —— `_socket` 单字段顺理成章，且**关闭顺序简单**（见 §7.3）。

### 步骤 1：前置检查（三件事，**要吵不要静默**）

| 检查 | 建议 |
|---|---|
| 已 `Dispose` | **抛**（或至少记 `Warning`）—— 设计结论是「**让"没生效"可见**」，而这个类里现在有 **6 处静默 `return`** |
| 已 `Attach` | 按步骤 0 **抛**，消息里说清"一个实例只能接一个 socket" |
| `socket.State == Open` | 顺手校验 —— 甲之下 socket 是**别人连好了才交给你的** |

> 📌 `Attach` 是**调用方写错才会失败**的方法 → 这里的检查**应该吵**。静默返回在这里毫无意义。

### 步骤 2 ⭐ **顺序**：先存 `_socket`，再启动接收循环

- **顺序不能反**：接收循环第一件事就是读 `_socket`（现在代码里 `if (_socket == null) → 记 Error 并 return`）→ 先启循环再赋 socket = **循环立刻自己退出**
- `_socket` 会被 `SendAsync` / `CloseAsync` / `DisposeAsync` **从别的线程读** → 建议 `volatile`

### 步骤 3：状态事件 —— **`Connecting` 不该由 `Attach` 发**

| 状态 | 谁发 |
|---|---|
| `Connecting` | **适配器**（客户端 `ConnectAsync` **之前**）—— `Attach` 拿到 socket 时**已经连上了** |
| **`Connected`** | ⭐ **`Attach`** |
| `Disconnecting` / `Disconnected` | `CloseAsync` / `DisposeAsync` |

⚠️ **触发线程不固定**（`Connected` 在宿主线程、`Disconnected` 可能在接收循环线程）→ **订阅者要能接受多线程回调**（客户端 UI 侧需 `Dispatcher` 派发）。**这一条要写进注释。**

### 步骤 4：启动接收循环，并**把 Task 存进字段**

- `_receiveTask = Task.Run(ReceiveLoopAsync);` —— `Attach` 是 `void`，**不会报 CS4014**
- ⚠️ **存了字段但没人 await = 加了等于没加**（项目里已犯过一次）→ 必须做步骤 5

### 步骤 5 ⭐ **异常必须有人接 + 关闭时要能等它收敛**

| # | 做什么 | 为什么 |
|---|---|---|
| **(a)** | 给 `_receiveTask` 挂 `ContinueWith(..., TaskContinuationOptions.OnlyOnFaulted)` 记日志 | 保证"接收循环异常退出"**一定进日志** |
| **(b)** | 在 `CloseAsync` / `DisposeAsync` 里 **`await _receiveTask`** | 否则 `Dispose` 完循环还在动 socket |

> ⚠️ (b) 意味着 **`DisposeAsync` 必须真的 `async`** —— 不能再是"置个标记就 `return ValueTask.CompletedTask`"。

### 步骤 6：`Attach` 是 `void` 的取舍

| | |
|---|---|
| ✅ 好处 | 宿主那行不用 `await`，简单 |
| ⚠️ 代价 | **"接收循环已在跑"没有可等待的信号** → 测试里难同步 |

若将来改成 `Task Attach(WebSocket, CancellationToken)`，宿主变成 `await transport.Attach(socket, ct)`。**不改也行**，但要知道这个取舍。

### 步骤 7：⚠️ **注册生命周期：用 `Scoped`，<u>不是</u> `Transient`**（**助手此前建议有误，已实测更正**）

> 🔴 **更正（2026-10-01）**：助手早前说「注册成 `Transient`」。**实测证明那是错的** —— 见下。

#### 实测对比（同一 scope 内解析两次）

| 注册方式 | 解析具体类型 vs 契约，是同一个实例吗 | 再解析一次具体类型 |
|---|---|---|
| **`Transient`** | ❌ **不是（两个对象！）** | ❌ **还不是同一个** |
| **`Scoped`** ⭐ | ✅ **是** | ✅ **还是它** |

**为什么 `Transient` 在这里是错的**：

> **D1 说"一个实例 = 一个连接"** —— 而 `Transient` 的语义是「**每次解析都新建**」。
> 端点里只要解析两次（比如先拿 `ServerSideTransport`、再拿 `IServerTransport`，或者一次 `IWebSocketTransport`），
> **就会得到两个 transport 对象** → **两个 `_socket` 字段、两条接收循环** 🔴

**为什么 `Scoped` 正好对**：

| 事实 | 说明 |
|---|---|
| **WS 请求 = 一个 DI scope** | ASP.NET Core 每个请求建一个 scope，**WS 请求的 scope 存活整个连接**（只要处理函数不返回） |
| **scope 内唯一实例** | 一个连接 → 一个 scope → 一个 transport ✅ **正是 D1 要的** |
| **scope 结束自动释放** | 连接结束 → 请求结束 → **scope Dispose → transport Dispose** ✅ 释放问题**一起解决** |

**⭐ 所以 `Scoped` 一次解决两件事**：①"一个连接一个实例" ②"谁负责释放"。

#### 关于释放时机：**谁解析，谁跟踪**（修正早前的说法）

早前助手说的是「从**根容器**解析 Transient → 跟踪到宿主关闭」。**更准确的规则是**：

| 从哪里解析 | 谁跟踪它 | 什么时候释放 |
|---|---|---|
| **根容器** | 根容器 | 🔴 **宿主关闭时**（会一直积着） |
| **scope** | 该 scope | ✅ **scope 结束时就释放** |

→ 所以即便用 `Transient`，只要**从 scope 解析**也不会积到宿主关闭；
**但"每次解析都是新对象"这个问题依然存在** → **所以还是要 `Scoped`**。

#### ⚠️ 一个必须知道的副作用：**同一个实例会被 `Dispose` 多次**

实测：**4 次 Dispose 调用了 3 个对象**（其中一个被 Dispose 两次）。

原因：注册了多个服务类型（具体类型 + `IServerTransport` 转发 + `IWebSocketTransport` 转发）时，
**每个注册都各自跟踪**同一个实例 → 释放时**分别调一次 `Dispose`**。

→ ✅ **所以 `DisposeAsync` 必须幂等** —— 你现在的代码有 `if (_isDisposed) return;` 是对的，**保留它**。

**⚠️ 但 `CloseAsync` <u>不能</u>用同一个早退条件**（见 §13.6）—— 那个是 bug，不是幂等。

#### 两条出路（如果不想用 `Scoped`）

| 方案 | 做法 | 评价 |
|---|---|---|
| **甲** ⭐ **（推荐）`Scoped` + WS 端点 per-request scope** | 端点从 `ctx.RequestServices` 解析；`await transport.Completion` 让 scope 活到连接结束 | ✅ 最干净 —— **实例唯一 + 自动释放，都不用手管** |
| **乙** | **工厂**（`ITransportFactory`）+ 连接结束时**显式** `await transport.DisposeAsync()` | ✅ 直白；⚠️ "谁在什么时候调"变成纪律，漏了就是泄漏 |

**⚠️ 甲的一个连带要求（D6 因此从"可选"变成"必须"）**：

`Attach` 是 `void` → 端点调完就返回 → **scope 当场结束 → 连接还活着 transport 却被 Dispose** 🔴

→ 端点必须**有东西可以 await**。两种做法：

| 做法 | 形态 |
|---|---|
| **推荐** | 基类暴露 **`Task Completion { get; }`**（连接结束时完成）→ 端点写 `await transport.Completion;`。**`Attach` 保持 `void`** —— 与 `BackgroundService.ExecuteTask` 是同一个模式 |
| 备选 | `Attach` 改成返回 `Task`（D6 原选项） |

```csharp
// 端点侧（形状示意，非实现）
app.MapGet("/events", async ctx => {
    var transport = ctx.RequestServices.GetRequiredService<ServerSideTransport>();
    var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    transport.Attach(socket);
    await transport.Completion;      // ⭐ 让 scope 活到连接结束
});
```

> 📌 **一句话**：**"谁创建、谁负责释放"** —— 连接不是容器创建的，所以**容器不该负责**。
> 要么把 scope 拉到和连接一样长（甲），要么显式释放（乙）。

---

## 6. ⭐ 关闭顺序（**已实测，写错会产生假错误日志**）

### 6.1 现状的问题

当前 `DisposeAsync` **没有 `Cancel()`**，靠 `_socket.Dispose()` 让 `ReceiveAsync` 抛异常 → 走接收循环的 `catch` → **记一条「接收消息异常」** 😿

> **而真实原因是"正常关闭"。**
> → 日志里全是**假错误** → **真正的问题会被淹在里面**（正是「让没生效可见」的反面）。

### 6.2 正确顺序

```
① StateChanged(Disconnecting)
② await socket.CloseAsync(...)      ← 礼貌关闭（发关闭帧）
③ _cts.Cancel()                     ← 让接收循环知道"该退了"
④ await _receiveTask                ← 等它真的退出（收敛）
⑤ _cts.Dispose()                    ← 所有用 _ct 的人都停了，才能释放
⑥ socket.Dispose()
⑦ StateChanged(Disconnected)
```

> ⚠️ **③ 必须在 ⑤ 之前。** 实测：CTS 释放后读 `token.IsCancellationRequested` **不抛异常**，
> 但会**永远返回 `false`**（因为从没 Cancel 过）→ **接收循环永远不会因为 token 退出**。

### 6.3 `CloseAsync` 与 `DisposeAsync` 的语义分工

| 方法 | 语义 | 收尾 |
|---|---|---|
| `CloseAsync` | **礼貌关闭这一个连接** | 走到 ② 后即可；不 Dispose 对象 |
| `DisposeAsync` | **释放整个对象** | 走完 ①–⑦ |

---

## 7. 踩坑清单（**全部已实测或已查证**）

| # | 坑 | 证据 | 对策 |
|---|---|---|---|
| **1** | **`CancellationTokenSource.TryReset()` 在已取消时<u>必然返回 false</u>** | 实测返回 `false`；源码注释：*"We can only reset if cancellation has not yet been requested: we never want to allow a CancellationToken to transition from canceled to non-canceled."* | **甲-1 之下整套 `ResetAction` / `TryReset` 直接删掉** —— 重连 = 新建实例 |
| **2** | **`new MemoryStream(buffer)` 是<u>固定容量</u>的** | 实测：初始 `Length` = 4096（不是 0）；写满后第二帧抛 `NotSupportedException: Memory stream is not expandable` | 用无参 `new MemoryStream()`（可增长） |
| **3** | **分帧时必须写 `result.Count`，不是 `buffer.Length`** | 实测 `ToArray().Length` 永远 = 4096 | 写 `result.Count` |
| **4** | **WebSocket 不允许并发 `SendAsync`** | 第二个会抛 `InvalidOperationException`；而 `HandleAPI` 是 `Task.Run` 出去的 | `SemaphoreSlim(1,1)`（设计坑 #3） |
| **5** | **`async void` 的异常没有任何办法接住** | 本项目教训清单第 2 条（旧项目原样犯过） | 改 `async Task` + 在调用点接异常 |
| **6** | **`_socket!` 只是压制警告，不产生运行时检查** | —— | 没连接时**明确语义**（见 §12 D2） |
| **7** | **`HttpListener` 在 Linux/macOS 不支持 HTTPS** | 官方文档原话（§1.2） | 改用 Kestrel |
| **8** | **顶层通配绑定 `http://+:port/` 有安全风险** | 官方 Warning | 别用通配；配合「非 loopback 必须配 token」 |
| **9** | **`StateChanged` 触发线程不固定** | `Connected` 在宿主线程、`Disconnected` 可能在循环线程 | 订阅者自己派发；注释写明 |
| **10** | **一条坏 JSON 会让整个接收循环退出** | `JsonNode.Parse` 抛 → 被外层 catch → `return` | `Parse` 单独 try/catch，**一条坏消息不该断连接** |
| **11** | **`while (!EndOfMessage)` 无限追加 → 内存无上限** | —— | `MaxMessageBytes` 上限 + 超限抛异常 |
| **12** | **接收是高频路径，别每次 `new byte[4096]`** | `transport-design.md` 第 278 行**早就写了**要用 `ArrayPool` | 复用缓冲 |

---

## 8. 客户端侧（甲的另一半）

`Attach` 通了之后，客户端只差**"谁提供 socket"**这一层：

```
ClientSideTransport.ConnectAsync(uri, ct):
    ① StateChanged(Connecting)        ← 这里才是 Connecting 该发的点（§5 步骤 3）
    ② new ClientWebSocket()
    ③ await ConnectAsync(uri, ct)
    ④ Attach(socket)                  ← ⭐ 继承来的同一个实现
    （外加：重连退避 + 抖动 —— 设计坑 #6、§6.2）
```

**NapCat 两个方向**：

| 方向 | 谁提供 socket |
|---|---|
| **正向**（服务端连 NapCat） | 服务端作为**客户端**：`ClientWebSocket` + `ConnectAsync` → `Attach` |
| **反向**（NapCat 连服务端） | 宿主 Kestrel accept → `Attach` |

> ⭐ **四种场景，`WebSocket` 到手之后 100% 一样** —— 这就是"一份实现、两端复用"。

---

## 9. `TransportOptions`

| 项 | 含义 | 默认 |
|---|---|---|
| `MaxMessageBytes` | 单条消息上限（超限**抛**，不静默截断） | 待定（建议 1 MB） |
| `HeartbeatInterval` | 应用层心跳间隔（`0` = 关闭） | `0`（v1） |
| `SendTimeout` | 单次发送超时 | 待定 |
| `Reconnect.*` | 重连退避参数（仅客户端用） | 待定 |
| `ReceiveBufferSize` | 接收缓冲（配合 `ArrayPool`） | 4096 |

⚠️ **`TransportOptions` 放哪 —— 这是 §10 那个问题的试金石**（见下）。

---

## 10. ⚠️ 配置归属："Options 该放哪"（本项目当前的一个真实矛盾）

### 10.1 现状态 vs 已定规则

| | 现状态 | `AGENTS.md` §二 已定的 |
|---|---|---|
| Options 类型放哪 | **全部在 `Core/Configurations/Options/`**<br>（`AgentOptions` / `NapCatOptions` / `ServerOptions` / `ClientOptions` / `StorageOptions`） | 「**Options 类型跟着消费者模块走**（**不集中在 Core**）」 |

**→ 直接冲突。**

### 10.2 为什么会变成这样（不是随手写错）

`ZipperConfig` 用了一个**静态 `OptionTemplates` 数组**枚举所有节：

```
OptionTemplates = { new NapCatOptions(), new StorageOptions(), ... }
```

要枚举所有类型，**就必须看得见所有类型** → 于是全塞进 Core。
而 Core 是**第 0 层（零依赖）**，它**不能**引用 `Agent` / `NapCat` / `Storage` 等项目（`overview.md` §2.3 约束 1 的对称要求）。

**→ 这个设计<u>结构性地</u>逼着业务配置住进基础设施层。**

### 10.3 两个副作用

| # | 副作用 |
|---|---|
| **1** | 🔴 **`Core` 里躺着业务机密** —— `AgentOptions.ApiKey`、`NapCatOptions.NapCatToken` 都在 Core 里。而 Core 的定位是"**无业务语义的基础设施**" |
| **2** | 🔴 **加一个模块的配置项要改 Core** —— `Core` 从"基础设施"变成了"配置类型集中营" |

> ⭐ **用 `TransportOptions` 当试金石**：它是**纯传输层**的配置（消息上限、心跳、退避），
> 按现方案**也得塞进 `Core`** → 而 `Transport` 设计上是「**第 0 层、零依赖、与业务无关**」的独立库。
> **一个与业务无关的库，它的配置类型却住在 Core 里 —— 这明显不合理。** 矛盾就这样被照出来了。

### 10.4 三条出路

| 方案 | 做法 | 好处 | 代价 |
|---|---|---|---|
| **A｜各模块自己注册节**（最轻） | 去掉硬编码的 `OptionTemplates`；`AddZipperAgent` 里 `config.RegisterSection<AgentOptions>()`；Options 类型回到各自模块 | ✅ **直接满足已定规则**；不用新项目；Core 变回"配置骨架 + 注册表" | 注册必须在 `Load()` 之前（DI 注册阶段完成，没问题）；`Save()` 的节顺序改为**按注册顺序** |
| **B｜单独一个组合/DI 项目**（用户的想法） | 新建 `ZipperAIQQ.Composition`，**引用所有模块**；`ZipperConfig` 的"节清单"搬过去；Server/Client 只引它 | ✅ Core 保持干净；Options 可回到各模块；**组合根只引一个项目** | 新增第 8 个项目；它必须引用所有模块 → **容易变"上帝项目"**（需要像 Core 那样定准入规则）；与 §4.1.7「不抽 `AddZipperApplication`」的既有结论**需要协调** |
| **C｜保持现状 + 改规则** | 承认"业务配置集中在 Core"，改掉 `AGENTS.md` §二 那一行 | 零代码改动 | ❌ **代价最重**：Core 从此有业务语义 → 「第 0 层」的定位破功，`Transport` / `Storage` 的"不收进 Core"论证也跟着松 |

### 10.5 助手的看法（**供决策，不替你拍**）

1. **A 和 B 不冲突，解决的其实是两件事**：
   - **A 解决"Options 在哪定义"**（→ 各模块）
   - **B 解决"组合根引用谁"**（→ 只引一个项目）
2. **若目标只是"解耦" → A 就够了，而且不用新项目。**
3. **B 只有在"组合根想只引一个项目"时才值得。** 但要注意：
   - 设计已经**明确否决**过 `AddZipperApplication`（`composition-design.md` §4.1.7：「不抽，因为带一堆开关的装配函数比重复几行更糟」）
   - 所以 B 若要做，**必须先说清它装什么**，而不是"把 `AddXxx` 串起来"
   - ⚠️ **风险**：一个引用所有模块的项目，会**掩盖注册顺序与生命周期问题** —— 而 §4.1.3 那条「**每个 `AddXxx` 必须能独立调用**」的硬规则，正是为了让注册保持诚实
4. **推荐顺序：先做 A**（改造成本低、直接对齐已定规则），**观察一段时间再决定要不要 B**。

---

## 11. 实现顺序与验收标准

### 11.1 顺序

| 步 | 做什么 | 立即收益 |
|---|---|---|
| ① | **删干净**：`HttpListener` 字段/构造/Dispose 两行、`_isInitialized`、csproj 的 `Core` 引用 | **立刻消掉 1 个编译错误** |
| ② | **`WebSocketDuplexBase` + `Attach` 主体**（§5 步骤 0–4） | **消掉另外 2 个错误**（`_receiveTask` / `_isStartReceived` 有了用处） |
| ③ | `_receiveTask` 的异常接住 + 收敛（§5 步骤 5） | 异常不再静默 |
| ④ | `CloseAsync` / `DisposeAsync` 的顺序（§6） | **免掉假错误日志** |
| ⑤ | `IsConnected` 实现 + `SendAsync` 的 NRE/并发（§7 坑 4、6） | 没客户端不再崩 |
| ⑥ | `HandleAPI` 去 `async void`（§7 坑 5） | 异常可追 |
| ⑦ | `ClientSideTransport`（§8） | 两端共用打通 |
| ⑧ | `Channels.NapCat` 注入 `GetAPIAction` | OneBot 语义归位 |

### 11.2 验收标准

| # | 验收项 | 怎么验 |
|---|---|---|
| 1 | CI 同款构建通过 | `dotnet build -c Release -warnaserror --no-incremental`（**当前失败，3 个错误**） |
| 2 | **`Attach` 之后能收到消息**（当前 A1 修了但没验证过） | 起一个 `ClientWebSocket` 连上去发一条 JSON，确认回调触发 |
| 3 | 分帧正确 | 发一条 **> 4 KB** 的消息，确认完整收到（当前必抛 `NotSupportedException`） |
| 4 | 超限被拒 | 发一条 > `MaxMessageBytes` 的消息，确认**抛异常**而非静默截断 |
| 5 | 并发发送不炸 | 并发调两次 `SendAsync`，确认**串行完成**（不是 `InvalidOperationException`） |
| 6 | 正常关闭**不产生错误日志** | 关闭连接 → 日志里**不该**有"接收消息异常" |
| 7 | `SendAsync` 在无连接时不崩 | 不 `Attach` 直接 `SendAsync` → 按 §12 D2 的约定行为 |
| 8 | **客户端与服务端共用同一份核心** | 代码层面：`Attach` / 接收循环 / 串行发送**只存在一处** |

> ⚠️ **现状**：项目**还没有测试项目** → 上面 2–7 目前只能**手动跑**。第 2/3/5 条都属"静默失效型"，**值得进单测**。

---

## 12. 待决策项

| # | 决策 | 选项 | 影响 |
|---|---|---|---|
| **D1** | **一个实例 = 一个连接，还是可换连接？** | ✅ **已决（2026-10-01）：一个实例 = 一个连接**（甲-1） | 第二次 `Attach` **抛异常**；客户端重连 = **新建实例**；`ResetAction` / `TryReset` 整套删除；**注册生命周期 = `Scoped`**（🔴 **更正：早前说 `Transient` 是错的** —— 实测证明 Transient 下"同一 scope 解析两次 = 两个对象"，与"一个连接一个实例"冲突；详见 **§5 步骤 7**） |
| **D2** | **`SendAsync` 在没连接时怎么办？** | ✅ **已决（2026-10-01）：记 `Warning` 后返回** | 语义为「**不保证发送成功**」（接口注释写明）。⚠️ 连带要求见下方「D2 的实现含义」 |
| **D3** | **消息出口的形态** | ✅ **已决（2026-10-01）：基类用<u>虚方法</u>出口**（如 `protected abstract ... OnMessage(...)`）；**分类与分发全在子类内部** | 见下方「D3 为什么选虚方法」 |
| **D4** | **handler 同步还是异步** | ✅ **已决（2026-10-01）：异步 —— `Func<JsonNode, Task<JsonNode>>`** | 见下方「D4：这不是 CPU 密集」。**⚠️ 同时决定：<u>所有方法都不带 `CancellationToken` 参数</u>** —— 只用私有 `_cts`（理由与代价见下方「D4-2」） |
| **D5** | **配置归属走 A / B / C**（§10.4） | A ⭐ ／ B ／ C | 决定 `TransportOptions` 及所有 Options 的最终位置 |
| **D6** | **`Attach` 保持 `void` 还是改 `Task`** | ✅ **已决（2026-10-01）：`Attach` 保持 `void`；基类暴露 `Task Completion { get; }`** | 端点 `await transport.Completion` 让 scope 活到连接结束；`Completion` **正常完成（不 fault）**，失败细节由内部 continuation 记日志 |

### D4-2 ⚠️ **连带决定：所有方法都不带 `CancellationToken` 参数**（用户拍板）

**用户理由**：「项目还没必要精细到这个程度，有个私有 `CancellationToken` 就够了，**后期如果要加也很简单**」。

**助手评估结果：这个决定站得住** ✅ —— 其中三条要记录清楚：

| # | 判断 | 说明 |
|---|---|---|
| 1 | **"对象自己的 `_cts` 就够"——在 D1 之下完全正确** | 「一个实例 = 一个连接」意味着**对象生命周期 = 操作生命周期** → 对象级取消已经覆盖了全部取消需求 |
| 2 | **"后期加很简单"——属实** ✅ | 加**可选参数**（`CancellationToken ct = default`）是**源码兼容**的改动，不破坏现有调用点 → **不是单向门**，随时可加 |
| 3 | 🔴 **但有一条代价必须补对策** | 见下 |

**⚠️ 代价：丢掉 `ctx.RequestAborted` 这个「客户端断开」信号**

| | 有接 CT | 不接 CT（当前决定） |
|---|---|---|
| 怎么知道客户端断了 | 框架**立刻**告诉你 | **只能靠"读 socket 抛异常"发现** → 更晚 |
| 日志写什么 | 可以是「**客户端断开**」（正常） | 容易写成「**接收消息异常**」（看起来像故障）🔴 |

> **✅ 但这个坑有<u>不用 CT</u> 的解法**（不接 CT 不代表必须记假错误）：
> **在 `ReceiveLoopAsync` 的 catch 里区分"正常收尾"与"真故障"** ——
> 判据：`_socket.State` 是否为 `CloseReceived` / `Closed` / `Aborted`，或异常是否为 `WebSocketException` / `OperationCanceledException`。
>
> | 情况 | 该记什么级别 |
> |---|---|
> | socket 已关闭 / 收到 Close 帧 / `_cts` 已取消 | ✅ **Information**（正常收尾，一句话即可） |
> | 其它异常 | 🔴 **Error**（真故障，带栈） |
>
> **→ 这样"日志里全是假错误"的问题就绕过去了**，而且**不需要 CT**。

**顺带确认一件已实测的事**：`CancellationToken` **不能"重置"**（`TryReset()` 在已取消时必然返回 false，源码注释：*"we never want to allow a CancellationToken to transition from canceled to non-canceled"*）。
→ 所以「改变私有的 CT」实际只能是**换新 CTS + 新 Token**。**而 D1 之下不需要复位** → 这个坑正好避开 ✅

---

## 12.1 决策说明

### D2 的实现含义（"不保证发送成功"落在代码上是什么）

用户已定：**无连接时记 `Warning` 后返回**，且 `SendAsync` **不保证发送成功**。这三条要求必须一起做到：

| # | 要求 | 为什么 |
|---|---|---|
| 1 | **`SendAsync` 自己捕获并记录发送异常**（`WebSocketException` / `ObjectDisposedException` 等） | 否则异常会从调用方（`HandleAPI`）逃出去 → **静默丢失**。「不保证成功」的代价就是「**自己负责报告失败**」 |
| 2 | ⚠️ **两种失败要分级别** | **无连接** → `Warning`（客户端没开是**常态**）；**连接在但发送失败** → `Error`（**真故障**）。混成一个级别 → 日志没法筛 |
| 3 | **调用方拿不到"发失败"的返回值** | 上层若要重试/告警，**只能靠日志或 `StateChanged`** → 这一点要写进接口注释 |

### D3 为什么选**虚方法**（而不是事件 / 注入委托）

| 形态 | 编译期保证 | 失败模式 |
|---|---|---|
| **虚方法 `protected abstract`** ⭐ | ✅ **不实现就编译不过** | 无 |
| 事件 `event Action<...>` | ❌ | **忘了订阅 = 静默不处理** |
| 注入委托（`SetXxx`） | ❌ | **忘了注入 = NRE 或静默**（现在 `GetAPIAction!` 就是这个坑） |

**→ 选虚方法。** 两个子类的差异清单里，第 3 条（**收到消息怎么办**）本来就是子类的职责，用 `abstract` 表达最准确 —— 而且**把"忘了做"变成编译错误**，符合本项目「**让没生效可见**」的取向。

**⚠️ 这是对 [`transport-design.md`](transport-design.md) §4 的一处「精化」**：

| | 原设计 | 精化后 |
|---|---|---|
| 基类 | **注入** `ClassifyMessage` / `ExtractCorrelationId` 委托，自己按 `MessageKind` 分流 | 基类**只负责**"收到一条完整 JSON → 交给 `OnMessage`"；**分类与分发全在子类** |

- ✅ **好处**：基类**不需要认识 `MessageKind`**，也**不需要 `IEventBus`** → 与 `Core` 的耦合**进一步降低**
- ✅ **设计原意（传输层不认识 OneBot 语义）完整保住**
- 📌 若将来分类策略要**可换**，再在**子类**上做委托注入即可 —— `SetAPIActionGetter` 已经是这个模式

### D4 ⚠️ **这不是 CPU 密集**（用户的前提需要修正）

> 用户的想法：「handler 放线程池执行，**因为这不是 CPU 密集吗**？用异步会好些吗」

**先纠正前提** —— 这个项目里 handler **不是** CPU 密集：

| 环节 | 性质 | 量级 |
|---|---|---|
| 解析 JSON（`JsonNode.Parse`） | CPU | **微秒级** |
| 分发到 handler | CPU | 纳秒级 |
| **handler 真正做的事** | ⚠️ **IO** | **秒级** |

**handler 实际要做什么**（按项目设计）：

| handler | 里面是什么 |
|---|---|
| Agent 相关（`chat` / `listSessions` …） | **调 LLM** —— 几秒到几十秒 |
| Storage 相关 | **读写 SQLite** —— 毫秒到秒 |
| 配置相关 | **读写文件** |

→ **全是 IO。**「处理 JSON 感觉像计算」是错觉，**真正的耗时在后面**。

**为什么「`Task.Run` + 同步 handler」在这里是坏选择**：

| 做法 | 线程占用 | 后果 |
|---|---|---|
| `Task.Run(() => syncHandler())` | **占一个线程池线程，直到 handler 做完**（包括等 LLM 的那 30 秒） | 🔴 **线程池饥饿** —— 线程池线程有限（默认 ≈ CPU 核数），**几个并发慢请求就占满** → **整个服务变慢，连接收循环都被拖** |
| `await asyncHandler()` | **等待期间不占线程** | ✅ 几百个并发等待都没问题 |

> ⭐ **一句话判据**：**handler 里有没有 `await`（IO）？有 → 必须异步。**
> **线程池是给「真的要算」的东西用的，不是给「等」用的。**

**那 `Task.Run` 在这里到底有没有用？** 有 —— 但**用途不是"给 CPU"**：

- 现在是 `MessageArrived` → `Task.Run(() => HandleAPI(...))`，目的是「**不阻塞接收循环**」
- ✅ **异步 handler 能达到同样效果，而且不占线程** → 所以 `Task.Run` 可以**整个去掉**

**推荐形态**：

```csharp
// 现状（同步）
void RegisterApiHandler(string apiName, Func<JsonNode, JsonNode> handler);

// 推荐（异步）
void RegisterApiHandler(string apiName, Func<JsonNode, CancellationToken, Task<JsonNode>> handler);
```

⚠️ **一个反方观点（要诚实说）**：异步接口对调用方（尤其新手）**更重** —— 要理解 `async/await`、要传 `CancellationToken`。
但**这里的 handler 注定要 IO**，用同步版本等于**给自己埋线程池饥饿的雷** —— 而这个雷**只在并发上来时才炸**，到时候很难查。

**⚠️ 连带一个必须定的**：**消息处理要不要并发？**

| 做法 | 后果 |
|---|---|
| **串行处理** | 一个慢请求（LLM 30 秒）**卡住后面所有请求** |
| **无限并发** | 客户端一次发 1000 个请求就打爆 → 设计**坑 #4 的"有界队列 + 背压"** |
| **⭐ v1 建议：设并发上限**（如 `SemaphoreSlim(N)`），超了排队 | 既不互相卡死，也不打爆 |

> ⚠️ **注意区分**：**`SendAsync` 要串行**（WebSocket 不允许并发发送，坑 #4），但**"处理"的等待不该串行** —— 两件事不要混。

---

## 13. 接口契约（**T1 产出**）

> **依据**：D1✅ D2✅ D3✅ D4✅ D6✅（见 §12）
> ⚠️ **契约不含实现** —— 实现按 §5 / §6 自己写。
> 💡 **关于 `CancellationToken`（D4-2）**：**所有方法都不带 CT** —— 只靠对象私有的 `_cts`。

### 13.1 ① 共用契约 —— `ZipperAIQQ.Transport`

```csharp
public enum WebSocketTransportState
{
    Connecting,     // ⚠️ 仅【客户端】可达：服务端 Attach 时 socket 已经连上了（§5 步骤 3）
    Connected,
    Disconnecting,
    Disconnected,
}

public interface IWebSocketTransport : IAsyncDisposable
{
    /// 当前连接是否可用（≈ `_socket?.State == WebSocketState.Open`）
    bool IsConnected { get; }

    /// ⚠️ 触发线程【不固定】：`Connected` 在宿主线程，`Disconnected` 可能在接收循环线程。
    ///    订阅者（尤其 UI）要自己派发。
    event Action<WebSocketTransportState>? StateChanged;

    /// 连接结束信号 —— 端点 `await` 它，让 DI scope 活到连接结束（§5 步骤 7）。
    /// 【正常完成，不 fault】；失败细节由内部 continuation 记日志。
    Task Completion { get; }

    /// 接管一个【已建立】的 socket（D1：一个实例 = 一个连接）。
    /// 前置条件（违反则【抛】，不要静默返回）：
    ///   · 未 Dispose
    ///   · 未 Attach 过 —— 第二次 Attach 抛异常（重连请【新建实例】）
    ///   · socket.State == WebSocketState.Open
    void Attach(WebSocket socket);

    /// D2：无连接 → 记 Warning 后返回；发送失败 → 记 Error 后返回。
    /// ⚠️ 语义 = 【不保证发送成功】—— 调用方拿不到失败信号，只能靠日志 / StateChanged。
    /// ⚠️ 内部【必须串行化】（WebSocket 不允许并发 SendAsync —— §7 坑 #4）。
    Task SendAsync(JsonNode message);

    /// 礼貌关闭【这一个连接】（发关闭帧 + 超时兜底）。
    /// ⚠️ 内部【不得】使用会被取消的 token —— 见 §6.2 与「关闭路径不能用会被取消的 token」。
    Task CloseAsync(WebSocketCloseStatus status);
}
```

### 13.2 ② 服务端特有 —— `ZipperAIQQ.Transport.Server`

```csharp
public interface IServerTransport : IWebSocketTransport
{
    /// 注入「怎么从 JSON 里取 action」。
    /// ⚠️ 这是 OneBot 语义的【唯一入口】—— Transport 本体不认识 action / echo（§12.1 D3）。
    ///    返回 null = 不是 API 请求。
    void SetAPIActionGetter(Func<JsonNode, string?> getter);

    /// D4：handler 【异步】（它做 IO：LLM / SQLite）。
    /// ⚠️ 不带 CancellationToken —— 生命周期由对象私有 `_cts` 管（D4-2）。
    void RegisterApiHandler(string apiName, Func<JsonNode, Task<JsonNode>> handler);

    void UnregisterApiHandler(string apiName);
}
```

### 13.3 ③ 客户端特有 —— `ZipperAIQQ.Transport.Client`

```csharp
public interface IClientTransport : IWebSocketTransport
{
    /// 建立连接；成功后【内部】调用 `Attach`。
    ///   · 期间触发 `StateChanged(Connecting)` → `Connected`
    ///   · 【重连 = 新建实例】（D1）；退避策略由【调用方】持有，不放这里
    /// ⚠️ D7 待定：token 怎么传（header / query）
    Task ConnectAsync(Uri uri, string? token = null);
}
```

### 13.4 ④ 共用基类（**声明两个子类继承什么**）

```csharp
public abstract class WebSocketDuplexBase : IWebSocketTransport
{
    // ⭐ 继承而来、【只写一遍】—— §3.3 那 14 项能力：
    //   Attach / 接收循环 / 分帧 / MaxMessageBytes / 串行发送 / 心跳 /
    //   背压 / 关闭顺序 / Completion / IsConnected / StateChanged / Dispose

    /// ⭐ D3：子类唯一【必须】实现的东西 —— 「收到一条完整 JSON 怎么办」。
    /// ⚠️ 基类【不 await】它 —— 否则一条慢消息（LLM 30 秒）会卡住接收循环。
    ///    基类应把它丢进一个内部包装（try/catch 记日志 + 并发上限），
    ///    然后立即回去读下一条。
    protected abstract ValueTask OnMessageAsync(JsonNode json);
}
```

> **具体分工**：
> | 子类 | `OnMessageAsync` 里做什么 |
> |---|---|
> | `ServerSideTransport` | 用注入的 `GetAPIAction` 取 action → 查 `_apiHandlers` → `await handler(json)` → `await SendAsync(...)` |
> | `ClientSideTransport` | 把事件推给上层（或触发事件 / 回调） |

### 13.5 两个待定的微决策

| # | 决策 | 建议 |
|---|---|---|
| **D7** | **客户端 token 怎么传给服务端**（WS 握手） | **HTTP header**（`Authorization: Bearer …`）优于 query string（query 会被日志/代理记下来）。影响 `IClientTransport.ConnectAsync` 的签名 |
| **D8** | **消息处理要不要并发上限** | v1 建议 **`SemaphoreSlim(4)`** 之类的小上限 —— 无上限时"客户端一次发 1000 个请求"会同时起 1000 个 LLM 调用；串行则会被一个慢请求卡死（§12.1 D4） |

### 13.6 ⚠️ 一条必须写进实现的约束（**关闭路径**）

**`CloseAsync` / `DisposeAsync` 是"清理路径"，<u>绝对不能用会被自己取消的 token</u>**：

| ❌ 现在的写法 | 🔴 后果 |
|---|---|
| `if (_ct.IsCancellationRequested) return;` | 一旦 `_cts` 因别的原因先取消 → **`CloseAsync` 永远早退，socket 关不掉** → 客户端看到**突然断开** |
| `await _socket.CloseAsync(status, null, _ct);` | 用"要被取消的 token"做关闭握手 → **逻辑自相矛盾** |
| `DisposeAsync` 里直接 `_socket?.Dispose()` | **完全没走关闭握手** → 客户端不知道是正常关还是掉线 |

**✅ 正确做法**：清理路径用**独立的超时 token**（例如一个 5 秒的 `CancellationTokenSource`），关闭握手失败就**强断**。业务路径才用 `_cts`。

---

## 14. 附：本文结论的验证方式

| 结论 | 验证手段 |
|---|---|
| `TryReset()` 在已取消时返回 `false` | 运行探针 + dotnet/runtime 源码 `CancellationTokenSource.TryReset` 注释 |
| `new MemoryStream(buffer)` 初始 `Length` = 4096；第二帧抛 `NotSupportedException` | 运行探针 |
| `HttpListener` Unix 支持 WebSocket（`Managed/WebSockets` 存在） | dotnet/runtime 源码目录 |
| `HttpListener` 官方不推荐新开发 / Unix 无 HTTPS / 通配绑定有安全风险 | [官方文档](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener) |
| `Stop()` 未 Start 不抛、`Start()` 两次不抛 | 运行探针（**Windows**；Linux 是另一套实现，行为可能不同） |
| 分帧写 `buffer.Length` 会带垃圾 + 多帧必炸 | 运行探针 + 代码走查 |
| **Transient vs Scoped：同一 scope 内两次解析** | 运行探针：`Transient` → **两个不同对象**；`Scoped` → **同一个实例**。且 `Transient` 下"具体类型 + 契约"解析会得到 2 个对象、被 Dispose 2 次 |
| **释放时机是"谁解析谁跟踪"** | 运行探针：从**根容器**解析 → 跟踪到宿主关闭；从 **scope** 解析 → scope 结束就释放（同一份探针里 scope 结束时 Dispose 就发生了） |
| 设计原本就是 Kestrel | `transport-design.md` §2 第 42 行原文 |

---

> **关联文档**：[`transport-design.md`](transport-design.md)（结构设计）｜[`overview.md`](overview.md) §2.3（依赖约束）｜[`composition-design.md`](composition-design.md) §4（注册与生命周期）｜[`configuration-design.md`](configuration-design.md)（配置骨架）
