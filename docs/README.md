# 文档索引

> 本项目文档统一放在 `docs/` 下（2026-10-20 起）。此前设计文档放在独立的私有仓库，现已全部合并进来。

---

## 我该看哪份？

| 我想… | 看这份 |
|---|---|
| **知道这产品要做什么** | [`design/requirements-roleplay-agent.md`](design/requirements-roleplay-agent.md) —— **S1 需求（源头）** |
| **知道整体架构** | [`architecture.md`](architecture.md)（先读，面向新人）→ [`architecture-proposal.md`](architecture-proposal.md)（完整方案，**单一事实来源**） |
| **知道某个模块怎么设计** | [`design/README.md`](design/README.md) —— 详细设计索引 |
| **知道配置怎么搞** | [`design/configuration-design.md`](design/configuration-design.md) |
| **知道 AI/Agent 怎么设计** | [`design/agent-design.md`](design/agent-design.md) |
| **知道部署在哪、怎么运维** | [`design/deployment-design.md`](design/deployment-design.md) |
| **接手 / 贡献代码** | [`onboarding.md`](onboarding.md) → [`standards/csharp-编码规范.md`](standards/csharp-编码规范.md) |
| **通信契约长什么样** | [`contracts/README.md`](contracts/README.md) |
| **项目最近在做什么 / 为什么这么定** | [`memory/`](memory/) —— 进度 · 决策 · 教训 |

---

## 目录结构

| 位置 | 内容 |
|---|---|
| `architecture.md` | 架构说明（面向新人，较浅） |
| `architecture-proposal.md` | **架构方案（单一事实来源）** |
| `onboarding.md` | 新人上手 |
| `design/` | 详细设计（索引见 [`design/README.md`](design/README.md)） |
| `contracts/` | 通信契约样例 JSON（测试要跑它） |
| `standards/` | 编码规范 |
| `research/` | 调研报告（如 MAF 可行性） |
| `memory/` | **项目本地记忆**：进度 · 决策 · 踩坑 · 教训 |
| `napcat-server-feasibility.md` | ⚠️ **历史存档** —— 服务器路线已废弃；其中 Xvfb 环境实测对 Linux 部署仍有参考价值 |

---

## 三条纪律

1. **决策一旦改变**，必须同步更新架构文档 + `memory/`，**不得只在对话里说**
2. **移动 / 改名文档后必跑链接检查**（脚本见 [`design/open-source-readiness.md`](design/open-source-readiness.md) §8.1）——
   一次移动曾断掉 11 处链接
3. **机密永不入库** —— token / 密码 / API key 一律走环境变量或 `appsettings.Local.json`（已在 `.gitignore`）
