# TypeScript / JavaScript 编码规范

> 适用范围：本仓库内所有 TypeScript / JavaScript（含 Node sidecar、脚本、外置工具）
> 基线：**ESM + Node ≥ 22**（`"type": "module"`）、**TypeScript ≥ 5.7**、`strict: true`
> 目标：**让"异步 + 外部进程 + 页面自动化"这三类最容易出事的地方不出事**
>
> 与 [`CSharp编码规范.md`](CSharp编码规范.md) 同一定位：不是教科书，是**故障驱动的清单**。
> 每条后面的「教训」都来自真实踩过的坑。

---

## 0. 总则

1. **可读性优先于小聪明**：`reduce` 套三层不如多写两行 `for`。
2. **显式优于隐式**：不靠副作用传递信息；不靠"默认导出"猜类型。
3. **失败要响**：拿不到就抛错并带上中文原因，**不许静默返回空数组/空字符串冒充成功**。
4. **不猜**：《不确定某个 API 行为 → 先写最小验证脚本》，别靠"应该可以"。

---

## 1. 命名

| 元素 | 规则 | 示例 |
|---|---|---|
| 类型 / 接口 / 类 / 枚举 | PascalCase | `OutboundPlan`、`DouyinBrowser` |
| 函数 / 变量 / 参数 | camelCase | `buildOutboundPlan`、`chatType` |
| 常量（模块级、真不变） | UPPER_SNAKE_CASE | `PROTOCOL_VERSION`、`QR_REFRESH_MS` |
| 常量对象 / 枚举式映射 | PascalCase 对象 | `MESSAGE_TYPE`、`SERVICE` |
| 私有成员 | 不加下划线前缀，用 `private` | `private readonly log` |
| 布尔量 | `is` / `has` / `can` / `should` 开头 | `isLoggedIn`、`hasImage` |
| 文件名 | kebab 或单词小写，一个文件一个主题 | `outbound.ts`、`protocol.ts` |

- **禁止**拼音命名、无意义缩写（`tmp`、`a1`、`obj2`）。`ctx`、`req`、`res`、`cfg` 这类公认缩写可以。
- 与 C# 侧同名的概念**必须同名**（`OutboundPlan` / `sessionIdFor` / `classifyFailure`），
  否则跨语言的契约对不上，读代码时要在两个词之间反复翻译。

---

## 2. 文件与分层

- **一个文件一个主题**，文件顶部写清「这个文件管什么 + 不碰什么」。
- **纯函数与副作用分离**（本项目硬性要求）：
  - `protocol.ts` 这类**翻译层**：不碰网络、不碰文件、不碰浏览器，输入输出都是普通对象 → 必须能直接用 `node --test` 喂假数据。
  - 「谁碰外部世界」只能有一个文件：HTTP/WS 连接、`fs`、`child_process`、Playwright 各归各的文件。
- **禁止**在纯函数文件里 `import` 有副作用的模块（`playwright`、`node:fs`、`node:child_process`）。
- 导出**具名**符号，不用 `export default`（默认导出改名后 IDE 与 grep 都找不到）。

---

## 3. 类型

- `strict: true` + `noUncheckedIndexedAccess: true` **不许关**。
- **禁止 `any`**；确实未知用 `unknown` + 显式收窄（`asRecord` / `typeof` 守卫）。
- 外部数据（页面 `evaluate` 返回值、JSON、帧体）**一律先当 `unknown`**，再用收窄函数处理：
  ```ts
  const record = asRecord(parsed); // 非对象 → null
  const type = str(record?.type);  // 非字符串 → undefined
  ```
- 可辨识联合优于可选字段堆叠：
  ```ts
  type ChallengeDetection =
    | { kind: "sms"; hint: string }
    | { kind: "slider"; hint: string; bgBox: QrBox | null }
    | null;
  ```
- **`null` 与 `undefined` 的分工**：`undefined` = 没有这个值；`null` = 明确「没有」。同一定义域内保持一致，别混。

---

## 4. 异步

- 每个 `await` 都要想清楚「它失败会怎样」：能失败的地方**必须有 `try/catch` 或 `catch` 兜底**。
- **禁止 floating promise**：不 `await` 的调用要么 `void fn()`（明确表示"故意不等"），要么 `.catch()`。
- **禁止 `await` 在循环里做无谓串行**：能并行的用 `Promise.all`；但**有速率/配额约束的必须串行**（如发消息）。
- 定时器一律存句柄并 `clearInterval`；`process.once("SIGINT"/"SIGTERM")` 里要能干净退出。
- 超时自己实现（`Promise.race` + `setTimeout`），**不要指望外部库默默兜底**。

---

## 5. 错误

- 抛 `Error` 的子类并带**可机器识别的码**（如 `OutboundError.code = "readback"`），
  文本给人看（中文 + 具体上下文），码给程序看。
- **错误分类集中一处**（`classifyFailure`）：别在每个调用点各写一套 `if (msg.includes(...))`。
- 日志与错误信息**不许**包含 token / cookie / profile 路径里的敏感段；URL 里的 token 要打码。
- 「降级」必须**留痕**：要么写进返回值（`degraded: ["reply_to"]`），要么写日志；不许静默降级。

---

## 6. 资源与进程

- 文件句柄、WebSocket、浏览器 context、定时器：**谁开谁关**，且关闭路径要幂等（可重复调用不炸）。
- 临时文件落**配置里的 `workDir`**，用完即删；不许往系统临时目录或仓库根目录乱写。
- 起子进程（如果有）必须：设超时、读 stdout/stderr 都要有上限、非 0 退出即失败、**不许把子进程当常驻服务**。

---

## 7. 字符串与编码

- 源文件一律 **UTF-8（无 BOM）**，换行 **LF**。
- 中文只出现在**面向人的文本**（日志、错误、注释、README）；**代码标识符一律英文**。
- 处理平台文本时**不许按 code unit 硬切**（会切断 emoji 代理对）—— 分段/截断走统一的 `chunkText` 这类函数。
- 时间：内部一律**毫秒时间戳**；展示才转本地时间。平台给的时间戳要判量级（秒 vs 毫秒）。

---

## 8. 页面自动化（Playwright）额外纪律

- **选择器只许出现在 `site.ts`**：改版时只改一个文件。
- `page.evaluate` 里的代码运行在浏览器：**不许引用外部变量**（除了作为参数显式传入），
  也不许指望 Node 的 API（`Buffer`、`process` 都不在）。
- 每个页面动作都要有超时；**动作后要验证结果**（发消息 → 回读；点会话 → 校验标题）。
- **不许用 `fill` 代替真实输入 + 回读校验**（网页版对 `fill` 不一定触发前端事件）。
- 拿不到就抛，**不许点列表第一个凑数**（点错人会发错消息，这是最贵的错）。

---

## 9. 测试

- 单测用 **`node:test` + `node:assert/strict`**，放 `test/*.test.ts`，**不联网、不开浏览器**。
- 纯函数层（`protocol.ts`）**必须有单测**；夹具用**真实抓包的最小样本**，并注明来源与日期。
- 契约类常量（字段号、帧名）要有**锁值单测**：改了就红，别等真机才发现。
- 测试名写"人话 + 期望"，中文可以：`test("chunkText：不切断代理对", ...)`。

---

## 10. 注释

- 注释解释**为什么**，不解释"这行在做什么"。
- 每个对外函数至少一句用途；有坑的地方写清坑（「实测踩到」比「注意」有用一百倍）。
- 引用代码位置用 `` `文件:行号` ``（与 C# 侧同口径），别用"上面那个函数"这种会腐烂的说法。
