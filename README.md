# ZipperAIQQ

对接 QQ 与 DeepSeek 的中间件：QQ 侧通过 **NapCat**（OneBot 11）接入，AI 侧基于 **MEAI（`Microsoft.Extensions.AI`）打底 + 部分自研**（自动工具循环用 MEAI，会话隔离 / 上下文压缩 / 工具权限门禁自研）。

- **服务端**：C# / .NET 10，**同时兼容 Windows 与 Linux**
- **客户端**：WPF 桌面程序，与服务端通过 **HTTP（命令）+ WebSocket（事件）** 通信
- **QQ 通道**：NapCat（OneBot 11），抽象为 `IQQChannel`，支持正向 WS 与反向 WS

> ⚠️ **文档状态**：本项目**尚在设计阶段**，代码尚未开始。本 README 会在首个可运行版本出现时重写为完整的"快速开始"。

## 当前状态

| 阶段 | 状态 |
|---|---|
| S1 需求方案 | 🔄 产品定位已定 |
| S2 技术方案 | 🔄 草案进行中 |
| S3 开发 | ⬜ 未开始 |

## 技术选型

| 项 | 选择 |
|---|---|
| 语言 / 运行时 | **C# / .NET 10**（`net10.0`，LTS） |
| 服务端宿主 | `Microsoft.Extensions.Hosting` + ASP.NET Core (Kestrel) |
| 客户端 | **WPF** + `CommunityToolkit.Mvvm`（仅表现层；服务端保持跨平台） |
| IoC | `Microsoft.Extensions.DependencyInjection` |
| 客户端通信 | **HTTP 单端点（非 REST）+ WebSocket**（不引 gRPC） |
| QQ 协议端 | **NapCat**（OneBot 11） |
| AI | **MEAI（`Microsoft.Extensions.AI`）打底 + 部分自研** —— 不引 MAF |
| JSON | **`System.Text.Json`**（全项目统一，与 MEAI 同栈） |
| 存储 | SQLite |
| 测试 | xUnit |

### 通信契约

- **HTTP 只发命令**：`POST /api`，body `{action, payload}`，响应 `{code, message, data}`
- **WebSocket 只收事件**：`WS /events`，帧 `{type, seq, time, data}`
- **鉴权**：loopback 免验；**非 loopback 强制 token，且启动时校验未配即拒绝启动**

## 架构要点：三个不可破坏的接缝

| 接缝 | 约束 |
|---|---|
| **A. 表现层 ↔ 编排层** | 必须走 HTTP/WS，**不得进程内直连** —— 这是「本机单体」与「服务器分离」共用一套代码的前提 |
| **B. QQ 通道 ↔ 编排层** | 必须经 `IQQChannel` 抽象，**上层不得直接依赖 NapCat 类型** |
| **C. 工具执行 → 权限门禁** | 危险操作（执行命令、写 / 删文件、访问外部服务）**默认仅管理员**；门禁在服务端 |

## ⚠️ 关于 AI 与本项目的关系

本项目的实现代码**由人类作者编写**，不采用 AI 直接生成实现代码的方式。

对**贡献者**同样适用：AI 可以用来解释原理、指出问题、给排查方向，但**不用来产出你要提交的实现代码**。
理由很直接 —— 代写会让你失去学习机会，而且外人**无法分辨**这段代码是人写的还是 AI 写的。

细节见 [`CONTRIBUTING.md`](CONTRIBUTING.md)（**含 AI 使用政策**）。

## 文档索引

| 文档 | 内容 |
|---|---|
| 🚀 [`docs/onboarding.md`](docs/onboarding.md) | **新人上手** —— 从这里开始：克隆、环境、第一个任务 |
| [`docs/architecture.md`](docs/architecture.md) | **架构说明（贡献者版）** —— 分层、三个接缝、依赖约束、改动导航 |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | **贡献指南** —— 环境、分支、PR 流程、危险区、AI 政策 |
| [`docs/standards/csharp-编码规范.md`](docs/standards/csharp-编码规范.md) | C# 编码规范（提交前必读） |
| [`docs/contracts/`](docs/contracts/) | **契约样例** —— 客户端 / 服务端 DTO 一致性的安全网（共享数据，不共享代码） |

> 📌 更详细的设计文档属于**项目内部资料，不随本仓库公开**（其中含有个人基础设施信息与内部决策过程）。
> 本仓库**不包含**指向那些文档的链接。

## ⚠️ 使用风险（务必先读）

1. **NapCat 是非官方协议端** —— 使用它可能违反腾讯的用户协议，**你的 QQ 账号存在被限制的风险**，请自行评估后再使用
2. **消息内容会发给第三方 LLM（DeepSeek）** —— 涉及隐私的内容**不要经过它**
3. **默认配置不是安全边界** —— 工具权限门禁默认仅管理员，但对外提供服务前请自己审一遍
4. **不要把服务暴露到公网** —— 非 loopback 监听必须配 token（本项目会强制校验），但 token 不解决所有问题
5. **二维码截图等同于账号接管凭证** —— 提 issue 时**不要**贴二维码或未脱敏的日志

## 许可

[MIT](LICENSE)
