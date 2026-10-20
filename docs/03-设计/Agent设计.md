# ZipperAIQQ Agent 设计（MEAI 打底 + 部分自研）

> **定位**：`ZipperAIQQ.Agent` 项目的详细设计
> **上层文档**：[`项目总览.md`](项目总览.md)（原 `technical-design-v1.md` 的 §3.4 已**归档**，见 [`archive/旧技术设计.md`](../99-归档/旧技术设计.md)）
> **决策依据**：[`docs/07-记忆/决策记录.md`](../07-记忆/决策记录.md) 「Agent 实现改为 MEAI + 部分自研」
> **实测依据**：`D:\temp\MeaiSpike`（2026-09-22 spike，见 §1.2）
> **状态**：待用户确认

---

## 1. 定位与边界

### 1.1 一句话

**用 `Microsoft.Extensions.AI`（MEAI）扛住"调用 LLM + 自动跑工具循环"这条主干，自研的是它周围的一圈**——会话、权限、事件、上下文策略、编排。

### 1.2 MEAI 给什么（**已由 spike 实测确认**）

spike 结论（`D:\temp\MeaiSpike\Program.cs` 实测输出）：

| 结论 | 实测证据 |
|---|---|
| ✅ `FunctionInvokingChatClient` **在流式下也能自动执行工具** | 底层 client 被调用 **2 次**（第 1 次返回工具调用，第 2 次拿到结果后返回文本） |
| ✅ **工具事件是白拿的** | update 流依次为 `FunctionCallContent` → `FunctionResultContent` → `TextContent` |
| ✅ **工具结果自动回填** | 第 2 次调用的上下文里含 `FunctionResultContent` |
| ✅ **assistant 消息（含工具调用）自动回写** | 第 2 次调用的上下文里含 `FunctionCallContent`（**这个坑框架替我们踩平了**） |
| ❌ **`IChatReducer` 只有接口，零实现** | 类型探查：含 "Reducer" 的仅 1 个（就是接口本身） |
| ❌ **没有内置重试** | 类型探查：含 "Retry" 的类型数 = **0** |
| ➕ 额外发现 | MEAI 提供 `DelegatingChatClient` / `DelegatingAIFunction` / `DelegatingImageGenerator` 等**中间件基类** |

### 1.3 分工边界

| 层 | 归属 | 内容 |
|---|---|---|
| **LLM 调用** | MEAI | `Microsoft.Extensions.AI.OpenAI` → OpenAI 兼容端点（DeepSeek） |
| **工具循环** | MEAI | `FunctionInvokingChatClient`（含参数拼接、结果回填、assistant 回写） |
| **工具调用事件** | MEAI | 从 update 流里取 `FunctionCallContent` / `FunctionResultContent` |
| **统一抽象 / 中间件基类** | MEAI | `IChatClient` / `DelegatingChatClient` / `DelegatingAIFunction` |
| 会话隔离 | **自研** | `SessionId` → 上下文与状态的归属 |
| 权限门禁 | **自研** | `GuardedAIFunction`（接缝 C） |
| 事件翻译与推送 | **自研** | `ChatResponseUpdate` → `AgentEvent` → WS 帧 |
| 上下文压缩 | **自研** | `IChatReducer` 的实现（三级策略） |
| 重试 | **自研** | `RetryingChatClient`（MEAI 无内置） |
| 漂移修正 | **自研** | MEAI 完全不提供 |
| 外层编排 | **自研** | 何时调 agent、多轮怎么走 |

### 1.4 分层图

```
┌──────────────────────────────────────────────────────┐
│  自研：编排层（Server 侧）                              │
│    · 何时触发 agent、会话定位、多轮策略                  │
├──────────────────────────────────────────────────────┤
│  自研：会话与上下文（AgentRuntime / ContextManager）      │
│    · SessionId 隔离 · token 预算 · TieredChatReducer    │
├──────────────────────────────────────────────────────┤
│  自研：事件翻译（AgentEventTranslator）                  │
│    · ChatResponseUpdate → AgentEvent → IEventBus       │
├──────────────────────────────────────────────────────┤
│  自研：工具层（GuardedAIFunction / ToolRegistry）        │
│    · 权限门禁 · 超时 · 异常隔离 · 输出限长                │
├──────────────────────────────────────────────────────┤
│  MEAI：FunctionInvokingChatClient（自动工具循环）         │
├──────────────────────────────────────────────────────┤
│  MEAI：RetryingChatClient（自研重试，包在循环【内层】）     │
├──────────────────────────────────────────────────────┤
│  MEAI：IChatClient ← Microsoft.Extensions.AI.OpenAI     │
│    → OpenAI 兼容端点（DeepSeek）                         │
└──────────────────────────────────────────────────────┘
```

---

## 2. 核心抽象

### 2.1 会话

```csharp
/// 一个会话 = 一个上下文容器（群 / 私聊 / 管理会话各一个）
public sealed record AgentSession
{
    public required string SessionId { get; init; }        // "g<群号>" / "p<QQ号>" / "admin"
    public required List<ChatMessage> History { get; init; }
    public string? SystemPrompt { get; init; }             // 人格设定（多角色切换的入口）
    public string? PersonaId { get; init; }
    public DateTimeOffset LastActiveAt { get; set; }
    public AgentSessionStats Stats { get; set; } = new();
}

public interface ISessionStore
{
    ValueTask<AgentSession?> GetAsync(string sessionId, CancellationToken ct);
    ValueTask SaveAsync(AgentSession session, CancellationToken ct);
    ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken ct);
}
```

**要点**：
- **`SessionId` 是隔离的唯一依据**——不同群、同群不同人、私聊，各自一个会话，**绝不共用上下文**
- 内存缓存 + 异步落库（重启可恢复）
- `SystemPrompt` 与 `History` 分开存：**system prompt 永不参与压缩**（防漂移的根基）

### 2.2 用不用自己的 `IAgent`？

**倾向：不要**，直接用 MEAI 的 `AIAgent`（若可用）或薄封装。

理由：MEAI 已经有 `AIAgent` / `ChatClientAgent` 抽象，再造一层 `IAgent` 大概率是重复。
**但**：需要一个"会话 + 事件 + 工具"的组合入口，建议做成 **`IAgentRuntime`**（自研，负责编排，不重复 MEAI 的 agent 抽象）。

```csharp
public interface IAgentRuntime
{
    /// 跑一轮：组装上下文 → 调 MEAI → 翻译事件 → 落库
    IAsyncEnumerable<AgentEvent> RunAsync(AgentRunRequest request, CancellationToken ct);
}

public sealed record AgentRunRequest
{
    public required string SessionId { get; init; }
    public required string UserInput { get; init; }
    public Role RequesterRole { get; init; }               // 权限门禁用
    public string? PersonaId { get; init; }                // 多角色
}
```

> **待确认**：是否直接用 MEAI 的 `AIAgent`（需查它在 10.10.0 里的实际形态）。

### 2.3 `AgentEvent`（事件流）

```csharp
public abstract record AgentEvent(long Seq, DateTimeOffset Time);

public sealed record RunStartedEvent(...)                              : AgentEvent;
public sealed record TextDeltaEvent(string Text)                       : AgentEvent;
public sealed record ReasoningDeltaEvent(string Text)                  : AgentEvent;
public sealed record ToolCallStartedEvent(string CallId, string ToolName, IDictionary<string, object?>? Args) : AgentEvent;
public sealed record ToolCallFinishedEvent(string CallId, object? Result, TimeSpan Elapsed) : AgentEvent;
public sealed record RunFinishedEvent(string? FinalText, RunStopReason Reason) : AgentEvent;
public sealed record RunFailedEvent(string Error)                      : AgentEvent;

public enum RunStopReason { Completed, MaxRoundsReached, Cancelled, Error }
```

> ⚠️ **与旧设计的差异**：不再需要"自研循环去埋点产出这些事件"——它们**从 MEAI 的 update 流翻译而来**（见 §4）。

---

## 3. 工具系统

### 3.1 工具定义：直接用 `AIFunctionFactory`

```csharp
static string GetWeather(string city) => $"{city}：晴，20°C";

AIFunction tool = AIFunctionFactory.Create(
    GetWeather,
    name: "get_weather",
    description: "查询指定城市的当前天气。用户问到天气时使用。");
```

**约定**：
- **`description` 决定 LLM 会不会用这个工具** —— 必须写"**何时使用**"，不是复述工具名
- 参数类型靠 `AIFunctionFactory` 从方法签名推导 JSON Schema
- **参数校验在工具内自己做**：Schema 是给 LLM 的提示，**不是运行时校验**

### 3.2 `GuardedAIFunction`（**接缝 C 的落地点**）

> **设计修正**：原设计让它同时负责"权限 + 事件"。spike 证明**事件是白拿的**（框架自动产出 `FunctionCallContent`/`FunctionResultContent`）→ **`GuardedAIFunction` 只负责安全与稳定性四件事**。

```csharp
/// 包一层：权限门禁 + 超时 + 异常隔离 + 输出限长
public sealed class GuardedAIFunction : DelegatingAIFunction   // MEAI 提供 DelegatingAIFunction
{
    private readonly IPermissionGate _gate;
    private readonly TimeSpan _timeout;
    private readonly int _maxOutputChars;
    private readonly string _toolName;
    private readonly ILogger _logger;

    public override async ValueTask<object?> InvokeAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // ① 权限门禁（接缝 C）—— 拒绝时【返回说明】而非抛异常，让 LLM 知道"你调不了"
        if (!_gate.Allow(_toolName, arguments))
            return $"权限不足：工具 {_toolName} 仅管理员可用。";

        // ② 超时
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            var result = await base.InvokeAsync(arguments, cts.Token);

            // ③ 输出限长（防止一次吃掉全部上下文）
            //    ⚠️ 必须先把结果统一成 string —— MCP 工具返回的是 JsonElement，只判 string 会漏（见 §3.4）
            var text = result switch
            {
                null => "",
                string s => s,
                JsonElement je => je.GetRawText(),
                JsonNode jn => jn.ToJsonString(),
                _ => JsonSerializer.Serialize(result)
            };
            if (text.Length > _maxOutputChars)
                text = text[.._maxOutputChars] + $"\n…<已截断，原文 {text.Length} 字符>";

            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"工具 {_toolName} 执行超时（{_timeout.TotalSeconds} 秒）。";
        }
        catch (Exception ex)
        {
            // ④ 异常隔离 —— 转成失败结果回填，绝不炸掉 agent 循环
            _logger.LogWarning(ex, "工具 {Tool} 执行失败", _toolName);
            return $"工具 {_toolName} 执行失败：{ex.Message}";
        }
    }
}
```

**三条纪律**：

1. **拒绝与失败都要"回填成结果"**，不能抛异常 —— 否则整个循环会炸（而 LLM 拿到"权限不足"还能换个方式回答）
2. **超时用 linked CTS**，且要区分"我方超时"与"外层取消"
3. **输出限长必须有** —— 工具结果可能巨大（读大文件、查大结果集）；**且必须兼容非 `string` 返回**（MCP 工具返回 `JsonElement`，见 §3.4）

### 3.3 工具注册表

```csharp
public interface IToolRegistry
{
    IReadOnlyCollection<AIFunction> All { get; }                 // 已包装成 GuardedAIFunction
    AIFunction? Find(string name);
    IList<AITool> BuildToolList(IEnumerable<string>? enabledNames);   // 直接喂给 ChatOptions.Tools
}
```

- 工具以 `IEnumerable<IToolDefinition>` 注入（DI 收集），**新增工具 = 新增一个类 + 一行注册**
- 工具名**全局唯一**，重名在**启动时**报错
- 注册表负责把 `AIFunction` 包成 `GuardedAIFunction`（**包装在注册时做一次，不在每次调用时做**）

### 3.4 工具来源（**三种，统一到 `AIFunction`**）

> **已明确（2026-09-22 用户确认）**：「环境中的外部工具」**三类都要**。设计上统一收敛到 `AIFunction` 这一层 —— **注册表只认 `AIFunction`，不关心它从哪来**。

| 来源 | 怎么变成 `AIFunction` | 门禁档位 | 关键约束 |
|---|---|---|---|
| **① 自研 C# 方法** | `AIFunctionFactory.Create(...)` | 按工具逐个声明 | 主力来源（§3.1） |
| **② MCP server**（外部进程） | `McpClientTool : AIFunction`（包 `ModelContextProtocol.Core`） | **默认不启用**，要显式白名单 | 外部进程 → 要管生命周期与降级 |
| **③ 本机 OS 能力**（执行命令、读写文件） | 自研包装成 `AIFunction` | ⛔ **默认仅管理员** | **最高危**；路径必须限定在白名单目录内 |

**统一铁律**：

1. **三类一律经 `GuardedAIFunction` 包装** —— **不许存在绕过门禁的注册路径**（包括 MCP 工具）
2. **MCP 工具的 `name` / `description` 来自外部，不可信** → 注册时**必须标注来源**，且**默认不自动启用**
3. **OS 能力的门禁要校验参数，不能只校验工具名** —— 危险的是 `rm -rf /` 这个**参数**，不是"执行命令"这个工具

**MCP 的已查证事实**（2026-09-22，来源：MCP C# SDK 官方 API 文档）：

| 事实 | 影响 |
|---|---|
| `public sealed class McpClientTool : AIFunction`（`ModelContextProtocol.Client`，程序集 `ModelContextProtocol.Core.dll`） | ✅ 与 MEAI 天然对接，**注册表不用改** |
| 它是 **`sealed`** | ⚠️ **不能继承** → `GuardedAIFunction` 只能**包裹**它（`DelegatingAIFunction` 正好支持任意 `AIFunction`）—— 这也是**必须用装饰器模式**的硬理由 |
| 提供 `WithName(string)` / `WithDescription(string)` | ✅ 可给 MCP 工具**加命名空间前缀**（`mcp__<server>__<tool>`）避免重名；**改的只是给模型看的名，调服务端仍用原名** |
| `InvokeAsync` 返回的是**序列化后的 `CallToolResult`（`JsonElement`）**，不是 `string` | ⚠️ **§3.2 的输出限长只处理了 `string`，这里会漏** —— 截断逻辑**必须同时处理 `JsonElement` / `JsonNode`**（见下方修正） |
| 工具不存在或服务端出错时抛 `McpException` | ✅ 被 `GuardedAIFunction` 的异常隔离兜住 |

> **⚠️ §3.2 的修正点（MCP 带来的）**：`GuardedAIFunction` 的输出限长不能只判断 `result is string`：
> ```csharp
> // 错：MCP 工具返回 JsonElement，这条判断会漏掉
> if (result is string s && s.Length > _maxOutputChars) ...
>
> // 对：按"最终序列化后的字符数"限长
> string text = result switch
> {
>     null => "",
>     string s => s,
>     JsonElement je => je.GetRawText(),
>     JsonNode jn => jn.ToJsonString(),
>     _ => JsonSerializer.Serialize(result)          // 兜底
> };
> if (text.Length > _maxOutputChars) text = text[.._maxOutputChars] + $"…<已截断，原文 {text.Length} 字符>";
> return text;
> ```
> **顺带收益**：统一成 `string` 返回后，回填给模型的内容也更干净。

**MCP 的工程问题**：

| 问题 | 倾向 |
|---|---|
| 客户端放哪个项目？ | `ZipperAIQQ.Agent/Tools/Mcp/` —— 它只是**工具来源之一**，不值得单独建项目 |
| 进程生命周期 | 按配置**启动时连接**、断线重连；**连不上不阻塞启动**（降级为该 server 的工具不可用 + 告警） |
| 与权限门禁的关系 | MCP 工具**默认对非管理员不可见**（连 schema 都不给模型）—— 让模型"看不见"比"看得见但调不动"更省 token、也更少试探 |
| 第一版做不做 | 见 §10 Q6 |

---

## 4. 事件流（自研翻译层）

### 4.1 `ChatResponseUpdate` → `AgentEvent`

spike 实测的 update 流：

```
[update #1] FunctionCallContent      name=get_weather callId=call-1 args=city=北京
[update #2] FunctionResultContent    callId=call-1 result=北京：晴，20°C
[update #3] TextContent              "北京今天晴，20°C。"
```

**翻译规则**：

| update 里的 content | 翻译成 |
|---|---|
| `TextContent` | `TextDeltaEvent(text)` |
| `FunctionCallContent` | `ToolCallStartedEvent(callId, name, args)` |
| `FunctionResultContent` | `ToolCallFinishedEvent(callId, result, elapsed)` |
| （推理内容，若模型返回） | `ReasoningDeltaEvent(text)` |
| 流结束 | `RunFinishedEvent(finalText, reason)` |

```csharp
public sealed class AgentEventTranslator
{
    public static async IAsyncEnumerable<AgentEvent> TranslateAsync(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long seq = 0;
        var textBuffer = new StringBuilder();
        var toolStart = new Dictionary<string, DateTimeOffset>();

        await foreach (var update in updates.WithCancellation(ct))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent t:
                        textBuffer.Append(t.Text);
                        yield return new TextDeltaEvent(++seq, DateTimeOffset.Now, t.Text);
                        break;

                    case FunctionCallContent fc:
                        toolStart[fc.CallId] = DateTimeOffset.Now;
                        yield return new ToolCallStartedEvent(++seq, DateTimeOffset.Now,
                            fc.CallId, fc.Name, fc.Arguments);
                        break;

                    case FunctionResultContent fr:
                        var elapsed = toolStart.TryGetValue(fr.CallId, out var s)
                            ? DateTimeOffset.Now - s : TimeSpan.Zero;
                        yield return new ToolCallFinishedEvent(++seq, DateTimeOffset.Now,
                            fr.CallId, fr.Result, elapsed);
                        break;
                }
            }
        }

        yield return new RunFinishedEvent(++seq, DateTimeOffset.Now, textBuffer.ToString(), RunStopReason.Completed);
    }
}
```

### 4.2 推送

翻译出的 `AgentEvent` → `IEventBus.Publish` → `EventBroadcaster` → WS `/events` → WPF 客户端。
（**事件总线与广播的细节见 `通信层设计.md` 与技术设计 §3.9。**）

> **要点**：`Agent` 层**只发事件到事件总线**，**不直接持有 WS 连接**（保持层次）。

### 4.3 流式文本的限流

真实 LLM 的 `TextContent` 是**逐片**的（可能每片一两个字）。若逐片推给 WS 会刷爆客户端。

**对策**：翻译层做**合并限流**（如 50ms 合并一次，或累积到 N 字符再发），**但最终文本必须完整**（`RunFinishedEvent` 里带全文）。

---

## 5. 上下文管理（**全自研**）

### 5.1 token 预算

```
可用预算 = 模型上下文上限 − 预留输出 − 安全余量
```

- 每轮调用前估算；超了就压缩
- 估算可以粗糙（中英文字符数近似），但**要有一致性**（同输入得同结果）

### 5.2 压缩策略：`TieredChatReducer`（自研 `IChatReducer`）

**spike 确认 `IChatReducer` 只有接口、没有实现** → 策略完全自研。

**三级策略（按代价递增）**：

| 级 | 动作 | 何时用 |
|---|---|---|
| **1** | 丢弃最老的**工具结果**（保留工具调用与文本对话） | 轻度超预算 |
| **2** | 把老对话**摘要**成一条 system 消息 | 中度超预算 |
| **3** | **硬截断**最早的轮次 | 严重超预算 |

**四条不变量**：

1. **system prompt 永不丢**（人格设定的根基）
2. **最近 N 轮永不压缩**（N 建议 3–5，保证连续性）
3. **压缩动作本身要产出事件**（让 UI 显示"上下文已压缩"）—— 别偷偷干
4. **压缩后必须仍满足预算**（否则会陷入"压了还超"的死循环）

```csharp
public sealed class TieredChatReducer : IChatReducer
{
    public Task<IList<ChatMessage>> ReduceAsync(
        IList<ChatMessage> messages, CancellationToken ct)
    {
        // 1. 若含 <N> 轮以内的消息 → 保留
        // 2. 从最老的工具结果开始丢
        // 3. 还不够 → 摘要
        // 4. 还不够 → 硬截断
        // 5. 断言：结果必须在预算内
    }
}
```

### 5.3 会话落库

- 内存缓存（`ConcurrentDictionary<string, AgentSession>`）+ 异步落库（SQLite）
- **写库失败只记日志，绝不阻塞回复**（消息发不出去比丢一条历史严重得多）

---

## 6. MEAI 装配（DI）

### 6.1 管道的装配（**用 `ChatClientBuilder`，别手工套构造函数**）

> **拼装顺序的权威依据**（2026-09-22 查证 MEAI 源码 `ChatClientBuilder.Build`）：
> ```csharp
> // To match intuitive expectations, apply the factories in reverse order,
> // so that the first factory added is the outermost.
> ```
> → ⭐ **第一个 `.Use(...)` 是最外层。** 这条别凭直觉猜，`IHttpClientBuilder` 那套 `DelegatingHandler` 是同一个语义。

```csharp
services.AddSingleton<IChatClient>(sp =>
{
    var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;

    // 最内层：OpenAI 兼容端点（DeepSeek）
    IChatClient inner = new OpenAIClient(
            new ApiKeyCredential(llm.ApiKey),
            new OpenAIClientOptions { Endpoint = new Uri(llm.BaseUrl) })
        .GetChatClient(llm.Model)
        .AsIChatClient();

    return new ChatClientBuilder(inner)
        // ① 最外层：漂移守卫 —— 它要的是「工具循环跑完之后的最终回答」
        .Use(next => new DriftGuardChatClient(next, /* detectors, corrector, options, logger */))
        // ② 上下文压缩：一轮用户消息只压一次（放在循环外）
        .UseChatReducer(sp.GetRequiredService<IChatReducer>())
        // ③ 工具循环（MEAI）
        .Use(next => new FunctionInvokingChatClient(next))
        // ④ 重试（自研）—— 最内层：循环里每一次 LLM 调用都享有重试
        .Use(next => new RetryingChatClient(next, sp.GetRequiredService<ILogger<RetryingChatClient>>()))
        .Build(sp);
});
```

**为什么是这个顺序**：

| # | 位置 | 理由 |
|---|---|---|
| ① | 漂移守卫在**最外层** | 它必须看见**工具循环结束后的最终回答**才能判断漂移（人格 / 格式 / 事实都是对**成品**的判断）。放内层只会看到一轮轮中间态，还会干扰工具循环 |
| ② | 压缩在**循环外** | `FunctionInvokingChatClient` **每轮迭代都会把全部消息再传给内层** → 压缩若在它**内层**，一次用户消息会被**反复压缩 N 次**（还可能与工具调用中间态打架）。放它外层 = **一轮压一次** ✅ |
| ③ | 工具循环 | MEAI 提供的 `FunctionInvokingChatClient`（也可用糖 `UseFunctionInvocation()`） |
| ④ | 重试在**最内层** | `FunctionInvokingChatClient(Retrying(...))` → 循环里**每一次** LLM 调用都会重试 ✅<br>`Retrying(FunctionInvokingChatClient(...))` → 只有整轮失败才重试，**且重试会重跑已成功的工具**（**副作用可能重复执行**）❌ |

> ⚠️ **两条必须记住的代价**：
>
> **1. 守卫在最外层 → 它的重试 = 重跑整条链（含工具循环）。**
> 所以 §8.3 才立了两条硬规矩：**只重试 1 次**、**已有副作用工具调用时禁止重跑**。
>
> **2. 压缩在循环外 → 循环内部的消息增长不受压缩保护。**
> 兜底靠 `GuardedAIFunction` 的**输出限长**（§3.2）—— 工具结果被截断 → 循环内不会突然爆上下文。
> **这就是"输出限长"不是可选优化的原因。**

> 📌 **`DriftGuardChatClient` 必须重写 `GetStreamingResponseAsync`**，不能只重写 `GetResponseAsync`。
> MEAI 的 `AnonymousDelegatingChatClient` 文档明确写着：只提供 `GetResponseAsync` 时，`GetStreamingResponseAsync` 会退化为"**limited streaming**"（先攒批再吐）。
> 本项目**全程走流式**（`AgentEvent` 靠它产文本增量）→ 只覆写非流式方法 = **流式静默降级**，这条坑必须避开。

**可选加装的中间件**（MEAI 自带，需要时一行就能塞进来）：

| 中间件 | 用途 | 建议位置 |
|---|---|---|
| `UseLogging()` | 报文级日志 | 最内层（贴着真实调用） |
| `UseOpenTelemetry()` | 链路追踪 + token 计数 | 最内层 |
| `UseDistributedCache()` | 相同请求去重（重放场景） | 循环外 |
| `ConfigureOptions(...)` | 统一改 `ChatOptions`（如温度） | 视需要 |

### 6.2 注册扩展

```csharp
public static IServiceCollection AddZipperAgent(
    this IServiceCollection services, IConfiguration config);
```

负责：绑 `LlmOptions`、建 `IChatClient`（上面那串）、注册 `IToolRegistry`（收集 `IToolDefinition`）、注册 `ISessionStore`、注册 `IAgentRuntime`。

---

## 7. 重试与错误处理（**自研**）

spike 确认 **MEAI 无内置重试** → 自研 `RetryingChatClient : DelegatingChatClient`。

| 失败类型 | 策略 |
|---|---|
| **429 / 5xx / 网络错误** | **指数退避重试**（1s → 2s → 4s，上限 3–5 次，带抖动） |
| **4xx 参数错误（400/401/403）** | **不重试**（重试无用，且可能被计费） |
| **超时** | 视为网络错误，重试 |
| **取消（用户主动）** | **不重试**，直接抛 `OperationCanceledException` |

> ⚠️ **不要**用 `.Result` / `.Wait()` 等异步结果（`CSharp编码规范.md` §3.1）。

---

## 8. 漂移修正（**自研**）

> **定义已明确（2026-09-22，用户确认）**：「AI 设定漂移」指**三类** ——
> ① **人格 / 语气漂移**　② **指令遵循漂移**　③ **事实漂移（自我矛盾 / 幻觉）**
> **「话题漂移」不在范围内**（被群友带偏不做自动纠正）。

### 8.1 一个共用骨架

三种漂移的**检测**手段完全不同，但**纠正**动作高度相似 —— 都是「**把权威信息重新摆到模型面前，然后让它重说一遍**」。所以抽成可插拔骨架：

```csharp
public interface IDriftDetector
{
    string Name { get; }
    /// 只读最终输出 + 上下文即可；L0/L1 档不得发起网络调用
    ValueTask<DriftFinding?> DetectAsync(DriftContext ctx, CancellationToken ct);
}

public sealed record DriftFinding(
    string DetectorName,
    DriftSeverity Severity,   // Info / Warn / Block
    string Reason);           // 会作为纠正提示回填给模型

public interface IDriftCorrector
{
    /// 把 finding 变成「回填的一条消息」或「重新注入的 system」
    ChatMessage BuildCorrection(DriftFinding finding, DriftContext ctx);
}
```

**唯一落点**：`DriftGuardChatClient : DelegatingChatClient`（包装顺序见 §6.1）。

### 8.2 ① 人格 / 语气漂移

| | |
|---|---|
| **典型症状** | 聊到第 20 轮，该毒舌的变客气了、该简短的两百字了 |
| **根因** | system prompt 离「当前这一轮」太远，注意力被稀释 |
| **检测**（优先不花额外调用） | · **距离计数**：距上次注入 system 已过 N 轮 → 直接触发<br>· **廉价启发式**（纯本地计算）：回复长度突变、人称 / 口癖消失、命中禁用词表<br>· 可选高成本：独立 persona 自评调用（廉价模型打分） |
| **纠正** | ⭐ **周期性重新注入 system prompt** —— 最有效的一招<br>· 每 N 轮（默认 **10**）在消息序列**末尾**再插一条 system<br>· **会话从库里恢复时必做一次**（跨重启最容易出戏）<br>· 检测器报 `Warn` 时立即做一次，并附上「刚才那段偏离了人设」 |
| **代价** | 每轮多一份 system 的 token —— 所以 **N 不能太小** |

> **硬约束**：`TieredChatReducer`（§5.2）**永远不许丢 system 消息** —— 否则压缩一轮就出戏。

### 8.3 ② 指令遵循漂移

| | |
|---|---|
| **典型症状** | 该输出 JSON 却输出散文；该先查工具再回答，直接凭记忆答了 |
| **检测**（**可判定信号，不需要额外模型**） | · **结构化输出校验**：约定要 JSON 的地方，解析失败即判定<br>· **工具使用契约**：该走工具的意图下，`AgentEvent` 流里**没有 `ToolCallStarted`** 即判定<br>· 必填要素缺失（要带出处、要带数字） |
| **纠正** | ① **自动重试 1 次**，并把**具体失败原因**作为一条 user 消息回填（「上次输出不是合法 JSON，错误在第 3 行」）<br>② 连续 2 次仍失败 → **降级**：固定模板兜底 + 发 `AgentEvent` 告警。**不许无限重试** |
| **纪律** | 纠正提示必须说**哪里不对**；只说「重新来」等于没说 |

> ⚠️ **重试落点很关键**：`DriftGuardChatClient` 在**最外层**，重试 = **重跑整条链（含工具循环）**。所以两条硬规矩：
> 1. **只允许重试 1 次**（成本兜底）
> 2. 若该次已产生**有副作用的工具调用**（写文件 / 发消息），**禁止重跑** → 直接降级为告警
>
> **宁可这一次答得不好，也不能把消息重复发出去。**

### 8.4 ③ 事实漂移 / 自我矛盾

> **先把话说清楚**：**幻觉没法靠「让模型自查」根治。** 让模型检查自己刚说的话，它经常会给出同样自信的确认。设计上能做的是两件事：**把可核对的事实挡在模型外部**，加**事后冲突检测**。

| | |
|---|---|
| **预防（比纠正重要）** | ⭐ **可核对的事一律走工具**，不靠模型记忆（「现在几点」「谁是管理员」这类必须查）<br>· 维护**会话事实清单**（`ISessionFactStore`，**新增组件，待批准**）<br>· 清单作为 **system 的一部分常驻**（同样受 §8.2「不许丢」约束）<br>· 事实**带出处与时间戳**（「2026-09-22 由工具查得」），**过期事实要能失效** |
| **检测** | · **冲突检测**：新回复里出现与事实清单冲突的数字 / 名字 / 状态 → 标记<br>· **同一事实的多次不同表述**（第 5 轮说 A、第 12 轮说 B）→ 标记<br>· 可选高成本：独立校验调用 |
| **纠正** | 把**权威版本**重新摆出来（「注意：已确认 X = …（来源：工具查询）」），要求模型更正<br>**冲突时优先级**：**工具查询 > 用户告知 > 模型记忆** |
| **纪律** | 检测到冲突**必须记录**（`AgentEvent` + 日志），**即使模型改对了** —— 冲突率是「人设 / prompt 是否健康」的核心指标 |

### 8.5 成本分级（**默认只开免费的那档**）

| 档 | 含什么 | 额外 LLM 调用 | 默认 |
|---|---|---|---|
| **L0** | 周期性重注入 system + 事实清单常驻 + 结构化输出校验 | **0** | ✅ **默认开** |
| **L1** | + 廉价启发式检测（长度 / 口癖 / 禁用词）+ 冲突检测 | 0（纯本地计算） | ✅ **默认开** |
| **L2** | + 独立自评 / 校验调用（人格打分、事实核对） | 每轮 **+1** | ⬜ **按需开**（配置项） |

> **理由**：L0/L1 **不花一分钱**却能拦住大部分漂移；L2 每轮多一次调用，QQ 群里高频对话会明显吃成本 → **按会话 / 按群**配置开关。

### 8.6 可观测

漂移是**产品的核心体验指标**，必须看得见：

- 每次 `DriftFinding` → 发一条 `AgentEvent`（`DriftDetectedEvent`）→ **客户端能实时看到**
- 落库（`SessionId` + 检测器 + 严重度 + 时间）→ 能画出「哪类漂移在哪个会话最频繁」
- **纠正后可以说出来**（可选）：人设纠回时若角色设定允许，让它自嘲一句 —— **把修正变成体验的一部分**

### 8.7 一句话总结

| 漂移 | 一句话对策 |
|---|---|
| **人格 / 语气** | **定期把 system 重新念给它听**（最便宜、最有效） |
| **指令遵循** | **可判定的就自动重试一次，说不清的就降级** |
| **事实** | **能查的绝不靠记；记下来的必须能对账** |

---

## 9. 测试策略

| 测什么 | 怎么测 | 必测项 |
|---|---|---|
| **事件翻译** | 用 spike 里的 `FakeChatClient` 脚本化产出 update | ① 文本增量 ② 工具调用事件 ③ 工具结果事件 ④ 顺序正确 ⑤ 空 update 不崩 |
| **`GuardedAIFunction`** | 直接构造，喂各种 `AIFunctionArguments` | ① **权限不足被拒**（安全底线）② 超时被拦 ③ 工具抛异常被转成失败结果 ④ 超长输出被截断 ⑤ 外层取消能正确传播 |
| **`RetryingChatClient`** | 假 client 按脚本抛 429/500/400 | ① 429 重试 ② 5xx 重试 ③ **400 不重试** ④ 取消不重试 ⑤ 退避时长合理 |
| **`TieredChatReducer`** | 构造超长上下文 | ① 压缩后仍在预算内 ② **system 未被丢** ③ 最近 N 轮保留 ④ 压缩不会死循环 |
| **会话隔离** | 两个 SessionId 交叉跑 | **上下文绝不串** |
| **DI 装配** | 从容器解析 `IChatClient` | 包装顺序正确（`FunctionInvoking` 在最外、`Retrying` 在内） |

**原则**：**测试全部用 `FakeChatClient`，不打真实 DeepSeek**。

---

## 10. 待决问题

| # | 问题 | 影响 | 结论 / 倾向 |
|---|---|---|---|
| ~~Q1~~ | ~~用不用 MEAI 的 `AIAgent`？~~ | —— | ✅ **已查证（2026-09-22）：MEAI 10.10.0 里根本没有 `AIAgent` 类型** —— `Microsoft.Extensions.AI.Abstractions` 与 `Microsoft.Extensions.AI` 两个包的 DLL 与 XML 文档里都搜不到 `AIAgent` / `AgentRunResponse` / `AgentThread`（`AIAgent` 属于 MAF）。**所以这不是一个选择题**：自研 `IAgentRuntime` 是唯一路径 |
| ~~Q2~~ | ~~「设定漂移」具体指哪一类？~~ | —— | ✅ **已明确（2026-09-22）**：① 人格/语气 ② 指令遵循 ③ 事实（自我矛盾/幻觉）—— **不含话题漂移**。§8 已按此写完 |
| Q3 | 流式文本限流阈值（50ms？N 字符？） | UI 体验 vs WS 压力 | 先 50ms，实测后调 |
| Q4 | 摘要用什么模型？主模型还是更便宜的模型？ | 成本 | 倾向用主模型（简单），量大后再拆 |
| ~~Q5~~ | ~~`LlmOptions` 放哪？~~ | —— | ✅ **已定（2026-09-22）：放 `ZipperAIQQ.Agent`** —— 见 `决策记录.md` 取消 `Llm` 项目那一条 |
| ~~Q6~~ | ~~要不要 MCP 接入？~~ | —— | ✅ **已定（2026-09-22）：要**（三类工具来源都做）。**但分期**：第一版只做 ① 自研方法，② MCP 与 ③ OS 能力留接口、后置实现 —— 见 §3.4 |
| Q7 | 事实清单（`ISessionFactStore`）要不要第一版就做？ | 事实漂移防线的成本 | 倾向**第一版只做"落库 + 常驻 system"的最小版**，冲突检测后置（§8.4） |
| Q8 | MCP 依赖包的确切版本与体积 | 依赖膨胀（MAF 教训） | 引入前**先算传递闭包**，别重蹈 32 包的坑 |

---

## 附：与既有文档的关系

| 文档 | 关系 |
|---|---|
| [`项目总览.md`](项目总览.md) | **总览**。其原 §3.4（Agent）与 §3.3（Llm）已被本文取代 → 已**归档** |
| [`archive/旧技术设计.md`](../99-归档/旧技术设计.md) | **本文取代的那两块旧设计在这里**（保留存痕，**勿参照**） |
| [`组合与注册.md`](组合与注册.md) | `AddZipperAgent` 的注册内容与装配位置 |
| [`通信层设计.md`](通信层设计.md) | 本文的事件流经它推送 |
| [`../07-记忆/决策记录.md`](../07-记忆/决策记录.md) | 决策依据（MEAI + 部分自研、不引 MAF） |
| `D:\temp\MeaiSpike` | 本文 §1.2 与 §4.1 的实测来源（spike，不在主仓库） |
