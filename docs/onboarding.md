# 新人上手（Onboarding）

> 这份文档给**第一次参与这个项目的人** —— 包括"没什么基础"的同学。
> 照着做一遍，你就能跑起来、也能提交第一个改动。

---

## 0. 先确认你手上有这些

| 需要 | 说明 |
|---|---|
| **GitHub 账号** | 用你自己的账号，**不要**共用别人的 |
| **.NET SDK 10** | 注意是 **SDK**，不是只装 Runtime（`dotnet --list-sdks` 能看到才算） |
| **Git** | Windows 装 [Git for Windows](https://git-scm.com/download/win) 即可 —— 它自带凭据管理器，后面登录 GitHub 不用折腾 |
| **一个编辑器** | VS / Rider / VS Code 都行 |

---

## 1. ⚠️ 前置条件：先让 maintainer 把你加成协作者

**这一步不做，克隆会报 404。**

本项目是**单一仓库**（代码 + 文档都在里面），当前为**私有仓库**：

> ⚠️ **一个很坑的地方**：私有库**没有权限时，GitHub 会假装"仓库不存在"**（返回 404 而不是 403）。
> 所以看到 "repository not found" **先别怀疑地址写错了** —— 大概率是你还没被加进去。

请 maintainer 到 GitHub 的 **Settings → Collaborators** 里把你加进去。

---

## 2. 克隆仓库

```powershell
cd D:\Works                      # 换成你自己的目录
git clone https://github.com/xrzxrzx/ZipperAIQQ.git
cd ZipperAIQQ
```

> 📌 **文档就在同一个仓库的 `docs/` 里**，克隆完就能看，不需要额外步骤。
> （2026-10-20 之前文档曾放在独立的私有仓库，现已合并进 `docs/`。）

---

## 3. 配置你的 Git 身份（一次性）

```powershell
git config --global user.name  "你的名字"
git config --global user.email "你的邮箱"
```

> 提交记录里的作者就是从这里来的。**不用自己的账号，贡献是没法归因的。**

---

## 4. 接下来读什么（按这个顺序）

| 顺序 | 读什么 | 大概要多久 |
|---|---|---|
| 1 | 根 [`README.md`](../README.md) | 5 分钟 —— 知道这东西是干什么的 |
| 2 | [`docs/requirements-roleplay-agent.md`](design/requirements-roleplay-agent.md) | 20 分钟 —— **这产品到底要做什么**（需求是源头） |
| 3 | [`docs/architecture.md`](architecture.md) | 30 分钟 —— ⚠️ **不读这个，改代码就是蒙的** |
| 4 | [`CONTRIBUTING.md`](../CONTRIBUTING.md) | 15 分钟 —— 尤其「危险区」那一节 |
| 5 | [`docs/standards/csharp-编码规范.md`](standards/csharp-编码规范.md) | 提交前必读 |
| 6 | [`docs/design/README.md`](design/README.md) | **要改具体模块时**按索引找你关心的那份 |
| 7 | [`docs/contracts/README.md`](contracts/README.md) | **只有你要碰契约时才需要** |

> 💡 **别一开始就读全部设计文档** —— `docs/design/` 有十份，按需查比通读有效。

---

## 5. 第一个任务从哪拿

**从最好上手的开始**（完整清单见 [`CONTRIBUTING.md`](../CONTRIBUTING.md) §九）：

| 任务 | 门槛 | 为什么适合新手 |
|---|---|---|
| 🟢 **写一个工具（`AIFunction`）** | 最低 | 一个类 + 一行注册，**完全不碰架构** |
| 🟢 **补契约样例 / 补测试用例** | 低 | 照抄已有结构改断言即可 |
| 🟢 **改文档、加截图** | 低 | 对项目价值真实 |
| 🟡 **改桌面界面（WPF/XAML）** | 中 | 表现层独立，**改错不会炸服务端** |

---

## 6. 提交你的第一个 PR

流程见 [`CONTRIBUTING.md`](../CONTRIBUTING.md) §四 / §五。要点：

- **开分支**（`feat/xxx` / `fix/xxx`），**不要直推 `main`**
- 提交信息用**约定式提交**
- **PR 描述里写清"你怎么验的"** —— 同目录下有个表格说明要写哪几项
- **CI 会自动跑**（这是它存在的意义），**绿了再找 review**

---

## 7. 常见问题（Troubleshooting）

| 症状 | 原因 / 怎么办 |
|---|---|
| 克隆报 **404 / repository not found** | 你还没被加成协作者。**私有库无权限时会假装"不存在"** |
| 克隆时反复要密码 | 用 Git for Windows 自带凭据管理器（首次会弹浏览器登录）；或改用 PAT / SSH |
| `dotnet build` 说找不到 SDK | 装的是 **.NET SDK** 而不是 Runtime？`dotnet --list-sdks` 确认一下 |
| CI 出现红叉 | 点进去看是哪个作业。本仓库 CI 只做**机械检查**（JSON 合法 / 链接不断 / 身份字段是占位值），报错信息通常直接说清问题 |
| PR 里 CI 一直不跑 | 检查是不是直接推到 `main` 了（CI 只对 PR 和 `main` 的推送触发） |

---

## 8. 卡住了怎么办

**直接开 issue 问，说明三件事**：

1. 你**做了什么**
2. 你**期望**什么
3. 你**实际看到**什么（贴报错原文）

> **这不会显得你菜。** 说清这三件事本身就是一项能力，而且这样别人才帮得上忙。
> 另外：**贴日志/截图前先脱敏** —— 里面有 QQ 号、token 的别原样贴（见 [`CONTRIBUTING.md`](../CONTRIBUTING.md) §十）。
