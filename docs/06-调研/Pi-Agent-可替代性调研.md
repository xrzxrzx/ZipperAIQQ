# Pi（`pi-agent-core` / `pi-coding-agent`）可替代性调研

> **调研目的**：评估能否用 Pi（`earendil-works/pi`）替换本项目的部分能力，让 Agent 侧更完善。
> **调研方式**：**只读**。数据来自 GitHub REST API、npm registry 元数据、npm 包内 README（官方文档随包分发）。**未安装、未运行、未下载任何包。**
> **时间基准**：2026-11-03（`api.github.com/repos/earendil-works/pi` 的 `pushed_at` = `2026-11-02T23:53:09Z`）
> **证据强度**：强 = GitHub API / npm registry 直接读出；中 = 官方 README / 官方文档原文；弱 = 第三方文章或推断。

## 结论（先看这段）

1. **Pi 替代不了本项目的「产品本体」**：通道（接缝 B）、权限门禁（接缝 C）、SQLite 存储契约、定时调度、记忆/RAG、WinUI3 客户端 —— Pi **明确不做**这些，也做不了。
2. **Pi 真正值得拿的是「回合与上下文管理」这一层**：`steer()` / `followUp()` 两种投递语义、`turn_start`/`turn_end` 事件粒度、`transformContext` 压缩钩子、`abort()`/`waitForIdle()`。这正好覆盖**未拍板的 R-04（思考中插入消息）与 R-10（轮次机制）**。
3. **最省事的做法不是集成，是抄语义**：把 R-04/R-10 的方案从「A 分层 / B 并行预判断 / C 组合」改写成 Pi 的两队列语义（steer = 引导，followUp = 跟进），我们自己的 `CancellationToken` 队列已经够用，**零跨语言成本**。
4. **若真要跑 Pi，唯一站得住的形态是「后台 sidecar + 无副作用任务」**（`pi-agent-core` 只有 3 个依赖），主线 Agent 留在 C#。主线替换不成立（见 §C3 六条硬障碍）。
5. **一个额外发现**：`pi-ai` 支持订阅 OAuth（Claude / ChatGPT / Copilot 额度），这是本项目**唯一可能省钱**的替代点，但涉及供应商条款与跨语言，建议单独立项，不在本轮。

---

## A. Pi 的准确身份

### A1. 仓库与包

| 项 | 值 | 来源 | 强度 |
|---|---|---|---|
| 仓库 | `https://github.com/earendil-works/pi`（原 `badlogic/pi-mono`，作者 Mario Zechner，现属 Earendil Inc.） | GitHub API `full_name` | 强 |
| 定位 | 「AI agent toolkit: unified LLM API, agent loop, TUI, coding agent CLI」 | GitHub API `description` | 强 |
| License | **MIT** | GitHub API `license.spdx_id` | 强 |
| 语言 | TypeScript | GitHub API `language` | 强 |
| Stars / Forks / Open issues | **119,651** / **15,358** / **316** | GitHub API | 强 |
| 创建 / 最后推送 | 2025-08-09 / **2026-11-02**（活跃） | GitHub API `created_at` / `pushed_at` | 强 |
| 归档 | 否 | GitHub API `archived=false` | 强 |

### A2. 包矩阵（npm，当前 `1.5.0`）

| 包 | 版本 | License | 直接依赖数 | 依赖里有什么 | 对我们的意义 |
|---|---|---|---|---|---|
| `@earendil-works/pi-coding-agent` | 1.5.0 | MIT | **22** | `pi-ai` `pi-agent-core` `pi-mcp` `pi-tui` `pi-codemode` typebox / undici / chalk … | 完整 CLI（TUI + 会话树 + 压缩 + 扩展），**重** |
| `@earendil-works/pi-agent-core` | 1.5.0 | MIT | **2** | 只有 `pi-ai` + `pi-schema` | **轻**，是「嵌进自有应用」的正确入口 |
| `@earendil-works/pi-ai` | 1.5.0 | MIT | 10 | `openai` / `@anthropic-ai/sdk` / `@google/genai` / `@aws-sdk/client-bedrock-runtime` … | 统一 LLM API + 多家 provider |

- 运行时要求：**Node ≥ 22.19.0**（`engines.node`）。⚠️ 本项目的抖音 sidecar 基准是 **Node 22.5+**（`多平台通道接入-需求.md` 决策二），再跑 Pi 需要把 Node 抬到 22.19。**强**
- 与 2026-08 第三方文章（`news.qiniu.com`）相比：**包名已从 `@mariozechner/*` 迁到 `@earendil-works/*`，版本从 0.x 到 1.5.0**。该文描述的「四种运行模式、会话树、compaction 默认值」是 0.x 时期的信息，**不能当 1.5.0 的规格用**。**弱**

### A3. 设计哲学（官方原文）

> 「Pi is a minimal, extensible agent harness that you can make your own.」
> 「Pi ships with powerful defaults but **skips features like sub-agents and plan mode**.」 —— 中

四种用法：交互 TUI / print-json / **RPC**（`pi --mode rpc`，stdin-stdout JSON）/ **TypeScript SDK**（`pi-agent-core`）。**中**

---

## B. 功能对照：我们缺的 vs Pi 有的

| # | 我们的能力 / 待办 | 我们现状（证据） | Pi 对应 | 判定 |
|---|---|---|---|---|
| 1 | Agent 工具循环 | MEAI `FunctionInvokingChatClient`（决策 2026-09-22） | `Agent` + 自动工具循环（`pi-agent-core` README） | **已有，不替代** |
| 2 | **思考中插入消息（R-04）** | 排队合并；三方案未拍板（`docs/03-设计/思考中新消息处理策略.md`） | **`agent.steer()`**：工具执行中注入，**当前 assistant 的工具调用全部完成后**合并，下一轮回应；**`agent.followUp()`**：等这轮本会结束时再投递；`steeringMode` / `followUpMode` = `one-at-a-time`（默认）/ `all` | ✅ **直接借鉴（蓝本级）** |
| 3 | **轮次机制（R-10）** | 一轮 = 一条用户消息；无挂起等待 | `turn_start` / `turn_end` / `agent_end` 事件；`abort()` + `waitForIdle()`；`finishTurn` 返回 `{continue}` / `{end}` 决定是否再要一轮 | ✅ **借鉴语义** |
| 4 | 上下文压缩 | `SummaryService` + 自研策略 | `transformContext()` 钩子（`pi-agent-core`）；`compaction`（`pi-coding-agent` 层，token 预算 + 结构化摘要） | ⚠️ **借鉴形式（钩子 + 预算），不搬实现** |
| 5 | 会话历史 | SQLite `Messages` + 检索工具（**客户端契约**） | JSONL 会话**树**（fork / resume / tree） | ❌ **不替代**（数据模型冲突），形态可选做 |
| 6 | **权限门禁（接缝 C）** | `PermissionGate` + `RoleResolver`，服务端强制 | **Pi 没有内置权限系统**（官方 FAQ：默认以启动用户权限运行，靠外部沙箱） | ❌ **绝不可替代** |
| 7 | 多平台通道（接缝 B） | `IMessageChannel` / NapCat / 抖音 sidecar / 邮件 | 无 | ❌ 不可替代 |
| 8 | 定时调度（R-03） | `SchedulePoller`（DB 即队列） | 无 | ❌ 不可替代 |
| 9 | 记忆 / RAG（R-09） | `MemoryRetriever` + ONNX embedding | 无（只有上下文压缩，不是长期记忆） | ❌ 不可替代 |
| 10 | 发送节流 / 长度（R-11） | `SendThrottle`、`MaxMessagesPerCall=5`、按空行切 | 无发送概念 | ❌ 不可替代 |
| 11 | 客户端 UI | WinUI3（FR-20） | TUI（自绘，非 Ink） | ❌ 不可替代 |
| 12 | 模型接入 | MEAI + OpenAI 兼容端点（DeepSeek）+ JEV 决策端点 | `pi-ai`：15+ provider，**订阅 OAuth 登录**（Claude Pro/Max、ChatGPT、Copilot） | 🔶 **唯一可能省钱的点**，单独评估 |
| 13 | MCP | 走 MEAI 的 MCP 生态 | 独立包 `@earendil-works/pi-mcp` | 🔶 互操作可能，非替代 |
| 14 | Skills / 扩展生态 | 无（工具目录 + 人设 + 命令） | Skills（`SKILL.md` 渐进式披露）、Extensions（TypeScript，可拦/改工具调用、自定义压缩） | 🔶 可选借鉴（暂不需要） |

> **第 2 行是本轮最有价值的发现**：我们为了「思考中插话」写了三个方案（分层 / 并行预判断 / 组合），还跑了一次 JEV（置信度 0.31 未拍板）。Pi 的做法**不是选方案，而是换问题** —— 不问「要不要打断」，只问「这条消息投哪个队列」：`steer` = 打断引导（工具步骤之间插入），`followUp` = 排队跟进（这轮结束再插）。冲突自然消失，剩下的只是 `one-at-a-time` 还是 `all`。

---

## C. 三个整合方案

### C1 · 只借语义（C# 自研实现）——**推荐做，成本最低**

**做什么**

1. 把 R-04 的三方案**作废**，改写为 Pi 语义：两条队列（steering / follow-up）+ 两种投递模式（一条 / 全投）；
2. R-10 借用 `turn_start`/`turn_end` 粒度与 `finishTurn` 的「continue / end」决策点，映射到我们现有的轮次终止条件；
3. 压缩沿用「钩子 + token 预算」：`transformContext` 等价物 = 我们 `IChatReducer` 之前的一层可插拔预处理。

**代价**：纯设计改动，0 新依赖、0 跨语言。
**收益**：R-04/R-10 从「三个方案拍不了板」变成「一个语义直接实现」，并且与我们已有 `CancellationToken` + 会话队列吻合。
**风险**：低。唯一注意：Pi 的 `steer` 是**工具步骤之间**注入（不是立即硬打断），这个细节决定了「已发出的消息不会撤回」—— 与 `思考中新消息处理策略.md` §五「状态回滚」的结论一致。

### C2 · 后台 sidecar 试点（`pi-agent-core`）——**JEV 倾向，可试**

**做什么**

把**无副作用、重上下文**的任务交给 Pi 跑：记忆整理 / 资料摘要 / 文档分析 / 长文审查。服务端通过 **RPC**（`pi --mode rpc`）或 Node SDK 调用，结果回到 C# 落库。

**为什么可行**

- `pi-agent-core` 只有 **2 个直接依赖**，不是「装一整套 CLI」；
- 我们已经有 Node 进程外集成的先例（抖音 sidecar），运维形态熟悉；
- 共享的工具若需要副作用（写库、发消息），**由 C# 侧以 MCP server 暴露**（`pi-mcp` 存在，MEAI 也吃 MCP）→ **权限门禁仍在服务端**（接缝 C 保住）。

**代价**：多一个进程 + 一套 JS 工具链；Node 需 ≥ 22.19；日志/排障分两处。
**收益**：真拿到 Pi 的压缩与长任务能力，且不动主线。
**风险**：中。Pi 1.x 虽比 0.x 稳，但 316 个 open issues；RPC 协议是它的私有约定，升级可能破坏。

### C3 · 主对话 Agent 整体替换（服务端降为通道壳）——**不建议**

六条硬障碍：

| # | 障碍 | 说明 |
|---|---|---|
| 1 | **接缝 C 失守** | Pi 无内置权限系统；门禁要么写进 JS 扩展（不在我们的代码审查里），要么全靠 MCP 回调（等于没替换） |
| 2 | **接缝 B 无关** | 通道、平台差异、群/私聊语义全在 C#，Pi 一点不管 |
| 3 | **存储契约冲突** | 客户端查 SQLite（会话页、审计页、数据页），Pi 用 JSONL 会话树 —— 要么双写，要么改客户端契约 |
| 4 | **状态分裂** | 一轮对话的状态在 Node 进程里，重启/崩溃恢复要重做；我们现在的「DB 即真相」被打破 |
| 5 | **可观测性** | 现有指标/审计/`/status` 全在 C# 侧；Pi 的事件流要重新接一遍 |
| 6 | **两套语言维护** | `Agent` 相关的一切（提示词、工具、压缩、指标）都会分裂到 TS + C# 两边 |

结论：**投入 × 收益不成立**，与「不引 MAF」的判断同构（那次否掉的理由就是「增量收益不抵代价」）。

---

## D. JEV 决策输入（**只是输入，不替你拍板**）

用 `typesafe/jev-1.13` 跑了一次（模型 `jev-1.13`，未真机验证任何实现）：

| 问题 | 结果 |
|---|---|
| 引入形态 | 后台 sidecar 试点 **0.60** / 只借语义 **0.35** / 主链路替换 **0.04** / 先不做 0.01，**confidence 0.47** |
| Pi 的 steering 语义值得当 R-04 蓝本？ | **0.53**（掷硬币，等于「信息不足」） |

**怎么读这两个数**：`0.47` 的置信度说明证据不够强，**倾向不可当作结论**。我的读法（供参考，不是决定）：**两件事并行不冲突** —— 先做 C1（零成本、立刻解 R-04/R-10 的僵局），同时用 C2 做一个 2 天规模的试点验证；C3 直接排除。

> 这正是本项目既有的节奏：先小闭环验证，再谈大改（参考 `docs/06-调研/MAF可行性调研.md` 的结论方式）。

---

## E. 若要试点（C2）：最小验收清单

1. **环境**：Node ≥ 22.19（现有 sidecar 22.5+ 需抬版本）；`pi-agent-core` 以项目私有方式安装（`npm i --ignore-scripts`，不装 CLI）；
2. **一个无副作用任务**跑通：例如「把本周 `docs/问题记录/` 的条目压成 500 字纪要」；
3. **RPC 通路**：C# 起进程 → 发 JSON 命令 → 收事件流 → 结果落库（只读输入，写只写我们自己的库）；
4. **对照**：同一任务用现有 C# 摘要链路跑一次，比较**质量 / 用时 / token**；
5. **退出条件写死在实验计划里**：若 RPC 稳定性、启动开销（Node 冷启）或质量没有明显优势 → **不引入**，只保留 C1 的语义借鉴。

> ⚠️ 试点**不得**让 Pi 直接持有 QQ/抖音通道，也不得让它在无门禁的情况下执行写文件 / 发消息 —— 这是接缝 B/C 的底线，不是可选项。

---

## F. 风险

| 风险 | 说明 | 缓解 |
|---|---|---|
| 版本漂移 | 1.x 仍在快速演进（0.x → 1.5.0 一年多；316 open issues） | 试点阶段锁死版本号，README/RPC 契约不当作长期 API |
| Node 版本 | Pi 要 22.19+，现有 sidecar 基准 22.5+ | 抬版本前先确认抖音 sidecar 兼容 |
| 双份日志/指标 | 跨进程后排障要拼两个日志源 | 试点阶段就给 Pi 侧日志加 traceId，与 C# 侧对齐 |
| 订阅额度条款 | `pi-ai` 支持用 Claude / ChatGPT / Copilot 订阅登录 —— **能否用于第三方 agent 取决于供应商条款与风控**，需实测，不作承诺 | 不纳入本轮，单独立项评估 |
| 期望膨胀 | 「Pi 很强」→ 容易滑向「那主线也换了吧」 | 用 §C3 的六条障碍当作硬边界 |

---

## G. 待拍板

- [ ] C1（借语义改 R-04 / R-10）是否立即做？**成本最低，建议先做**
- [ ] C2（后台 sidecar 试点）是否开一个限时实验（2 天 / 一个任务）？
- [ ] C3（主链路替换）是否直接排除，并写进决策记录？
- [ ] 「订阅 OAuth 省额度」是否单独立项调研？

---

## 附：来源

- GitHub API：`https://api.github.com/repos/earendil-works/pi`（2026-11-03 取）
- npm registry：`@earendil-works/pi-coding-agent` / `@earendil-works/pi-agent-core` / `@earendil-works/pi-ai` 的 `latest` 元数据与 README（2026-11-03 取）
- 官网与文档入口：[pi.dev](https://pi.dev)、[pi-coding-agent README](https://www.npmjs.com/package/@earendil-works/pi-coding-agent)
- 第三方参考（弱）：[Pi Agent 完整指南（2026-08，0.x 时期）](https://news.qiniu.com/archives/1787103478559)、[Pi Coding Agent 安全指南（morphllm）](https://www.morphllm.com/pi-coding-agent)
- 本项目对照物：`docs/03-设计/思考中新消息处理策略.md`、`docs/01-需求/运行可靠性与会话交互-需求.md`（R-04 / R-09 / R-10）、`docs/07-记忆/决策记录.md`（MEAI 决策、不引 MAF）
