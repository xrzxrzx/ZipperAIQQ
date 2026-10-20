# 设计文档索引

> 📌 本目录是 [`docs/`](../README.md) 下的**详细设计层**。第一次来请先读 [`../architecture.md`](../architecture.md)。

---

## 我该看哪份？

| 我想知道… | 看这份 |
|---|---|
| **这产品要做什么**（需求、范围、M1 切分、待决问题） | 📋 [`requirements-roleplay-agent.md`](requirements-roleplay-agent.md) |
| **这东西整体长什么样**（项目结构、依赖、数据流、分期计划） | 🧭 [`overview.md`](overview.md) |
| **IoC 怎么装配**（组合根在哪、每个模块注册什么、生命周期） | 🔌 [`composition-design.md`](composition-design.md) |
| **WebSocket 怎么封的**（双端复用、消息分流、六个坑） | 🌐 [`transport-design.md`](transport-design.md) |
| **Transport 具体怎么写**（`WebSocketDuplexBase`、`Attach` 步骤、关闭顺序、踩坑清单） | 🔧 [`transport-implementation.md`](transport-implementation.md) |
| **AI 那套怎么做的**（MEAI 边界、工具、压缩、重试、漂移修正） | 🤖 [`agent-design.md`](agent-design.md) |
| **配置和日志怎么搞**（`Core` 的定位、来源与优先级、热重载、脱敏） | ⚙️ [`configuration-design.md`](configuration-design.md) |
| **部署在哪、怎么运维**（T30、Windows/Linux、内存账、安全清单） | 🚀 [`deployment-design.md`](deployment-design.md) |
| **开源要注意什么**（边界、必备文件、新人友好、隐私自查） | 📦 [`open-source-readiness.md`](open-source-readiness.md) |
| **以前的设计是怎么被推翻的** | 🗄️ [`archive/`](archive/) |

---

## 文档清单

| 文档 | 行数级 | 主题 |
|---|---|---|
| [`requirements-roleplay-agent.md`](requirements-roleplay-agent.md) | ~300 | **S1 需求**（🔶 草案未冻结）：FR-1~FR-10、验收标准、M1 切分、6 个待决问题 |
| [`overview.md`](overview.md) | ~600 | **总览**：目标 / 项目结构 / 各模块摘要 / 数据流 / 测试策略 / 分期计划 |
| [`composition-design.md`](composition-design.md) | ~360 | **组合与注册**：组合根、各模块注册清单、生命周期、容器自检 |
| [`agent-design.md`](agent-design.md) | ~660 | Agent 详细设计 |
| [`configuration-design.md`](configuration-design.md) | ~650 | 配置与日志详细设计 |
| [`open-source-readiness.md`](open-source-readiness.md) | ~540 | 开源就绪设计与边界定义 |
| [`transport-design.md`](transport-design.md) | ~440 | Transport 详细设计（**结构**：复用切口、双端结构、六个坑） |
| [`transport-implementation.md`](transport-implementation.md) | ~430 | **Transport 实现指南**：甲方案（Kestrel）、`WebSocketDuplexBase`、`Attach` 步骤、关闭顺序、踩坑清单、配置归属 |
| [`deployment-design.md`](deployment-design.md) | ~260 | 部署设计 |
| [`archive/technical-design-legacy.md`](archive/technical-design-legacy.md) | ~230 | ⚠️ **历史归档，勿参照** |

> 📄 架构总纲在上一级：[`../architecture-proposal.md`](../architecture-proposal.md)（**单一事实来源**）。

---

## ⭐ 组织规则（新增文档时照这个来）

| 规则 | 说明 |
|---|---|
| **一份文档 = 一个主题** | 主题大到"读者会单独来找它"就拆出去（`transport-design` / `agent-design` / `configuration-design` / `deployment-design` 都是这么来的） |
| **`overview.md` 只放摘要 + 指针** | 详细内容**下沉到专题文档**。总览里出现大段实现细节 = 又该拆了 |
| **命名** | `<主题>-design.md`（`composition-design` / `agent-design`）；**不要**叫 `-v2`、`-new`、`-final` |
| **改名前先想引用** | 项目里大量引用写作「`xxx-design.md` §N」；改名/搬家会让它们**全部变味** |
| **拆分时保留原编号** | 拆出去的部分**沿用原章节号**（如 `composition-design.md` 里仍是 `§4.1` / `§4.4.7`）→ 已有引用继续有效，不必逐个改 |
| **过时内容进 `archive/`** | **不要直接删** —— 决策过程要可追溯。归档文件**头部必须写清"为什么在这里、勿参照"** |
| **移动文档后必跑链接检查器** | 这条踩过：一次移动断了 11 处链接。脚本在 [`open-source-readiness.md`](open-source-readiness.md) §8.1 |

---

## ⚠️ 两条纪律

1. **文档之间可自由互链**（2026-10-20 起文档与代码同仓库）
2. **机密永不入库** —— token / 密码 / API key 一律走环境变量或 `appsettings.Local.json`
