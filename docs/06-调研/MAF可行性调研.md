# Microsoft Agent Framework (MAF) 可行性调研报告

> 调研目的：评估在 ZipperAIQQ 项目（`net8.0` / Newtonsoft.Json / MS DI / 手写 agent 循环基线）中改用现成 agent 框架 MAF 的可行性。
>
> 调研方式：只读。数据来自 nuget.org 官方 API、GitHub 官方 API 与原始源码、Microsoft Learn 官方文档。
> **本报告未安装、下载、部署或修改任何依赖包。**
>
> 调研时间基准：**2026-09-22**（由 `api.github.com/repos/microsoft/agent-framework` 的 `updated: 2026-09-22T09:09:51Z` 反推）。
> 触发材料：`https://www.cnblogs.com/sheng-jie/p/19200934`（2025-11-07，带推广性质）。
> ⚠️ 该博客发布于 MAF 的 **preview 阶段**，与当前 1.22.0 已有 **11 个月**的差距，其 API 描述部分已过时（详见 C 节逐条核实）。
>
> ### ⚠️ 前提变更说明（2026-09-22 追加）
>
> 本报告的调研前提是「项目统一 `Newtonsoft.Json` + **手写 agent 循环** + `net8.0`」。**这三个前提现已全部变更**：
>
> | 前提 | 当时 | 现在 |
> |---|---|---|
> | JSON | Newtonsoft.Json | **`System.Text.Json`**（与 MEAI 同栈） |
> | Agent | 手写循环 | **MEAI + 部分自研**（**仍不引 MAF**） |
> | TFM | net8.0 | **net10.0** |
>
> **因此本报告中「与现有基线正面冲突（JSON 正面冲突）」这一核心结论已失效** —— 但那**不改变最终决策**：
> 用户选择了「**MEAI + 部分自研**」而非 MAF，理由是 **MAF 相对 MEAI 的增量收益（agent / workflow 抽象层）不抵其代价**（32 个包、issue #7733 未修、非流式中间件静默降级流式、`.Workflows` 的 BSP 屏障）。
>
> **本报告仍然有效的部分**：MAF 的依赖闭包分析、issue #7733、中间件限制、`SUPPORT.md` 支持范围、TFM 与版本节奏分析，以及 —— **MEAI 强制 STJ 并暴露到公开 API 的证据**（正是这条促成了 JSON 基线变更）。
>
> **报告正文不改**，作为历史调研记录保留。

**证据强度标注**：强 = 官方 API/源码/文档直接证据；中 = 官方文档但未在源码二次确认；弱 = 第三方转述或推断。

---

## A. 项目真实状态

### A1. MAF 的准确身份

| 项 | 值 | 证据来源 | 强度 |
|---|---|---|---|
| 仓库 | `https://github.com/microsoft/agent-framework` | nuspec `<repository type="git" url="...">`；GitHub API | 强 |
| 文档 | `https://learn.microsoft.com/agent-framework/` | nuspec `<projectUrl>` | 强 |
| 首次公开发布 | **2025-10-01**（`1.0.0-preview.251001.1`，14:17 UTC） | NuGet registration index `published` | 强 |
| 仓库创建时间 | **2025-04-28**（早于首发约 5 个月） | GitHub API `created_at` | 强 |
| 当前版本 | **1.22.0** | NuGet flat-container index.json | 强 |
| 最新发布 | **2026-09-18**（`published: 2026-09-18T17:18:42.93+00:00`） | NuGet registration index | 强 |
| 最新提交 | **2026-09-22T06:59:30Z**（`pushed_at`） | GitHub API | 强 |
| License | **MIT** | nuspec `<license type="expression">MIT</license>`；GitHub API `license.spdx_id = MIT` | 强 |
| Stars / Forks / Open issues | 13,716 / 2,360 / **647** | GitHub API | 强 |

**是否 GA？→ 是，已 GA。**

版本时间线（NuGet registration index，证据强度：强）：

```
1.0.0-preview.251001.1  2025-10-01   ← 首次公开 preview
1.0.0-preview.251107.1  2025-11-07   ← 博客发布当天
1.0.0-preview.260212.1  2026-02-12   ← 最后一版 preview
1.0.0-rc1               2026-02-20
1.0.0-rc5               2026-03-31
1.0.0                   2026-04-02   ← GA
1.1.0                   2026-04-10
...
1.22.0                  2026-09-18   ← 当前最新
```

GA 佐证（第三方，证据强度：中）：
- [Visual Studio Magazine: "Microsoft Ships Production-Ready Agent Framework 1.0 for .NET and Python"](https://visualstudiomagazine.com/articles/2026/04/06/microsoft-ships-production-ready-agent-framework-1-0-for-net-and-python.aspx)（2026-04-06）
- [AgentMarketCap: "Microsoft Agent Framework 1.0 GA: Stable APIs, LTS"](https://agentmarketcap.ai/blog/2026/04/16/microsoft-agent-framework-1-0-ga-lts-langgraph-crewai-autogen)（2026-04-16）—— 该文称 GA 日期为 2026-04-03，**与 NuGet 记录 2026-04-02 相差一天**，以 NuGet 为准。

> ⚠️ **官方文档的陈旧痕迹**：Microsoft Learn 多处安装示例仍写 `dotnet add package Microsoft.Agents.AI.Foundry --prerelease` 与 `--prerelease` 标志，但 1.22.0 已是正式版。这说明文档有未清理的 preview 时代残留（证据强度：强，见文档原文）。

**版本节奏**：GA 后 5.5 个月内发布 22 个 feature 版本（1.1.0 → 1.22.0），平均约 **每 7.5 天一个版本**（依据 NuGet published 时间戳计算，强度：强）。

### A2. 与 Semantic Kernel / AutoGen 的关系与迁移现状

**结论：博客"AutoGen + Semantic Kernel 融合"的说法准确，且两者均已进入维护模式。**

直接证据 —— 两个仓库的 README 原文（证据强度：强）：

**`microsoft/autogen` README：**
```
# AutoGen [![Maintenance Mode](https://img.shields.io/badge/status-maintenance%20mode-orange)]
> [!CAUTION]
> **⚠️ Maintenance Mode**
> AutoGen is now in maintenance mode. It will not receive new features or
> enhancements and is community managed going forward.
> New users should start with Microsoft Agent Framework.
> ... Microsoft Agent Framework (MAF) is the enterprise-ready successor to AutoGen.
```

**`microsoft/semantic-kernel` README：**
```
> [!IMPORTANT]
> Semantic Kernel is now Microsoft Agent Framework!
> Microsoft Agent Framework (MAF) is the enterprise-ready successor to Semantic Kernel.
```

**Microsoft Learn 官方表述（证据强度：强）**，来自 [Agent Framework Overview](https://learn.microsoft.com/en-us/agent-framework/overview/agent-framework-overview)：
> "Agent Framework combines AutoGen's simple agent abstractions with Semantic Kernel's enterprise features — session-based state management, type safety, middleware, telemetry — and adds graph-based workflows for explicit multi-agent orchestration. ... **Agent Framework is the next generation of both Semantic Kernel and AutoGen.**"

**仓库活跃度对比（GitHub API，证据强度：强）**：

| 仓库 | created | pushed_at | stars | open issues | archived |
|---|---|---|---|---|---|
| `microsoft/agent-framework` | 2025-04-28 | **2026-09-22** | 13,716 | 647 | false |
| `microsoft/semantic-kernel` | 2023-02-27 | 2026-09-19 | 28,587 | 312 | false |
| `microsoft/autogen` | 2023-08-18 | **2026-04-15** | 61,105 | 1,091 | false |

> **注意 AutoGen**：README 已挂维护模式牌，但 `pushed_at` 停在 **2026-04-15**——距今 **5 个多月无新提交**（证据强度：强，GitHub API）。这与"community managed、只收安全补丁"的说法一致。
>
> **注意 SK**：`pushed_at` 是 2026-09-19（活跃），但 README 已宣告被 MAF 取代。其 README 的 System Requirements 写 **".NET: .NET 10.0+"**（证据强度：强），即 SK 自身已把最低 .NET 抬到 10——这对本项目（`net8.0`）是**明确的不兼容信号**。

**迁移资源真实存在**（证据强度：强，仓库文件树）：
- `python/samples/autogen-migration/`（含 `orchestrations/01_round_robin_group_chat.py`、`03_swarm.py`、`04_magentic_one.py` 等）
- `python/samples/semantic-kernel-migration/`（含 `orchestrations/{concurrent_basic,group_chat,handoff,magentic,sequential}.py`）
- 官方迁移指南：[from-semantic-kernel](https://learn.microsoft.com/en-us/agent-framework/migration-guide/from-semantic-kernel/)、[from-autogen](https://learn.microsoft.com/en-us/agent-framework/migration-guide/from-autogen/)

> ⚠️ **重要观察**：迁移示例**只有 Python 目录**。仓库树中**没有 `dotnet/samples/*-migration/`**（证据强度：强，对 `git/trees/main?recursive=1` 全量 6657 条目的过滤结果）。.NET 侧的迁移路径缺少等价样例。

### A3. NuGet 包名与版本

**包名不是** `Microsoft.AgentFramework`，**是 `Microsoft.Agents.AI*`**（注意复数 `Agents`、`.AI` 后缀）。

查询命令（证据强度：强）：
```
https://azuresearch-usnc.nuget.org/query?q=Microsoft.Agents.AI&take=20
```

| 包 id | 最新版本 | 总下载量 | 说明 |
|---|---|---|---|
| `Microsoft.Agents.AI.Abstractions` | 1.22.0 | 9,779,876 | 接口与抽象 |
| `Microsoft.Agents.AI` | 1.22.0 | 8,218,367 | 核心功能 |
| `Microsoft.Agents.AI.OpenAI` | 1.22.0 | 3,496,439 | OpenAI 支持 |
| `Microsoft.Agents.AI.Workflows` | 1.22.0 | 3,252,056 | 工作流 |
| `Microsoft.Agents.AI.Workflows.Generators` | 1.22.0 | 223,523 | 工作流 Roslyn 源生成器 |
| `Microsoft.Agents.AI.Workflows.Declarative` | 1.22.0 | 184,514 | 声明式工作流 |
| `Microsoft.Agents.AI.GitHub.Copilot` | 1.22.0 | 95,783 | GitHub Copilot SDK |
| `Microsoft.Agents.AI.Harness` | 1.22.0 | 71,454 | HarnessAgent（长任务） |
| `Microsoft.Agents.AI.Foundry` | 1.5.0 | 439,519 | Foundry（版本号独立，滞后） |

**无关的同名干扰项**（写进报告以避免混淆，证据强度：强）：
- `Microsoft.SemanticKernel.Agents.Core` / `.Abstractions`（1.80.1）—— **SK 的旧包**，不是 MAF
- `Azure.AI.Agents.Persistent`、`Azure.AI.Projects.Agents` —— **Azure 服务 SDK**，不是 MAF
- `Microsoft.Agents.Extensions.Teams.AI`、`Microsoft.Agents.A365.*` —— **Teams / Agent365**，不是 MAF
- `AgentFrameworkToolkit`、`AgentMemory.AgentFramework` —— **第三方包**，不是微软官方

---

## B. 与本项目基线的兼容性

### B4. 目标框架（TFM）—— 【硬门槛】

## ✅ 结论：MAF 1.22.0 **支持 `net8.0`**。这不是硬阻塞。

证据：`Microsoft.Agents.AI` 1.22.0 的 nuspec 中**明确存在 `net8.0` 依赖组**（证据强度：强）。

```powershell
# 取证命令
[xml]$x = (Invoke-WebRequest -Uri `
  "https://api.nuget.org/v3-flatcontainer/microsoft.agents.ai/1.22.0/microsoft.agents.ai.nuspec" `
  -UseBasicParsing).Content
$x.package.metadata.dependencies.group | ForEach-Object { $_.targetFramework }
```

输出（真实 nuspec 片段）：
```xml
<dependencies>
  <group targetFramework="net10.0"> ... </group>
  <group targetFramework=".NETFramework4.7.2"> ... </group>
  <group targetFramework="net8.0">        ← ✅ 存在
    <dependency id="Microsoft.Agents.AI.Abstractions" version="1.22.0" />
    <dependency id="Microsoft.Extensions.AI" version="10.10.0" />
    <dependency id="Microsoft.Extensions.AI.Evaluation" version="10.10.0" />
    <dependency id="Microsoft.Extensions.Compliance.Abstractions" version="10.10.0" />
    <dependency id="Microsoft.Extensions.DependencyInjection.Abstractions" version="10.0.12" />
    <dependency id="Microsoft.Extensions.FileSystemGlobbing" version="10.0.12" />
    <dependency id="Microsoft.Extensions.Logging.Abstractions" version="10.0.12" />
    <dependency id="Microsoft.Extensions.VectorData.Abstractions" version="10.10.0" />
    <dependency id="Microsoft.ML.Tokenizers" version="2.0.0" />
    <dependency id="System.Diagnostics.DiagnosticSource" version="10.0.12" />
    <dependency id="Microsoft.Extensions.AI.Abstractions" version="10.10.0" />
    <dependency id="System.Text.Json" version="10.0.12" />
    <dependency id="System.Threading.Channels" version="10.0.12" />
  </group>
  <group targetFramework="net9.0"> ... </group>
  <group targetFramework=".NETStandard2.0"> ... </group>
</dependencies>
```

**全部 4 个核心包的 TFM 覆盖（nuspec 依赖组，证据强度：强）**：

| 包 | net10.0 | net9.0 | **net8.0** | netstandard2.0 | net472 |
|---|---|---|---|---|---|
| `Microsoft.Agents.AI` | ✅ | ✅ | **✅** | ✅ | ✅ |
| `Microsoft.Agents.AI.Abstractions` | ✅ | ✅ | **✅** | ✅ | ✅ |
| `Microsoft.Agents.AI.OpenAI` | ✅ | ✅ | **✅** | ✅ | ✅ |
| `Microsoft.Agents.AI.Workflows` | ✅ | ✅ | **✅** | ✅ | ✅ |

**源码侧二次确认（证据强度：强）** —— `dotnet/src/Microsoft.Agents.AI/Microsoft.Agents.AI.csproj` 原文：
```xml
<!-- Evaluation support requires net8.0+ (MEAI.Evaluation does not support legacy TFMs) -->
<ItemGroup Condition="$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">
  <PackageReference Include="Microsoft.Extensions.AI.Evaluation" />
</ItemGroup>
<ItemGroup Condition="!$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">
  <Compile Remove="Evaluation\**\*.cs" />
</ItemGroup>
```
MAF 自身代码里显式按 `net8.0` 做条件编译，说明 `net8.0` 是被**正式支持并测试**的目标，而非顺带兼容。

**依赖链 TFM 也已确认（证据强度：强）**：`Microsoft.Extensions.AI.Abstractions` 10.10.0、`Microsoft.Extensions.AI` 10.10.0、`Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12 **都有 net8.0 依赖组**。

> ⚠️ **但请注意措辞的边界**：以上证据来自 **nuspec 依赖组**与 **csproj 多目标条件**。nuspec 的依赖组与包内 `lib/<tfm>/` 资产严格对应，因此这是可靠证据；但本次调研**未下载 .nupkg 解包验证 `lib/net8.0/*.dll` 的实际存在**（受"禁止下载"约束）。若要 100% 落地确认，需在有网络的环境执行一次 `dotnet restore` 后检查 `project.assets.json`。

> ⚠️ **运行时注意**：MAF 的依赖里带 **`System.Text.Json 10.0.12`** 等 .NET 10 世代包。这些包**提供 net8.0 资产**，所以能在 net8.0 上运行；但这意味着你的 `net8.0` 应用会加载一整套 .NET 10 世代的 `Microsoft.Extensions.*` / `System.*` 库。这一点在"基线契合度"里是减分项（见末尾判定）。

### B5. JSON 依赖 —— 【本项目最关键的冲突点】

## ⚠️ 结论：MAF 全面且**强制**使用 `System.Text.Json`，并把 STJ 类型暴露到公开 API。这与本项目"全项目统一 Newtonsoft.Json"的基线**正面冲突**。

**证据 1：MAF 的 JSON 工具类直接以 STJ 为公开契约（证据强度：强）**

`dotnet/src/Microsoft.Agents.AI.Abstractions/AgentAbstractionsJsonUtilities.cs` 原文节选：
```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Agents.AI;

public static partial class AgentAbstractionsJsonUtilities
{
    /// <summary>
    /// Gets the default <see cref="JsonSerializerOptions"/> instance used for JSON
    /// serialization operations of agent abstraction types.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();
    ...
    // Chain in the resolvers from both AIJsonUtilities and our source generated context.
    options.TypeInfoResolverChain.Add(AIJsonUtilities.DefaultOptions.TypeInfoResolver!);
```

→ `JsonSerializerOptions` 是 **public 属性**，`AIJsonUtilities`（MEAI 的 STJ 工具类）被直接链入。这是**公开 API 层面的 STJ 强制暴露**。

**证据 2：`JsonElement` 直接出现在类型内部（证据强度：强）**

`dotnet/src/Microsoft.Agents.AI.Abstractions/AgentSessionStateBagValue.cs` 原文节选：
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(AgentSessionStateBagValueJsonConverter))]
internal class AgentSessionStateBagValue
{
    private JsonElement _jsonValue;

    public AgentSessionStateBagValue(JsonElement jsonValue) { ... }
    public JsonElement JsonValue { get; set; }
    public T? ReadDeserializedValue<T>(JsonSerializerOptions? jsonSerializerOptions = null)
    ...
}
```
→ 会话状态（`AgentSessionStateBag`）的存储格式**就是 `JsonElement`**。虽然该类是 `internal`，但 `AgentSessionStateBag` 本身是公开类型，且其序列化行为由 STJ 主导。

**证据 3：MEAI 的 content 类型同样以 STJ 为参数载体（证据强度：中）**

流式工具调用事件所用的类型来自 MEAI：
- `FunctionCallContent(string, string, IDictionary<String,Object>)` —— 见 [MS Learn API](https://learn.microsoft.com/zh-cn/dotnet/api/microsoft.extensions.ai.functioncallcontent.-ctor)
- `AIFunctionFactory.Create(...)` 的 `AIFunctionArguments` 以字典承载参数

**证据 4：MAF 的公开 JSON 配置入口只有 STJ（证据强度：强）**

`AgentAbstractionsJsonUtilities.DefaultOptions` 是 `JsonSerializerOptions`（STJ 类型）——**没有 Newtonsoft 的对等入口**。这意味着：任何需要持久化会话、checkpoint、结构化输出的场景，都必须走 STJ。

**对本项目的具体含义**：

| 影响 | 说明 |
|---|---|
| **两套 JSON 并存** | MAF/MEAI 内部走 STJ；你的 OneBot 事件、HTTP 契约 DTO、配置继续走 Newtonsoft。运行时两套序列化器共存，**不会崩**（STJ 与 Newtonsoft 是独立库，无类型系统冲突） |
| **依赖上被迫引入 STJ** | 即使你一行 STJ 代码都不写，`System.Text.Json 10.0.12` 也会作为传递依赖进入依赖图。这**直接违反** AGENTS.md §二"**不使用 `System.Text.Json`**"的字面约定 |
| **边界转换成本** | 若把 LLM 报文/会话状态经 MAF 处理后再交给 Newtonsoft 序列化，或反向传入，需要在两条 JSON 管线之间转换，增加摩擦与出错面 |
| **行为差异风险** | 两个库的默认行为不同（命名策略、null 处理、数字处理、日期格式、编码转义）。MAF 的 `DefaultOptions` 明确开了 `JsonSerializerDefaults.Web`、`IgnoreCondition.WhenWritingNull`、`NumberHandling.AllowReadingFromString`、`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`——这些**必须与 Newtonsoft 侧对齐**，否则同一份数据在两处的表现会不一致 |

> ⚠️ 补充：MAF 的 `DefaultOptions` 使用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`。源码注释自己警告："Consuming applications must ensure that JSON outputs are adequately escaped before embedding in other document formats, such as HTML and XML."（证据强度：强）。对本项目（QQ 消息文本）不构成直接风险，但需知晓。

### B6. 与 `Microsoft.Extensions.AI`（MEAI）的关系

**结论：MAF 构建在 MEAI 之上，`IChatClient` 确实是它的核心抽象之一，但 MAF 自己又加了 `AIAgent` 这一层。**（博客说"MAF = AutoGen + SK 融合"是对的，但**漏掉了 MEAI 这一层**）

证据（证据强度：强）：

1. **依赖关系**：`Microsoft.Agents.AI` 1.22.0 依赖 `Microsoft.Extensions.AI 10.10.0` + `Microsoft.Extensions.AI.Abstractions 10.10.0`（nuspec）。`Microsoft.Agents.AI.OpenAI` 还额外依赖 `Microsoft.Extensions.AI.OpenAI 10.10.0`。

2. **官方文档明示**（[Running Agents](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/running-agents)）：
   > "The Microsoft Agent Framework uses the message and content types provided by the **Microsoft.Extensions.AI** abstractions. Messages are represented by the `ChatMessage` class and all content classes inherit from the base `AIContent` class."

3. **类型表**（同上文档，证据强度：强）—— 这些**全是 MEAI 的类型**：

   | 类型 | 命名空间 | 用途 |
   |---|---|---|
   | `TextContent` | `Microsoft.Extensions.AI` | 文本内容 |
   | `DataContent` | `Microsoft.Extensions.AI` | 二进制（图/音/视） |
   | `UriContent` | `Microsoft.Extensions.AI` | URL 指向的外部内容 |
   | `FunctionCallContent` | `Microsoft.Extensions.AI` | **模型请求调用工具** |
   | `FunctionResultContent` | `Microsoft.Extensions.AI` | **工具调用结果** |

4. **分层结构**：
   ```
   AIAgent                      ← MAF 层（会话、中间件、工作流）
     └─ ChatClientAgent         ← MAF 的默认实现
          └─ IChatClient        ← MEAI 层（模型调用）
               └─ FunctionInvokingChatClient  ← MEAI 的 function-calling 循环
                    └─ OpenAI SDK / Azure SDK / Ollama / ...
   ```
   MAF 文档明确说函数调用中间件"only supported with an `AIAgent` that uses `FunctionInvokingChatClient`"——即 **agent 循环的自动工具调用由 MEAI 的 `FunctionInvokingChatClient` 承担**。

> **重要含义**：**引入 MAF 等于同时引入 MEAI**。你无法"只要 MAF 不要 MEAI"。MEAI 是 `Microsoft.Extensions.AI` 包，其 net8.0 依赖组包含 `System.Text.Json 10.0.12`、`System.Numerics.Tensors`、`System.Diagnostics.DiagnosticSource` 等。

### B7. 接入 OpenAI 兼容端点（DeepSeek）

**结论：可以，但 .NET 侧没有 `base_url` 这种一行参数；官方文档的"OpenAI 兼容端点"章节是 Python 专属。**

**关键证据：官方文档 [OpenAI-Compatible Endpoints](https://learn.microsoft.com/en-us/agent-framework/integrations/openai-endpoints) 的连接章节只给了 Python 示例**（证据强度：强）：

```python
# 官方文档原文 —— 只有 Python
agent = Agent(
    client=OpenAIChatCompletionClient(
        base_url="http://localhost:11434/v1/",  # e.g. Ollama
        api_key="not-needed",
        model="llama3.2",
    ),
    name="WeatherAgent",
    instructions="You are a helpful weather assistant.",
    tools=get_weather,
)
```
文档的 "### Connecting to OpenAI-Compatible Endpoints **(Python)**" 标题里就带 `(Python)`，Go 部分给了 `openaiprovider`，**.NET 部分缺失**。

**.NET 侧的实际路径**（证据强度：中——官方 .NET 示例只展示标准 OpenAI，DeepSeek 需经 OpenAI SDK 的 `Endpoint` 选项）：

MAF 的 .NET 示例（`dotnet/samples/02-agents/AgentProviders/openai/Agent_With_OpenAIChatCompletion/Program.cs`，证据强度：强）：
```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw ...;
var model  = Environment.GetEnvironmentVariable("OPENAI_CHAT_MODEL_NAME") ?? "gpt-5.4-mini";

AIAgent agent = new OpenAIClient(apiKey)
     .GetChatClient(model)
     .AsAIAgent(instructions: "You are good at telling jokes.", name: "Joker");

Console.WriteLine(await agent.RunAsync("Tell me a joke about a pirate."));
```

→ 对 DeepSeek，需要把 `new OpenAIClient(apiKey)` 换成带自定义 `OpenAIClientOptions { Endpoint = new Uri("https://api.deepseek.com/v1") }` 的构造。`OpenAI` 2.13.0 包本身支持该选项（这是 openai-dotnet SDK 的标准能力，证据强度：中，本次未逐行验证该 SDK 源码）。

**同时存在第二条路（不依赖 MAF 的 OpenAI 包）**：直接用 `Microsoft.Extensions.AI.OpenAI` 的 `AsIChatClient()` 构造 `IChatClient`，再交给 MAF：
```csharp
IChatClient chatClient = new OpenAIClient(
        new OpenAIClientOptions { Endpoint = new Uri("https://api.deepseek.com/v1") },
        new ApiKeyCredential(apiKey))
    .GetChatClient("deepseek-chat")
    .AsIChatClient();

AIAgent agent = chatClient.AsAIAgent(instructions: "...", name: "...");
```

**配置成本**：BaseUrl + ApiKey + 模型名，三者即可。**这一点上 MAF 与手写循环没有本质差别**——你无论走哪条路，都要自己配这三项。

> ⚠️ **注意 DeepSeek 走哪个协议**：官方文档把 **Responses API 列为"默认且推荐"**（证据强度：强），但 DeepSeek 提供的是 **Chat Completions 兼容**接口。文档也给了取舍标准：
> > "Use the **Chat Completions API** when: Migrating existing applications that rely on the Chat Completions format / You need simple, stateless request/response interactions / State management is handled entirely by your client / You're integrating with existing tools that only support Chat Completions"
>
> ⚠️ **本条结论已过期（2026-10-21 复查）**：DeepSeek 现已支持 Responses API（`/v1/responses`），下文"必须走 Chat Completions"的前提**不再成立**。原文保留以存历史。
> ~~对本项目，**必须走 Chat Completions**（DeepSeek 不支持 Responses API）。这意味着官方"推荐路径"你**用不上**。~~

---

## C. 能力与可定制性

### C8. 单 agent 定义与工具声明

**博客称 `[Tool("查询订单")]`——此写法在 MAF 中不存在。**

**核实结果（证据强度：强）**：

| 博客说法 | 实际情况 |
|---|---|
| `[Tool("查询订单")]` 特性 | ❌ **不存在**。仓库树中搜索 `dotnet/src/**/(ToolAttribute\|Attribute.*Tool\|AIFunctionFactory)` **零命中**（对全量 6657 条目过滤） |
| — | ✅ 真实写法：`AIFunctionFactory.Create(方法)` + `System.ComponentModel.DescriptionAttribute` |

**官方文档**（[Using function tools with an agent](https://learn.microsoft.com/en-us/agent-framework/agents/tools/function-tools)，证据强度：强）原文：
> "You can turn any C# method into a function tool, by using the **`AIFunctionFactory.Create`** method to create an `AIFunction` instance from the method. If you need to provide additional descriptions about the function or its parameters ... you can use the **`System.ComponentModel.DescriptionAttribute`** attribute."

```csharp
using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

[Description("Get the weather for a given location.")]
static string GetWeather(
    [Description("The location to get the weather for.")] string location)
    => $"The weather in {location} is cloudy with a high of 15°C.";

AIAgent agent = new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential())
    .AsAIAgent(
        model: model,
        instructions: "You are a helpful assistant",
        tools: [AIFunctionFactory.Create(GetWeather)]);
```

**Agent 定义方式**：
```csharp
AIAgent agent = chatClient.AsAIAgent(instructions: "...", name: "...", tools: [...]);
```
可用 `ChatClientAgent` 直接构造：`new ChatClientAgent(chatClient, instructions: "...")`（证据强度：强，middleware 文档示例）。

> **对本项目的含义**：若你已有"用特性标注工具方法"的设计，MAF 用的是 `[Description]`（BCL 类型，无框架耦合）+ `AIFunctionFactory.Create`。这**比博客说的更标准**，但也意味着你要把工具声明改成 MAF 的形态。工具若需权限门禁，MAF 提供 `ApprovalRequiredAIFunction` 包装（证据强度：强，middleware 示例）——**这与本项目"危险操作默认仅管理员"的接缝 C 直接相关，是个加分项**。

### C9. 多 agent 协作 —— 博客的 `AgentTeam` 不存在

**核实结果（证据强度：强）**：

| 博客说法 | 实际情况 |
|---|---|
| `new AgentTeam().AddAgent<T>(...)` | ❌ **不存在**。这是**已废弃的 AutoGen（Python）**时代的 API 名字，不是 MAF 的 .NET API |

**真实的 MAF 多 agent 方式**（证据强度：强，来自仓库 README 与工作流样例目录）：

README 原文：
> "**Orchestration Patterns & Workflows**: Build multi-agent systems with graph-based workflows supporting **sequential, concurrent, handoff, and group collaboration** patterns; includes checkpointing, streaming, human-in-the-loop, and time-travel"

样例目录 `dotnet/samples/03-workflows/` 实际结构（证据强度：强，仓库树）：
```
03-workflows/
  Agents/          Concurrent/      ConditionalEdges/   Declarative/
  HumanInTheLoop/  Loop/           Observability/      Orchestration/
  SharedStates/    Visualization/  Checkpoint/         Evaluation/
  Resources/       _StartHere/
```
文档侧 `learn.microsoft.com/agent-framework/workflows/orchestrations/` 覆盖 sequential / concurrent / handoff / group chat / magentic 五种编排模式（证据强度：中）。

→ **协作能力是有的，但 API 形态是"编排模式 + 图工作流"，不是 `AgentTeam.AddAgent<T>()`。**

### C10. Workflow 编排 —— `WorkflowBuilder` 存在，但 API 形态与博客不同

**核实结果（证据强度：强）**：

| 博客说法 | 实际情况 |
|---|---|
| `.Start<T>().Then<T>().Branch(...).Parallel(...).Build()` | ⚠️ **类名 `WorkflowBuilder` 真实存在，但这套链式方法签名不存在** |

**官方文档**（[Workflow Builder & Execution](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/builder-and-execution)，证据强度：强）原文：
```csharp
using Microsoft.Agents.AI.Workflows;

var processor = new DataProcessor();
var validator = new Validator();
var formatter = new Formatter();

// Build workflow
WorkflowBuilder builder = new(processor);   // 构造函数传入"起始 executor"
builder.AddEdge(processor, validator);
builder.AddEdge(validator, formatter);
var workflow = builder.Build();
```

→ 真实 API 是 **`WorkflowBuilder(startExecutor)` + `AddEdge(a, b)` + `Build()`**，即**显式的图构建（executor + edge）**，不是博客展示的 `.Start<T>().Then<T>()` 泛型链式 DSL。

**执行模型（重要，证据强度：强）**：MAF 工作流用**改良 Pregel 模型 / BSP 超步（superstep）**，**超步之间有同步屏障**：
> "Within a single superstep, all triggered executors run in parallel, but the workflow does not advance to the next superstep until every executor completes. This affects fan-out patterns: if you fan out to multiple paths — one with a chain of executors and another with a single long-running executor — **the chained path cannot advance until the long-running executor completes.**"

官方给出的绕法：
> "If you need truly independent parallel paths that don't block each other, **consolidate sequential steps into a single executor.**"

> ⚠️ **这是对本项目的一个实质约束**：如果你打算做"多路并行 agent、各自独立推进"的编排，MAF 的 BSP 屏障语义会**阻塞快路径**，官方建议的解法是"把顺序步骤合并进单个 executor"——这等于**放弃框架的编排粒度**，退回到自己写。这是"框架封死的地方"之一。

**流式与事件（这里反而是强项）**：
```csharp
StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, inputMessage);
await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    if (evt is ExecutorCompletedEvent executorComplete)
        Console.WriteLine($"{executorComplete.ExecutorId}: {executorComplete.Data}");
    if (evt is WorkflowOutputEvent outputEvt)
        Console.WriteLine($"Workflow completed: {outputEvt.Data}");
}
```

### C11. 流式与工具调用生命周期事件 —— 能力存在，但 API 不是博客描述的样子

**结论：能做，且这是 MAF 相对"手写循环"最明确的收益点。但需注意类型在 MEAI 而非 MAF，且曾有回归 bug。**

**1. 流式增量输出（证据强度：强）**

```csharp
await foreach (var update in agent.RunStreamingAsync("What is the weather in Amsterdam?"))
{
    Console.Write(update.Text);        // 增量文本
    Console.WriteLine(update.Contents.Count);  // 该 chunk 的全部内容项
}
```

`AgentResponseUpdate` 的公开成员（官方文档表格，证据强度：强）：
- `Text` —— 聚合后的文本片段
- `Contents` —— `IList<AIContent>`，**可能含 function call、function result、reasoning、status 更新等**
- `MessageId` / `ResponseId` —— 消息与响应标识

**2. 工具调用生命周期事件（证据强度：强）**

工具生命周期**不在流式 update 里单独给"开始/结束"事件对象**，而是通过 **`Contents` 里的 MEAI content 类型 + 中间件**两条路获得：

**路径 A —— 从流式内容里识别**：
```csharp
await foreach (var update in agent.RunStreamingAsync(prompt))
{
    foreach (var content in update.Contents)
    {
        switch (content)
        {
            case FunctionCallContent fcc:      // ← 调用的"开始"（含 Name / CallId / Arguments）
                // 推送 "工具开始"
                break;
            case FunctionResultContent frc:    // ← 调用的"结果"
                // 推送 "工具结束"
                break;
            case TextContent tc:
                // 推送增量文本
                break;
        }
    }
}
```
`FunctionCallContent` = "A request by an inference service to invoke a function tool"；`FunctionResultContent` = "The result of a function tool invocation"（官方文档类型表，证据强度：强）。

**路径 B —— 函数调用中间件（拿到完整的开始/结束/参数/结果/异常控制）**：
```csharp
async ValueTask<object?> FunctionCallMiddleware(
    AIAgent agent,
    FunctionInvocationContext context,
    Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
    CancellationToken cancellationToken)
{
    Console.WriteLine($"Function Name: {context!.Function.Name} - Middleware 1 Pre-Invoke");
    var result = await next(context, cancellationToken);   // ← 执行真正工具
    Console.WriteLine($"Function Name: {context!.Function.Name} - Middleware 1 Post-Invoke");
    return result;
}
```
`FunctionInvocationContext` 公开成员：`Function`、`Arguments`、`Session`、`Metadata`、`Result`、`Terminate`（官方 middleware 文档，证据强度：强）。

**3. 已知问题（证据强度：强 —— GitHub issue 原文）**

- **Issue #5794**（`state: closed`，`labels: bug, .NET, reproduced`，2026-05-13）：
  > "**.NET: [Bug]: Regression - Tool Events not being emitted correctly to the front end**"
  > "On Microsoft.Agents.AI.Hosting.AGUI.AspNetCore version 1.0.0-preview.260311.1, our copilot kit front end would display each tool call made by the remote agent, as expected. Once we update to version 1.3.0-preview.260423.1, those are not shown anymore."

  → 这是**流式工具事件曾出现回归**的真实案例，且 **issue 已被 close（修复）**。但它说明：**工具事件在版本升级中曾被破坏过**——这正是本项目 UI 实时展示的核心依赖。

> **对本项目的含义**：能满足"流式增量 + 工具调用生命周期"需求。但你想要的"开始/结束/参数/结果"四元组，**没有一个统一的 `ToolCallEvent` 对象**，而是要么自己从 `Contents` 里 case-match，要么写中间件。**这块的"便利性"没有想象中高**——你手写循环时本来就要处理这些，换成 MAF 后仍需写一层适配代码把它翻译成你 WS 的 `{type, seq, time, data}` 帧。

### C12. 可定制点 —— 这是 MAF 的强项，但有两处明确封死

**可定制（证据强度：强）—— 三层中间件，全部可选**：

| 层 | 签名 | 能做什么 | 对本项目的用途 |
|---|---|---|---|
| **Agent Run 中间件** | `Task<AgentResponse> Mw(IEnumerable<ChatMessage>, AgentSession?, AgentRunOptions?, AIAgent inner, CancellationToken)` | 拦截整个 agent run，改输入输出 | 会话上下文注入、审计日志 |
| **Function Calling 中间件** | `ValueTask<object?> Mw(AIAgent, FunctionInvocationContext, next, CancellationToken)` | 拦截**每次工具调用**，可改参数、改结果、终止循环 | ★ **接缝 C 权限门禁的落点** |
| **IChatClient 中间件** | `Task<ChatResponse> Mw(IEnumerable<ChatMessage>, ChatOptions?, IChatClient inner, CancellationToken)` | 拦截发往模型的请求 | 报文日志、token 统计、请求改写 |

注册方式（证据强度：强）：
```csharp
var middlewareEnabledAgent = originalAgent
    .AsBuilder()
    .Use(runFunc: CustomAgentRunMiddleware, runStreamingFunc: CustomAgentRunStreamingMiddleware)
    .Use(CustomFunctionCallingMiddleware)
    .Build();
```

**其他可注入点（证据强度：强）**：
- **会话存储**：`AgentSessionStore`（抽象类，仓库文件 `AgentSessionStore.cs`）、`ChatHistoryProvider`、`InMemoryChatHistoryProvider` —— 可替换成自己的 SQLite 实现，与项目的 SQLite 存储契合
- **上下文管理**：`AIContextProvider` / `MessageAIContextProvider`（可继承，注入额外消息）
- **工具执行策略**：`FunctionInvocationContext.Terminate` 可终止工具循环；`ApprovalRequiredAIFunction` 提供人工审批包装；`AgentSessionStateBag` 存状态
- **工具动态增删**：`FunctionInvocationContext.add_tools()` / `remove_tools()`（文档有述，Python 侧确认；.NET 侧同表列出，证据强度：中）
- **DI 集成**：`builder.Services.AddSingleton(agent)` 可直接用 MS DI（证据强度：强，`Agent_Step06_DependencyInjection` 样例）

**明确封死 / 受限的地方（这是本节最重要的部分）**：

1. **函数调用中间件只对特定 agent 类型生效**（证据强度：强，官方文档 Note）：
   > "Function calling middleware is currently only supported with an `AIAgent` that uses `FunctionInvokingChatClient`, for example, `ChatClientAgent`."
   → 若你自定义 `AIAgent` 实现，**权限门禁中间件会静默失效**。

2. **流式与非流式中间件不对称**（证据强度：强）：
   > "Ideally both `runFunc` and `runStreamingFunc` should be provided. When providing just the non-streaming middleware, the agent will use it for both streaming and non-streaming invocations. **Streaming will only run in non-streaming mode to suffice the middleware expectations.**"
   → 只写非流式中间件，会导致**流式退化成非流式**。对本项目的实时 UI 是**致命的**：你必须为每层中间件都实现流式版本，否则失去流式。

3. **`Use(sharedFunc:)` 无法拦截输出**（证据强度：强）：
   > "the shared middleware won't be able to intercept or override the output"

4. **终止工具循环会破坏历史一致性**（证据强度：强，官方 Warning）：
   > "Terminating the function call loop might result in your chat history being left in an inconsistent state, for example, containing **function call content with no function result content**. This might result in the chat history being unusable for further runs."
   → 若用中间件做"权限拒绝"，**必须自己补齐 result content**，否则后续轮次会坏。这是框架没有替你处理好的坑。

5. **工作流的 BSP 同步屏障**（见 C10）—— 并行路径互相阻塞，官方建议合并 executor。

6. **JSON 序列化器不可替换**（见 B5）—— 不能把 STJ 换成 Newtonsoft。

### C13. `.Result` / 同步 API（sync-over-async）

**结论：未发现公开的 sync-over-async API。全部主路径是 async。这是加分项。**

证据（证据强度：强，官方文档与样例）：
- 运行 agent：`await agent.RunAsync(...)`
- 流式：`await foreach (var u in agent.RunStreamingAsync(...))`
- 创建会话：`await agent.CreateSessionAsync()`
- 工作流流式：`await InProcessExecution.RunStreamingAsync(...)` + `await foreach (var evt in run.WatchStreamAsync())`
- 中间件全部是 `Task` / `ValueTask` / `IAsyncEnumerable`
- 样例里的同步方法（如 `static string GetWeather(...)`）是**被调用的工具方法**，不是框架 API

→ 未在任何官方文档或样例中看到 `.Result` / `.Wait()` / `GetAwaiter().GetResult()` 的**框架 API 设计**。这与本项目全异步（ASP.NET Core + WebSocket）的形态契合。

---

## D. 生产可用性

### D14. 成熟度信号

| 信号 | 值 | 评价 | 强度 |
|---|---|---|---|
| 版本语义 | **1.22.0**（GA 后 22 个 feature 版本） | ✅ 已过 1.0，语义化版本 | 强 |
| GA 日期 | 2026-04-02 | ✅ 已 GA | 强 |
| 当前稳定度 | 距 GA 约 **5.5 个月** | ⚠️ 相对年轻 | 强 |
| **版本节奏** | GA 后 **每 ~7.5 天**一个版本 | ⚠️ **非常快**，意味着持续变动 | 强 |
| LTS 承诺 | 官方 README / 第三方均称有 LTS 承诺 | ⚠️ **未在本次调研的 Microsoft Learn 页面中找到正式 LTS 政策页** | 中 |
| AutoGen README 转述 | "production-ready release: stable APIs, and a commitment to long-term support" | 中（转述，但来自官方仓库） | 中 |
| Breaking change 声明 | **Python `CHANGELOG.md` 有显式 `[BREAKING]` 标注；.NET 侧无 CHANGELOG 文件** | ⚠️ **.NET 侧缺少变更日志** | 强 |
| Open issues | 647 | 中（相对 13.7k stars） | 强 |
| 官方"生产就绪"声明 | 有（README："production-grade AI agents"；AutoGen/SK README 称 "production-ready"） | ✅ | 强 |

**Breaking change 密度（证据强度：强，`python/CHANGELOG.md` 原文）**——这是**最重要的风险信号**：

仅 `1.19.0`（2026-09-18）一个版本，就有 **4 条 `[BREAKING]`**：
```
- agent-framework-a2a, ..., -core, -declarative: [BREAKING] Make HTTP cookie
  persistence explicit for framework-owned and caller-owned clients (#8371)
- agent-framework-core: [BREAKING] Limit MCP skill archives to ZIP files and
  deprecate the MCP sampling callback (#8290, #8401)
- agent-framework-declarative: [BREAKING] Scope provider-backed MCP sessions
  per invocation (#8425)
- agent-framework-redis: [BREAKING - beta] Scope history storage keys by
  provider and session identity (#8236)
```
`1.18.0` 有 5 条 `[BREAKING]`，`1.17.0` 有 1 条，`1.9.0` 有 3 条……

CHANGELOG 头部自称遵循 SemVer：
> "The format is based on [Keep a Changelog] and this project adheres to [Semantic Versioning]."

→ **但 1.x 的 minor 版本里持续出现 `[BREAKING]`**，与 SemVer 的字面承诺存在张力。

> ⚠️ **关键缺口**：`git/trees/main?recursive=1` 全量过滤显示，仓库里**只有 `python/CHANGELOG.md`，没有 `dotnet/CHANGELOG.md`**（证据强度：强）。也就是说——**.NET 侧的 breaking change 没有官方变更日志可查**，你只能靠读 release notes 或实际编译报错发现。对"框架 breaking change 风险"这个维度，这是**实质性的透明度缺失**。

### D15. 公开生产案例

**必须先区分"官方示例"与"真实生产"**。

**A. 官方自己的东西（不算第三方生产证据）**：
- `dotnet/samples/` 下 **100+ 个样例**（01-get-started 6 个、02-agents 数十个、03-workflows 10+ 类、04-hosting、05-end-to-end）
- 官方 DevUI（浏览器调试界面）
- 官方博客 `devblogs.microsoft.com/agent-framework/`

**B. 声称的真实生产案例（证据强度：弱～中 —— 均来自第三方报道，未获一手确认）**：

来源：[AgentMarketCap "Microsoft Agent Framework 1.0 GA"](https://agentmarketcap.ai/blog/2026/04/16/microsoft-agent-framework-1-0-ga-lts-langgraph-crewai-autogen)（**商业分析博客，带倾向性**）：
> "Over 10,000 organizations already use the managed Azure AI Foundry Agent Service, with **KPMG running a multi-agent audit system called Clara AI** on the framework and **BMW reporting a 12× speedup** on test-fleet telemetry analysis after consolidating on it."

> ⚠️ 该文同时注意：这 10,000 组织用的是 **Azure AI Foundry Agent Service（托管服务）**，**不等于**在用 MAF 这个 SDK。KPMG / BMW 的说法**无一手出处**。**证据强度：弱。**

**C. 可验证的第三方采用（证据强度：中）**：
- `AgentEvalHQ/AgentEval` 仓库存在 `docs/maf-1.10.0-upgrade-plan.md` —— 一个第三方项目**真的在做 MAF 版本升级规划**（证据强度：中，搜索命中该文件路径；本次未读全文）
- Langfuse 已发布 MAF 集成（接 MAF 的 OpenTelemetry trace）—— 见上述博客（证据强度：弱）

**结论：公开可验证的"真实生产案例"证据稀薄。** 微软自家产品线（Foundry）在重度使用，但**独立第三方生产案例缺乏一手证据**。

### D16. 已知坑与限制

**已确认的 issue（证据强度：强，GitHub API 原文）**：

**① #7733（`state: open`，2026-08-18，label: .NET）—— 与本项目 ASP.NET Core 场景直接相关**
> "**.NET: [Bug]: Protocol hosting ignores AIAgent lifetimes and lacks consistent request-time selection**"
>
> "`AddAIAgent` explicitly supports `Singleton`, `Scoped`, and `Transient` registrations through its `ServiceLifetime` parameter. ASP.NET Core creates one request scope and exposes it through `HttpContext.RequestServices`. **The Agent Framework protocol hosting integrations currently bypass that request scope and resolve hosted agents or protocol services from the root provider.**"
>
> "This means a scoped agent registration is either **rejected by scope validation or effectively promoted/captured beyond its configured lifetime**. It also prevents agent factories from consuming request-scoped services such as `DbContext`, tenant configuration, authenticated request state, or scoped tools."

→ **这是 open 状态、截至 2026-09-22 未修**。对本项目（ASP.NET Core + MS DI + 需要按请求注入用户身份/权限上下文做门禁）**是明确的架构风险**：MAF 的 hosting 集成会绕过请求作用域。虽然本项目可以不用 MAF 的 hosting 集成（自己写 HTTP 端点），但这说明**MAF 与 ASP.NET Core DI 作用域的整合还不干净**。

**② #5794（`state: closed`，2026-05-13，label: bug, .NET, reproduced）—— 工具事件回归**
> 见 C11。已修，但**流式工具事件曾被版本升级破坏**。

**③ 从官方文档直接读出的限制（无需 issue，证据强度：强）**：
- 函数调用中间件仅支持 `ChatClientAgent` 等基于 `FunctionInvokingChatClient` 的 agent
- 仅写非流式中间件 → 流式退化为非流式
- 终止工具循环会留下"有 call 无 result"的历史，导致后续 run 不可用
- 工作流 BSP 同步屏障会阻塞快路径
- JSON 只能是 STJ
- OpenAI 兼容端点的**连接**文档只有 Python

**④ 支持政策限制（证据强度：强，`SUPPORT.md` 原文）**：
> "AI Support team will support Microsoft Agent Framework issues for customers under a **Unified support agreement when the issue arises from usage of Azure AI services** ... Conversely, if customer has any other / non unified support agreement and/or **Agent Framework SDK is used in a way not involving an Azure service**, it is treated as a **purely open-source tool – Microsoft's support organization will not handle it**, and users should use GitHub or forums for assistance."

→ **本项目是自托管 + DeepSeek（非 Azure）**，因此**不在微软支持范围内**。你得到的支持 = GitHub issue + 社区，与任何开源库一样。**这一点很重要**：不要以为"微软出品"就有企业级支持兜底。

### D17. 依赖体积

**计算方法**：从 `Microsoft.Agents.AI.OpenAI` 1.22.0 出发，递归抓取每个包 **net8.0 依赖组**的 nuspec，去重计数。

**结果：`net8.0` 下传递闭包共 32 个包**（证据强度：强，脚本枚举）

```
google.protobuf 3.30.2
microsoft.agents.ai 1.22.0
microsoft.agents.ai.abstractions 1.22.0
microsoft.agents.ai.openai 1.22.0
microsoft.extensions.ai 10.10.0
microsoft.extensions.ai.abstractions 10.10.0
microsoft.extensions.ai.evaluation 10.10.0
microsoft.extensions.ai.openai 10.10.0
microsoft.extensions.caching.abstractions 10.0.12
microsoft.extensions.compliance.abstractions 10.10.0
microsoft.extensions.configuration.abstractions 10.0.9
microsoft.extensions.dependencyinjection.abstractions 10.0.12
microsoft.extensions.diagnostics.abstractions 10.0.9
microsoft.extensions.fileproviders.abstractions 10.0.9
microsoft.extensions.filesystemglobbing 10.0.12
microsoft.extensions.hosting.abstractions 10.0.9
microsoft.extensions.logging.abstractions 10.0.12
microsoft.extensions.objectpool 8.0.31
microsoft.extensions.options 10.0.9
microsoft.extensions.primitives 10.0.12
microsoft.extensions.vectordata.abstractions 10.10.0
microsoft.ml.tokenizers 2.0.0
openai 2.13.0
system.clientmodel 1.15.0
system.diagnostics.diagnosticsource 10.0.12
system.io.pipelines 10.0.12
system.memory.data 10.0.9
system.net.serversentevents 10.0.12
system.numerics.tensors 10.0.12
system.text.encodings.web 10.0.12
system.text.json 10.0.12
system.threading.channels 10.0.12
```

**重量级依赖（逐条评估）**：

| 依赖 | 重量 | 说明 |
|---|---|---|
| **`Microsoft.ML.Tokenizers 2.0.0`** | 🔴 重 | 引入 **`Google.Protobuf 3.30.2`**（原生 protobuf 运行时）。这是 ML 分词器，为 token 计数服务 |
| **`OpenAI 2.13.0`** | 🟡 中 | 官方 OpenAI SDK，带 `System.ClientModel 1.15.0` |
| **`System.ClientModel`** | 🟡 中 | Azure SDK 体系的客户端基础设施 |
| **`Microsoft.Extensions.AI.Evaluation`** | 🟡 中 | 评估框架——**本项目大概用不到，但 net8.0 下会被强制拉入** |
| **`Microsoft.Extensions.VectorData.Abstractions`** | 🟡 中 | 向量数据抽象——**本项目用不到（无 RAG 计划），也会被拉入** |
| **`Microsoft.Extensions.Compliance.Abstractions`** | 🟡 中 | 合规/数据分类——**用不到，也会被拉入** |
| **OpenTelemetry** | 🟡 中 | ⚠️ **注意**：`Microsoft.Agents.AI.Workflows` 直接依赖 `OpenTelemetry.Api 1.18.0`（nuspec 确认）。若引 Workflows 包，会带进 OTel |
| **Azure SDK** | ✅ 无 | **`Microsoft.Agents.AI` / `.OpenAI` 核心包不带 Azure SDK**。Azure 仅在你显式引 `.Foundry` / `Azure.AI.*` 时才进来 |
| **Newtonsoft.Json** | — | 不在闭包内（因为 MAF 全线用 STJ） |

**若额外引入 `Microsoft.Agents.AI.Workflows`**，还会增加 `OpenTelemetry.Api 1.18.0`（证据强度：强，nuspec）。

> **对比**：本项目"手写 agent 循环"的 AI 侧依赖 ≈ `Newtonsoft.Json` + `System.Net.WebSockets.Client` + 可能的 `Microsoft.Extensions.Http`。**MAF 是 32 个包。**
>
> ⚠️ 但其中 `microsoft.extensions.*` 的多数包（`dependencyinjection.abstractions`、`logging.abstractions`、`options`、`configuration.abstractions`、`hosting.abstractions`、`primitives`、`caching.abstractions`）**ASP.NET Core 8 项目本来就已经有了**（只是版本会被提升到 10.x）。**净新增的重量级依赖主要是**：`Microsoft.ML.Tokenizers` + `Google.Protobuf` + `OpenAI` + `System.ClientModel` + `MEAI` 三件套（`AI` / `AI.Abstractions` / `AI.Evaluation`）+ `VectorData.Abstractions` + `Compliance.Abstractions`。

---

## 【对本项目的可行性判定】

### 一、硬门槛：能不能在 `net8.0` 上用？

## ✅ **能。`net8.0` 不是阻塞项。**

`Microsoft.Agents.AI` 1.22.0 的 nuspec 里有**明确的 `net8.0` 依赖组**，且 MAF 自己的 csproj 里用 `IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0')` 做条件编译——说明 `net8.0` 是被**一等公民对待**的目标，不是顺带兼容。

**同时确认**：`Microsoft.Agents.AI.Abstractions`、`.OpenAI`、`.Workflows` 四个核心包**全部有 `net8.0` 组**；依赖链上的 `Microsoft.Extensions.AI.Abstractions` 10.10.0、`Microsoft.Extensions.AI` 10.10.0、`Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12 **也都有 `net8.0`**。

→ **"因为开发机没有 .NET 9 所以不能用 MAF"这个担心是不成立的。** MAF 不需要 .NET 9，它在 `net8.0` 上有资产。

> 唯一残留的不确定性（诚实标注）：本次受"禁止下载"约束，**未解包 `.nupkg` 直接确认 `lib/net8.0/*.dll` 的物理存在**。nuspec 依赖组与 `lib/<tfm>/` 目录严格对应，因此判定可靠度高；但若要 100% 落地，建议在 G2 阶段做一次 `dotnet restore` 实测（只读验证，不改代码）。

### 二、采用它要接受的条件与代价

**必须接受的条件（不可协商）：**

1. **引入 `System.Text.Json`，违反 AGENTS.md §二"不使用 System.Text.Json"的字面约定。**
   这不是"可能"，是**必然**——MAF 的 `AgentAbstractionsJsonUtilities.DefaultOptions` 是 `JsonSerializerOptions`（公开 API），会话状态内部用 `JsonElement`，MEAI 的 content 类型也走 STJ。要改基线文档，或接受"Newtonsoft 管业务 DTO / STJ 管框架内部"的双轨制。

2. **依赖体积从 ~3 个包扩到 32 个包**，其中 `Microsoft.ML.Tokenizers` + `Google.Protobuf` 是真正的重量级新增。

3. **`Microsoft.Extensions.*` 从 8.x 被提升到 10.x**（`DI.Abstractions 10.0.12`、`Logging.Abstractions 10.0.12` 等）。这是**运行时行为的实质变更**，不只是版本号。

4. **接受 .NET 侧无 CHANGELOG 的现实**：breaking change 只能靠编译报错和 release notes 发现。

5. **接受微软不提供支持**：`SUPPORT.md` 明确说非 Azure 场景"treated as a purely open-source tool – Microsoft's support organization will not handle it"。你的项目正好是非 Azure。

6. **接受快速版本节奏**：GA 后 5.5 个月 22 个版本，且 minor 版本里持续出现 `[BREAKING]`。

**代价（工作量与复杂度）：**

7. **`net8.0` + STJ 10.x 的组合不在主流验证路径上**——MAF 的主推组合是 net10.0 + .NET 10。你走的是"次新 TFM"路径，遇到问题时社区里能帮你的人更少。

8. **中间件必须同时写流式和非流式两份**，否则流式**静默退化为非流式**——对本项目的实时 UI 是致命的。这意味着你要为每层中间件写两遍。

9. **工作流 BSP 屏障**会阻塞并行快路径，官方解法是"合并 executor"——即**为了绕开框架限制而放弃框架的编排能力**。

10. **issue #7733（open）**：MAF 的 hosting 集成绕过 ASP.NET Core 请求作用域。本项目若要用 MAF 的 hosting 集成 + 按请求注入权限上下文（门禁需要），会踩到它。绕法是自己写端点、不用它的 hosting 集成。

11. **工具调用的"开始/结束/参数/结果"没有统一事件对象**，仍需自己 case-match `Contents` 或写中间件，再翻译成你 WS 的 `{type, seq, time, data}` 帧。**这部分适配代码你无论如何都要写。**

12. **DeepSeek 必须走 Chat Completions**，而官方"默认推荐"是 Responses API——你会在官方文档定位为"兼容旧格式"的路线上。

**博客陈述的纠错（避免按错误信息做决策）：**

| 博客说法 | 真实情况 |
|---|---|
| `[Tool("查询订单")]` | ❌ 不存在。真实是 `AIFunctionFactory.Create(...)` + `[Description]` |
| `new AgentTeam().AddAgent<T>(...)` | ❌ 不存在。真实是 workflow 编排模式（sequential/concurrent/handoff/group） |
| `.Start<T>().Then<T>().Branch(...).Parallel(...).Build()` | ⚠️ 类名 `WorkflowBuilder` 真实，但 API 是 `new WorkflowBuilder(startExecutor)` + `AddEdge(a,b)` + `Build()` |
| "MAF = AutoGen + SK 融合" | ✅ 准确（官方文档原文确认），但**漏了 MEAI 这一层**——MAF 是建在 MEAI 之上的 |

> 博客发布于 2025-11-07，那时 MAF 还是 `1.0.0-preview.251107.1`。**今天（1.22.0）的 API 已经不同**。任何基于该博客的 API 记忆都不可靠。

### 三、对比表：MAF vs 手写 agent 循环

| 维度 | 采用 MAF 1.22.0 | 手写 agent 循环（当前基线） |
|---|---|---|
| **开发工作量** | 首版**更少**（agent/工具/会话/流式开箱即用）；但**适配层不能省**——工具事件要 case-match，中间件要写两份，WS 帧要自己映射。净收益估计**中等** | 首版**更多**（要自己实现工具循环、SSE 解析、会话管理）；但**每一行都是你要的**，无适配层 |
| **可控性** | ⚠️ **中**。三层中间件给了很强的手伸进去的能力，但：函数中间件只对 `ChatClientAgent` 生效、流式/非流式不对称、终止循环会破坏历史一致性、工作流有 BSP 屏障 | ✅ **完全**。循环的每一次迭代、每一个决策点都在你手里 |
| **可定制性** | ⚠️ **中高**。中间件 + `AgentSessionStore` + `AIContextProvider` 可替换；但**JSON 序列化器不可换**（锁死 STJ），**工作流并行语义不可改**（BSP） | ✅ **完全**。包括 JSON 库、循环语义、并行模型 |
| **调试难度** | 🔴 **更高**。13.7k stars / 647 open issues / 32 个包的调用栈；出错时要在"你的代码 / MAF / MEAI / OpenAI SDK"四层之间定位。且 .NET 侧无 CHANGELOG，行为变更无迹可寻 | ✅ **更低**。栈只有你的代码 + HTTP/WS，出问题必然在自己代码里 |
| **依赖体积** | 🔴 **32 个包**（含 `Microsoft.ML.Tokenizers` + `Google.Protobuf` 两个重量级），并把 `Microsoft.Extensions.*` 从 8.x 抬到 10.x | ✅ **~3 个包**，全部锁定在 8.x |
| **版本风险（breaking change）** | 🔴 **高**。GA 后 5.5 个月 22 个版本；`python/CHANGELOG.md` 里 minor 版本持续出现 `[BREAKING]`；**.NET 侧无 CHANGELOG**。对你的"阶段即提交、不轻易 churn"节奏是持续摩擦 | ✅ **零**。你的代码只有你会改 |
| **学习成本** | 🟡 **中高**。要掌握：`AIAgent` / `ChatClientAgent` / `IChatClient` / `AIFunctionFactory` / `AIContent` 体系 / 三层中间件 / `WorkflowBuilder` + 超步模型 / `AgentSession` / `AgentSessionStore`。**且官方文档混排 Python/Go/.NET，.NET 示例常被夹在中间** | ✅ **低**。你只需要懂 OpenAI Chat Completions 协议 |
| **与现有基线的契合度** | 🔴 **低**。`net8.0` ✅（能跑）；**Newtonsoft ❌（正面冲突，STJ 无法避开）**；**MS DI ⚠️（能接，但 hosting 集成绕过请求作用域，issue #7733 open）** | ✅ **完全契合**。三者都是按现有基线设计的 |
| **安全接缝 C（权限门禁）** | ✅ **加分**。`FunctionInvocationContext` 中间件是**天然的门禁落点**，`ApprovalRequiredAIFunction` 现成可用。但要自己补"拒绝时的 result content"，否则历史坏掉 | ✅ 自己实现，完全可控 |
| **流式 + 工具事件（UI 基础）** | 🟡 **可用但有坑**。`AgentResponseUpdate.Contents` + `FunctionCallContent`/`FunctionResultContent` 能拿到；但**曾有回归 bug（#5794）**，且无统一事件对象 | ✅ 自己解析 SSE，形态由你定 |
| **微软支持** | ❌ **无**（非 Azure 场景 = 纯开源，`SUPPORT.md` 原文） | — |

### 四、我的倾向与理由

> **以下是建议，不是替你做决定。** 按 AGENTS.md §四，选择权在你。

## 倾向：**当前阶段不建议引入 MAF。**

**理由（按权重排序）：**

1. **最硬的一条：`Newtonsoft.Json` 与 MAF 的 `System.Text.Json` 是正面冲突，而这不是能"绕开"的问题。** MAF 把 `JsonSerializerOptions` 放在公开 API 上、把 `JsonElement` 放进会话状态内部。你可以接受双轨制（Newtonsoft 管业务、STJ 管框架），但那样你就**主动放弃了自己刚定下的"全项目统一 JSON"这条基线的价值**。而这个基线是有正当理由的——统一序列化器能消除一类隐蔽 bug。

2. **收益与代价不成比例。** 你需要的核心能力——DeepSeek 走 OpenAI 兼容端点、工具调用循环、流式增量、工具生命周期事件、会话上下文、权限门禁——**手写循环全部能实现，且每一行都精确符合你的需求**。MAF 的收益主要在"少写一些胶水"，但代价是 **+29 个包、STJ 强制引入、`Microsoft.Extensions.*` 大版本跃迁、每 7.5 天一个版本、.NET 侧无 CHANGELOG**。对一个"手写循环"级别的工作量，这个交换不划算。

3. **你的实时 UI 需求与 MAF 的中间件设计有结构性摩擦。** 只写非流式中间件会导致**流式静默降级**——这意味着每层中间件要写两遍。而工具事件仍要你自己 case-match、自己翻译成 WS 帧。**"框架帮你省掉"的那部分，恰恰是你最在意的部分，它没省掉。**

4. **调试栈从 2 层变 4 层。** 现在是"你的代码 → DeepSeek HTTP"。引入 MAF 后是"你的代码 → MAF → MEAI → OpenAI SDK → HTTP"。出问题时定位成本显著上升，尤其在一个 .NET 侧没有 CHANGELOG 的库上。

5. **issue #7733（open）踩中了你的架构。** 你要做的门禁需要按请求区分管理员/普通用户，天然需要请求作用域注入。MAF 的 hosting 集成绕过请求作用域，且这个 issue 至今未修。绕开它要放弃 MAF 的 hosting 集成——那 MAF 又少了一块价值。

**但如果你仍然考虑采用，我建议这样降低风险：**

- **不要用 `Microsoft.Agents.AI.Hosting.*`**（避开 #7733），自己写 HTTP/WS 端点，MAF 只当 in-process 的 agent 引擎
- **不要引 `Microsoft.Agents.AI.Workflows`**（避开 `OpenTelemetry.Api`，也避开 BSP 屏障），只用 `Microsoft.Agents.AI` + `.OpenAI`
- **先在 `docs/06-调研/` 里做一个 spike**：只验证"DeepSeek 走 `OpenAIClientOptions.Endpoint` → `AsAIAgent` → 流式工具事件能拿到 `FunctionCallContent`/`FunctionResultContent`"这一条链路，**不动主代码**
- **把 `Newtonsoft` / `STJ` 的边界写进架构文档**：明确哪些 DTO 走哪个序列化器，以及两边的 naming/null/number 策略如何对齐

**如果最终决定维持手写循环**，那么这次调研的净收益是：**你确认了 MAF 在 net8.0 上可用（排除了一个错误的阻塞假设）**，并且**明确了将来若要切换，代价集中在 JSON 与 DI 作用域两处**。可以把这个结论记进 `docs/07-记忆/决策记录.md`，并保留"未来重新评估"的入口——尤其是等 `System.Text.Json` 这条约束松动、或 issue #7733 关闭之后。

---

## 附：本次调研未能查证的事项（如实声明）

1. **未解包 `.nupkg` 验证 `lib/net8.0/*.dll` 的物理存在** —— 受"禁止下载"约束。nuspec 依赖组是强证据，但非最终证据。
2. **未逐行验证 `openai-dotnet` 2.13.0 的 `OpenAIClientOptions.Endpoint` 在 DeepSeek 上的实际表现** —— 该 SDK 支持自定义端点是一般认知（强度：中），但未实测。
3. **未找到微软官方的 LTS 政策正式页面** —— 关于"LTS 承诺"，仅有 AutoGen/SK README 的转述（"a commitment to long-term support"）与第三方博客的报道，**Microsoft Learn 上未见正式的 LTS 支持周期页面**。
4. **未读完 `python/CHANGELOG.md` 全文**（约 60KB，被截断）—— 已读到 1.4.0 及若干后续版本，`[BREAKING]` 密集的结论基于已读部分。
5. **未系统性统计 647 个 open issues 的分类** —— 只深读了 #5794、#7733 两个与 ASP.NET Core / 流式工具事件最相关的。
6. **`.NET` 侧无 CHANGELOG** —— 已确认为"不存在该文件"，而非"未找到"。
7. **未能核实 KPMG / BMW 案例** —— 仅第三方博客转述，无一手出处，故标注证据强度为弱。
