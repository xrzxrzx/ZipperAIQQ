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

**这一步不做，第 3 步会报 404。**

本项目有**两个**仓库：

| 仓库 | 可见性 | 说明 |
|---|---|---|
| **代码仓库** | 公开 | 谁都能克隆 |
| **内部文档仓库** | **私有** | **必须先被加成协作者**，否则克隆报 404 |

> ⚠️ **一个很坑的地方**：私有库**没有权限时，GitHub 会假装"仓库不存在"**（返回 404 而不是 403）。
> 所以看到 "repository not found" **先别怀疑地址写错了** —— 大概率是你还没被加进去。

请 maintainer 到 GitHub 的 **Settings → Collaborators** 里把你加进去，**两个仓库都要加**。

---

## 2. 克隆代码仓库

```powershell
cd D:\Works                      # 换成你自己的目录
git clone https://github.com/xrzxrzx/ZipperAIQQ.git
cd ZipperAIQQ
```

---

## 3. 克隆内部文档仓库到 `ai-docs/`

> 📌 **地址由 maintainer 直接发给你** —— 它是私有库，**故意不写进这份公开文档**（公开仓库里没必要留下私有库地址）。

```powershell
# ⚠️ 站在【代码仓库根目录】执行
git clone <maintainer 发给你的地址> ai-docs
```

**为什么必须放在 `ai-docs/` 里面**：设计文档与代码分成两个仓库，但放在**同一个工作目录**下 ——
这样"文档里指向代码"和"代码文档里指向设计文档"的**相对链接都还能用**。

---

## 4. 🔴 关键一步：把 `ai-docs/` 设为「本地忽略」

**这一步不做，你迟早会把内部文档提交进公开仓库。**

```powershell
# 仍在代码仓库根目录
Add-Content -Path '.git\info\exclude' -Value "ai-docs/"
```

⚠️ **三个必须知道的点**：

| # | 要点 |
|---|---|
| 1 | **用 `.git/info/exclude`，不要用 `.gitignore`** —— `.gitignore` 会被提交，等于在**公开仓库里写下**"这里有个私有文档夹"；`info/exclude` 是**纯本地**的，不进版本库 |
| 2 | **这是每个克隆都要各自做一次的** —— `info/exclude` **不随仓库分发**，你做完，别人克隆下来还得再做 |
| 3 | **忘了做会怎样** —— `ai-docs/` 会显示成未跟踪，`git add -A` 会把它当**内嵌仓库（gitlink）**提交，git 会警告 `adding embedded git repository`；**但警告不是错误**，手快点就过去了 |

> ✅ **好消息**：CI 里有一条守卫会拦下来（`git ls-files ai-docs` 非空就失败）。
> **但那是推上去之后才发现** —— 自己先设好更体面。

---

## 5. ✅ 验证你的设置对不对

```powershell
# ① 应当输出：.git/info/exclude:9:ai-docs/	ai-docs/
git check-ignore -v ai-docs/

# ② 应当【看不到】ai-docs/ 这一行
git status --short
```

**两条都对，才算设置好了。**

---

## 6. 配置你的 Git 身份（一次性）

```powershell
git config --global user.name  "你的名字"
git config --global user.email "你的邮箱"
```

> 提交记录里的作者就是从这里来的。**不用自己的账号，贡献是没法归因的。**

---

## 7. 接下来读什么（按这个顺序）

| 顺序 | 读什么 | 大概要多久 |
|---|---|---|
| 1 | 根 [`README.md`](../README.md) | 5 分钟 —— 知道这东西是干什么的 |
| 2 | [`docs/architecture.md`](architecture.md) | 30 分钟 —— ⚠️ **不读这个，改代码就是蒙的** |
| 3 | [`CONTRIBUTING.md`](../CONTRIBUTING.md) | 15 分钟 —— 尤其「危险区」那一节 |
| 4 | [`docs/standards/csharp-编码规范.md`](standards/csharp-编码规范.md) | 提交前必读 |
| 5 | [`docs/contracts/README.md`](contracts/README.md) | **只有你要碰契约时才需要** |

---

## 8. 第一个任务从哪拿

**从最好上手的开始**（完整清单见 [`CONTRIBUTING.md`](../CONTRIBUTING.md) §九）：

| 任务 | 门槛 | 为什么适合新手 |
|---|---|---|
| 🟢 **写一个工具（`AIFunction`）** | 最低 | 一个类 + 一行注册，**完全不碰架构** |
| 🟢 **补契约样例 / 补测试用例** | 低 | 照抄已有结构改断言即可 |
| 🟢 **改文档、加截图** | 低 | 对项目价值真实 |
| 🟡 **改桌面界面（WPF/XAML）** | 中 | 表现层独立，**改错不会炸服务端** |

> ⚠️ **现在项目还在设计阶段，代码尚未开始** —— 上面大多数任务要等骨架建起来。
> 但 **文档、契约样例**现在就能改。

---

## 9. 提交你的第一个 PR

流程见 [`CONTRIBUTING.md`](../CONTRIBUTING.md) §四 / §五。要点：

- **开分支**（`feat/xxx` / `fix/xxx`），**不要直推 `main`**
- 提交信息用**约定式提交**
- **PR 描述里写清"你怎么验的"** —— 同目录下有个表格说明要写哪几项
- **CI 会自动跑**（这是它存在的意义），**绿了再找 review**

---

## 10. 常见问题（Troubleshooting）

| 症状 | 原因 / 怎么办 |
|---|---|
| 克隆文档库报 **404 / repository not found** | 你还没被加成协作者（或只加了一个仓库）。**私有库无权限时会假装"不存在"** |
| `git status` 里出现 `ai-docs/` | 第 4 步没做或没做对 → 跑一遍 §5 的两条验证 |
| `git add` 警告 `adding embedded git repository` | 同上 —— ⚠️ **别忽略这个警告**。先把 exclude 设好，再 `git reset ai-docs` |
| 克隆私有库时反复要密码 | 用 Git for Windows 自带凭据管理器（首次会弹浏览器登录）；或改用 PAT / SSH |
| `dotnet build` 说找不到 SDK | 装的是 **.NET SDK** 而不是 Runtime？`dotnet --list-sdks` 确认一下 |
| CI 出现红叉 | 点进去看是哪个作业。本仓库 CI 只做**机械检查**（JSON 合法 / 链接不断 / 身份字段是占位值 / 内部目录未入库），报错信息通常直接说清问题 |
| PR 里 CI 一直不跑 | 检查是不是直接推到 `main` 了（CI 只对 PR 和 `main` 的推送触发） |

---

## 11. 卡住了怎么办

**直接开 issue 问，说明三件事**：

1. 你**做了什么**
2. 你**期望**什么
3. 你**实际看到**什么（贴报错原文）

> **这不会显得你菜。** 说清这三件事本身就是一项能力，而且这样别人才帮得上忙。
> 另外：**贴日志/截图前先脱敏** —— 里面有 QQ 号、token 的别原样贴（见 [`CONTRIBUTING.md`](../CONTRIBUTING.md) §十）。
