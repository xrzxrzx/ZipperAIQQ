# ZipperAIQQ

把 QQ 变成**一个有记忆、有人设的 AI 角色扮演搭子**。

QQ 侧通过 **NapCat**（OneBot 11）接入；AI 侧基于 **MEAI（`Microsoft.Extensions.AI`）打底 + 部分自研** —— 自动工具循环交给 MEAI，**会话隔离 / 上下文压缩 / 记忆检索 / 工具权限门禁 / 人设防漂移** 全部自己实现。

---

## 🎯 项目宗旨

> **不接入任何复杂的 agent 应用/框架，仅用自身实现的简单 agent 架构，专门适配 AI 角色扮演。**

这不是"不喜欢框架"，而是**判定标准**：

| 如果引入一个依赖，是为了… | 那就不引 |
|---|---|
| 多 agent 协作、复杂编排、工作流可视化 | 我们只需要「一个有记忆、有人设、会发消息的聊天搭子」 |
| 通用 agent 抽象（如 MAF / LangChain / AutoGen 那类） | 它们解决的问题比我们的大，代价（体积、抽象层、调试难度）也跟着来 |
| "以后可能会用到"的能力 | 现阶段用不到的一律不预埋 |

**反过来说**：只要某件事是**角色扮演真正需要的**（上下文压缩、跨会话记忆、人设稳定、工具权限），我们就**自己写清楚**，而不是找一个通用框架来配。

---

## 技术栈

| 项 | 选择 |
|---|---|
| 语言 / 运行时 | **C# / .NET 10**（`net10.0`，LTS） |
| 服务端宿主 | `Microsoft.Extensions.Hosting` + ASP.NET Core (Kestrel) |
| 客户端 | **WinUI3**（Windows App SDK）+ `CommunityToolkit.Mvvm`（仅表现层；服务端保持跨平台） |
| IoC | `Microsoft.Extensions.DependencyInjection` |
| 客户端通信 | **HTTP 单端点（非 REST）+ WebSocket**（不引 gRPC） |
| QQ 协议端 | **NapCat**（OneBot 11） |
| AI | **MEAI 打底 + 部分自研**；接入走 **Responses API** |
| 本地向量 | `bge-small-zh-v1.5` + ONNX Runtime（**本地推理，零 API 成本**） |
| 配置 | **YAML 优先**（`config/server.yaml`），环境变量兜底 |
| JSON | **`System.Text.Json`**（全项目统一） |
| 存储 | SQLite |

### 通信契约

- **HTTP 只发命令**：`POST /api`，body `{action, payload}`，响应 `{code, message, data}`
- **WebSocket 只收事件**：`WS /events`，帧 `{type, seq, time, data}`
- **鉴权**：loopback 免验；**非 loopback 强制 token，且启动时校验未配即拒绝启动**

---

## 🚀 部署

> 服务端可跑在 **Windows 或 Linux**；客户端是 WinUI3（仅 Windows）。
> 两者可以同机，也可以分开 —— 客户端始终通过 HTTP/WS 连服务端。

### 前置条件

| 需要什么 | 说明 |
|---|---|
| **NapCat** | OneBot 11 **正向 WebSocket**，且**必须开启 access_token**（没 token 会在握手后被关掉） |
| **LLM API** | 任意 OpenAI 兼容端点（DeepSeek / 兼容 Responses 或 Chat 接口） |
| **.NET 10 SDK** | **仅构建时需要**；运行时若用 self-contained 发布则目标机无需安装 |

### 1. 发布

```bash
# Windows
dotnet publish ZipperAIQQ.Server/ZipperAIQQ.Server.csproj \
  -c Release -o out -r win-x64 --self-contained true

# Linux
dotnet publish ZipperAIQQ.Server/ZipperAIQQ.Server.csproj \
  -c Release -o out -r linux-x64 --self-contained true
```

> 目标机已装 .NET 10 运行时可把 `--self-contained true` 换成 `false`，体积小很多。

### 2. 补上 publish 产物里没有的文件

| 文件 / 目录 | 用途 | 缺了会怎样 |
|---|---|---|
| `temp/bge-small-zh-v1.5/` | 本地向量模型（约 24MB，**不入库**） | **启动直接崩**（`找不到 embedding 模型`） |
| `config/faces.qq.json` | 表情表 | 表情发不出去（变成方括号文字） |
| `config/persona.md` | 全局人设 | 用兜底人设 |

#### 向量模型怎么来

模型是 [**BAAI/bge-small-zh-v1.5**](https://huggingface.co/BAAI/bge-small-zh-v1.5)（**MIT 许可**，可商用可分发），
但要的是 **ONNX INT8 量化版**，所以不直接随仓库分发（二进制会永久留在 git 历史）：

```bash
# 一行生成（需要 Python 3.10+；会装 optimum + onnxruntime，首次几百 MB）
pip install "optimum[onnxruntime]" transformers
optimum-cli export onnx --model BAAI/bge-small-zh-v1.5 --task feature-extraction --quantize temp/bge-small-zh-v1.5
```

或者用仓库里的脚本（多一层环境检查）：

```powershell
.\scripts\build-embedding-model.ps1
```

产物里必须包含 `model_quantized.onnx`（约 24MB）+ 三个 tokenizer/config 文件。
访问不了 HuggingFace 时，可从 [ModelScope](https://www.modelscope.cn/models/AI-ModelScope/bge-small-zh-v1.5) 获取原始模型后再导出。

### 3. 写配置

服务端读 **`config/server.yaml`**（优先级 **YAML > 环境变量 > 默认值**）。
没有该文件时退回环境变量，两种方式都支持：

```yaml
server:
  httpAddress: 127.0.0.1:5180      # 局域网访问改成 0.0.0.0:5180
  httpToken: "<http-token>"         # 非 loopback 时必填
  database: ./data/zipperaiqq.db
  dataProtectionKeys: ./data/keys   # ⚠️ 必须备份：丢了已存的 LLM key 全解不开

napcat:
  wsUrl: "ws://<napcat-host>:<port>"
  token: "<napcat-token>"
  selfId: "<bot-qq>"                # 机器人自己的账号（mention 模式要用）

llm:
  apiKey: "<llm-api-key>"
  baseUrl: "https://api.deepseek.com"
  model: "deepseek-flash"
  apiKind: Responses                # Responses | Chat

limits:
  sendIntervalMs: 300               # 会话级最小发送间隔
  maxConsecutive: 10
  cooldownMs: 10000
  fileMaxBytes: 262144
  hotWindowMessages: 20

access:
  admins: ["<admin-qq>"]
  toolWhitelist: []
  whitelist: []

paths:
  faces: ./config/faces.qq.json
  persona: ./config/persona.md
  workspace: ./workspace
  embeddingModel: ./temp/bge-small-zh-v1.5
```

环境变量等价写法（前缀 `ZIPPER_`）：
`ZIPPER_NAPCAT_WS` / `ZIPPER_NAPCAT_TOKEN` / `ZIPPER_LLM_APIKEY` / `ZIPPER_LLM_BASEURL` /
`ZIPPER_LLM_MODEL` / `ZIPPER_DB` / `ZIPPER_HTTP_ADDR` / `ZIPPER_HTTP_TOKEN` / `ZIPPER_ADMINS` 等。

### 4. 启动

```bash
# 最简单：前台直接跑（日志同时写 logs/）
./ZipperAIQQ.Server            # Windows: ZipperAIQQ.Server.exe
```

要**常驻**，按平台选一种：

| 平台 | 方式 |
|---|---|
| **Linux** | systemd unit：`WorkingDirectory=/opt/zipper`、`ExecStart=/opt/zipper/ZipperAIQQ.Server`、`Restart=always` |
| **Windows** | **计划任务**：`schtasks /create /sc onstart /ru SYSTEM /tn <name> /tr "<path>\ZipperAIQQ.Server.exe"`<br>⚠️ **别用 `sc create` 注册成服务** —— exe 是控制台程序、不实现 SCM 协议，会报 **1053** |

> Windows 上还有两个坑：作为计划任务运行时**工作目录是 `System32`**（要在包装脚本里自己 `cd`），
> 且**环境变量不继承** —— 用 `config/server.yaml` 最省事。

### 5. 验证

```bash
curl -X POST http://127.0.0.1:5180/api \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <http-token>" \
  -d '{"action":"getStatus"}'
# → {"code":0, ..., "channelConnected":true, ...}
```

日志里出现 **`消息路由已启动`** 才算真正起来了 —— **进程活着不等于初始化成功**。

### 6. 客户端

客户端启动后填「服务器地址 + token」（loopback 可留空 token）即可：

- **状态页**：模型 / 通道 / 运行时长 / 会话数（0.5 秒自动刷新）
- **会话页**：会话列表 → 消息（增量追加、自动滚到底）
- **数据页**：Token 用量、检索命中率、JEV 判断（图文）
- **配置页**：模型端点、运行参数、行为模式（every / mention / jev）、
  人设（形象 + 性格分开）、人员与名单（主人 / 管理员 / 黑白名单）
- **审计页**：配置变更记录

---

## 当前状态

| 阶段 | 状态 |
|---|---|
| **S1 需求** | ✅ 已冻结（FR-1 ~ FR-21） |
| **S2 技术方案** | ✅ 已批准 |
| **S3 开发** | 🔄 进行中（M1 + M2 主体完成：通道 / 存储 / 记忆 / 工具 / 命令 / 端点 / 配置 / 客户端） |
| S4 测试 | ⬜ 未开始（仓库内暂无测试项目） |

**已可运行**：服务端能连 NapCat、收发消息、调用工具、执行命令；客户端能连服务端看状态 / 会话 / 数据 / 配置。

---

## 架构要点：三个不可破坏的接缝

| 接缝 | 约束 |
|---|---|
| **A. 表现层 ↔ 编排层** | 必须走 HTTP/WS，**不得进程内直连** —— 这是「本机单体」与「服务器分离」共用一套代码的前提 |
| **B. 平台通道 ↔ 编排层** | 必须经**平台无关的消息通道抽象**，**上层不得直接依赖任何平台类型**（QQ 只是第一个实现） |
| **C. 工具执行 → 权限门禁** | 危险操作（执行命令、写/删文件、访问外部服务）**默认仅管理员**；门禁在服务端 |

---

## 排障速查

| 现象 | 先看什么 |
|---|---|
| 启动就崩：找不到 `model_quantized.onnx` | 没传 embedding 模型（见「部署 第 2 步」） |
| 日志显示连上 WS 但立刻断开 | NapCat 没配 `access_token`（握手成功 ≠ 连上） |
| 局域网连不上 | `httpAddress` 还写着 `127.0.0.1` |
| 起来了但群里没反应 | 回复模式：`mention` 模式没被 @ 就不回；名单模式：白名单 + 空名单 = **全部拒绝** |
| 改配置不生效 | YAML 是否真被加载（日志里找 `已加载配置文件`） |

---

## ⚠️ 关于 AI 与本项目的关系

**本项目的实现代码由 AI 助手编写**，人类作者负责需求决策、方案批准与关键改动抽查。

**对贡献者没有 AI 使用限制** —— 你用不用、怎么用，由你自己决定。

---

## 文档索引

| 文档 | 内容 |
|---|---|
| 🚀 [`docs/08-上手/新人上手.md`](docs/08-上手/新人上手.md) | **新人上手** —— 克隆、环境、第一个任务 |
| [`docs/01-需求/角色扮演助手-需求.md`](docs/01-需求/角色扮演助手-需求.md) | **需求（源头）** —— 这产品到底要做什么 |
| [`docs/02-架构/架构说明-新人版.md`](docs/02-架构/架构说明-新人版.md) | **架构说明（新人版）** —— 分层、三个接缝、依赖约束 |
| [`docs/02-架构/架构方案.md`](docs/02-架构/架构方案.md) | **架构方案**（单一事实来源） |
| [`docs/03-设计/`](docs/03-设计) | **详细设计** —— 按模块查 |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | **贡献指南** —— 分支、PR 流程、危险区、安全约定 |
| [`docs/04-规范/CSharp编码规范.md`](docs/04-规范/CSharp编码规范.md) | C# 编码规范（提交前必读） |
| [`docs/05-契约/`](docs/05-契约) | **契约样例** —— 两端 DTO 一致性的安全网 |

完整的文档导航见 [`docs/README.md`](docs/README.md)。

---

## ⚠️ 使用风险（务必先读）

1. **NapCat 是非官方协议端** —— 使用它可能违反腾讯的用户协议，**你的 QQ 账号存在被限制的风险**，请自行评估后再使用
2. **消息内容会发给第三方 LLM** —— 涉及隐私的内容**不要**经过它
3. **群聊里 AI 说的话是"以你的机器人账号发的"** —— 它说错话，后果由账号承担
4. **默认配置不是安全边界** —— 工具权限门禁默认仅管理员，但对外提供服务前请自己审一遍
5. **不要把服务暴露到公网** —— 非 loopback 监听必须配 token（本项目会强制校验），但 token 不解决所有问题
6. **二维码截图等同于账号接管凭证** —— 提 issue 时**不要**贴二维码或未脱敏的日志

---

## 许可

[MIT](LICENSE)
