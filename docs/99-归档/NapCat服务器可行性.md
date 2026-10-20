# NapCat 服务器部署可行性评估

> ## ⚠️ 本文已降为**历史存档**（2026-09-22）
>
> **本项目已改用自有服务器（T30）**，那台阿里云服务器决定**"各管各的"**（不再参与本项目）。
>
> **所以本文的结论不再作为本项目的部署前提。** 但**保留价值**在于：
> - §2.3 的环境实测数据（Xvfb 依赖、LinuxQQ 包体积）**换到 T30 上同样有参考意义**
> - 那段**「Xvfb 怎么装、要装什么」**的经验，正是 T30 走 Linux 路线时会用到的
>
> 👉 **当前部署设计见 [`03-设计/部署设计.md`](../03-设计/部署设计.md)。**

> **评估日期**：2026-09-20
> **目标服务器**：`zipper-server`（8.141.89.50，标签 `production`）
> **当前状态**：**仅评估 —— 未安装任何 QQ / NapCat 组件**（仅安装了 Xvfb 及其依赖，经用户确认保留）
> **评估方式**：远端只读探测 + 依赖 dry-run 解析 + 官方文档核对 + 上游安装包体积核查
> **证据标注约定**：`[实测]` = 本机/远端实际命令输出；`[文档]` = 官方文档原文；`[估算]` = 推导值，非实测；`[未验证]` = 明确没有验证

---

## 1. 结论摘要

| 维度 | 判定 | 依据 |
|---|---|---|
| 系统与依赖 | ✅ **可行** | Xvfb 已装且可跑；Electron 依赖齐备（含 `libgbm`） |
| 官方支持 | ✅ **可行** | NapCat 官方文档明确给出 Redhat 系安装命令；本机非 RHEL 10+，不是死路 |
| 安装包获取 | ⚠️ **可行但需绕路** | 腾讯官方 NTQQ 链接**全部失效**；需用 GitHub 第三方存档 + SHA256 校验 |
| 磁盘 | ✅ **充足** | 需约 0.6–1 GB，现剩 6.6 GB |
| **内存** | ⚠️ **紧张 —— 主要风险** | 可用约 1.05 GB，需常驻 400–800 MB |
| **风控** | ❓ **未验证** | 必须真实扫码登录才能判定；issue #931 失败案例环境即阿里云 |

**总判定：技术上可行，但内存是硬约束。** 建议部署时**必须配合 cgroup 内存沙箱**，否则会加剧已有 OOM 问题。

---

## 2. 环境评估（实测）

### 2.1 系统

| 项 | 值 |
|---|---|
| 发行版 | Alibaba Cloud Linux 3 (OpenAnolis Edition)，`alinux-release 3.2104.13.2` |
| RHEL 对应 | **Anolis 8 系 ≈ RHEL 8**（包后缀 `.al8`） |
| 内核/架构 | x86_64 |
| 资源 | **2 核 / 1870 MB 内存 / 40 GB 磁盘（剩 6.6 GB）/ 2 GB swap** |
| 已运行 | 44 天；同时承载 8+ 生产服务 |

### 2.2 Xvfb（无头显示）—— 已装并验证

| 项 | 值 |
|---|---|
| 包 | `xorg-x11-server-Xvfb-1.20.11-28.0.1.al8.3` + `xorg-x11-xauth-1.1-10.al8` |
| 依赖 | 共 12 包 / 3.9 MB，**全部来自 alinux3 源，无需 EPEL** |
| 启动实测 | ✅ 成功（`Xvfb :98 -screen 0 1280x800x24`） |
| **内存占用实测** | **RSS 16.6 MB**（极轻，不构成负担） |

> 官方文档原文要求的正是这两个包：Redhat 系 `sudo dnf install xorg-x11-server-Xvfb xorg-x11-xauth`。
> 官方同时警告：**RHEL 10+ / CentOS Stream 10+ 因 X11 被完全移除，无法通过 xvfb 无头运行，且无已知替代方案** —— 本机为 RHEL 8 系，**不在这个死路上**。

### 2.3 Electron / LinuxQQ 依赖

| 依赖 | 状态 |
|---|---|
| `nss` 3.101.0 / `atk` 2.36.0 / `gtk3` 3.24.31 | ✅ 已在 |
| `alsa-lib` 1.2.14 / `cups-libs` 2.2.6 | ✅ 已在 |
| **`mesa-libgbm` 23.1.4**（`libgbm.so.1` 在 `/lib64/`） | ✅ **已在** |
| `chromium` 133.0.6943.141 | ✅ 已在（2026-09-16 安装） |
| `libXScrnSaver` | ⚠️ 缺失，可从 alinux3 源装（31 KB） |
| `liberation-fonts` | ⚠️ 缺失，可从 alinux3 源装（4.2 MB） |

**判定**：依赖层面**已基本就绪**，「RHEL 系不在 NapCat 官方支持列表」不构成实质阻碍。

---

## 3. 性能评估

### 3.1 内存（核心风险）

| 项 | 值 | 来源 |
|---|---|---|
| 物理内存 | **1870 MB** | `[实测]` |
| 当前可用（MemAvailable） | **约 1050 MB** | `[实测]` |
| swap 总量 / 已用 | 2047 MB / 291 MB | `[实测]` |
| **`vm.swappiness`** | **0** —— 系统几乎拒绝使用 swap | `[实测]` |
| LinuxQQ（Electron/Chromium） | **300–600 MB** | `[估算]`，**未实测** |
| NapCat（Node 进程） | **100–200 MB** | `[估算]`，**未实测** |
| **合计预计常驻** | **400–800 MB** | `[估算]` |
| **部署后余量** | **约 250–650 MB** | `[估算]` |

### 3.2 OOM 历史（已发生的事实）

近 30 天共 **84 次 OOM 事件**，分布：

| 日期 | 次数 |
|---|---|
| 2026-08-24 | 35 |
| 2026-09-06 | 26 |
| 2026-09-16 | 13 |
| 2026-09-17 | 10 |

**被 OOM killer 杀掉的进程**（部分）：

| 进程 | 次数 | 性质 |
|---|---|---|
| `bash` | 17 | 会话 |
| **`mysqld`** | **14** | 🔴 数据库 |
| **`gbase-api`** | **14** | 🔴 生产 API |
| `argusagent` | 10 | 监控 |
| `gbase-worker` | 8 | 生产 worker |
| `systemd-journal` | 6 | 日志 |
| **`zipper-agent-memory`** | **4** | 🔴 记忆库服务 |
| `temporalite` / `gbase-channel-g` / `AliYunDun*` | 各 4 | — |
| `sshd` / `gbase-dingtalk` / `sap-api` | 3 / 3 / 2 | — |

> **用户说明（2026-09-20）**：这些 OOM 是**走流水线部署**时造成的，非日常状态。数据佐证：OOM 集中在 4 天，且 9/16（装 chromium）与 9/17 的 OOM 与新增负载时间吻合。
>
> **但由此得出的推论仍然成立**：这台机器**对新增内存负载的容忍度很低**。NapCat + LinuxQQ 是**常驻**的 400–800 MB，它会在**下一次流水线部署时**成为压垮内存的那部分。

### 3.3 缓解手段（已验证可用）

| 手段 | 状态 | 说明 |
|---|---|---|
| **cgroup v1 memory 控制器** | ✅ **可用** | `/sys/fs/cgroup/07-记忆/memory.limit_in_bytes` 存在 |
| **`systemd-run -p MemoryLimit=`** | ✅ **实测生效** | 已用 `systemd-run --scope -q -p MemoryLimit=50M /bin/true` 验证通过 |
| `OOMScoreAdjust` | ✅ 可用（systemd 239） | 可让内核**优先杀 NapCat 而非 mysqld** |
| 调 `vm.swappiness` | ⚠️ **建议但需批准** | 当前为 0；调到 10–60 可让 1.7 GB 空闲 swap 真正发挥作用 |

**这是本评估最有价值的发现**：可以把 LinuxQQ 关进内存笼子（硬上限 + 优先被杀），**从而保护生产服务**。

### 3.4 磁盘

| 项 | 值 |
|---|---|
| 当前余量 | **6.6 GB**（83% 已用） |
| NTQQ rpm 包 | **178.66 MB**（`QQ_3.2.32_260812_x86_64_01.rpm`）`[实测]` |
| NapCat.Shell.zip | **28.1 MB**（29,498,670 字节）`[实测]` |
| 安装后 `/opt/QQ` | 约 400–550 MB `[估算]` |
| QQ 数据（登录后） | 约 100–300 MB `[估算]` |
| **合计预计占用** | **约 0.6–1 GB** |
| 部署后余量 | 约 5.6–6 GB ✅ |

### 3.5 CPU

2 核，load average 0.39 / 0.27 / 0.16（空闲）。LinuxQQ 无头空转 CPU 开销低，但**消息量大或图片处理时**会有峰值。`[估算，未实测]`

---

## 4. 部署路径评估

### 4.1 官方文档步骤（Redhat 系）

依据 NapCat 官方文档 BootWay03（半自动）：

1. 安装 QQ + `xvfb` + `xauth`（前两包**已装**）
2. 创建挂载脚本 `/opt/QQ/resources/app/loadNapCat.cjs`
3. 安装 NapCat Shell 到 `/opt/QQ/resources/app/napcat`（须存在 `napcat.mjs`）
4. 修补 `/opt/QQ/resources/app/package.json` 的 `main` 为 `./loadNapCat.cjs`
5. 启动：`xvfb-run -a qq --no-sandbox -q <QQ号>`
6. （可选）systemd 自启

> 官方提示：**未配置快速登录或快速登录失败时，仍需手动扫码**。

### 4.2 安装包获取 —— 需绕路（重要）

| 来源 | 状态 |
|---|---|
| `dldir1.qq.com`（NapCat release body 引用） | ❌ **404**（`linuxqq_3.2.23-44343_x86_64.rpm`） |
| `qqdl.gtimg.cn`（NapCat 安装脚本引用） | ❌ **404**（`linuxqq_3.2.30-50828_x86_64.rpm`） |
| QQ 官网下载页 `config1.js` | ❌ **指向 2020 年的 QQ Linux 2.0**（`linuxqq_2.0.0-b2-1089`），非 NTQQ 3.x |
| **GitHub `Rodert/qq-versions`**（第三方存档） | ✅ **可用**：`QQ_3.2.32_260812_x86_64_01.rpm`（178.66 MB） |

> **对照实验**：同样的 URL 从用户本机（Windows）访问**也是 404** → 证明是**链接失效**，不是服务器 IP 被 CDN 区别对待。
>
> ⚠️ **供应链风险**：只能用**第三方分发**，必须校验 `SHA256SUMS.txt`。这是需要用户知情接受的风险。

第三方仓库维护活跃度 `[实测]`：release 覆盖 2026-05-11 / 05-28 / 07-20 / 07-30 / 08-12 / 08-13。

---

## 5. 风险清单

| # | 风险 | 等级 | 说明 | 缓解 |
|---|---|---|---|---|
| R1 | **内存不足引发 OOM** | 🔴 高 | 近 30 天已 84 次，`mysqld` 被杀 14 次 | cgroup `MemoryLimit` + `OOMScoreAdjust`；建议调 `swappiness` |
| R2 | **腾讯风控拦住登录** | 🔴 高 | issue #931 失败案例环境即阿里云 Ubuntu 24.04；官方未给通用解 | 无法预先规避，**只能真登录验证**；失败则回退本机方案 |
| R3 | **第三方包供应链** | 🟡 中 | 官方源失效，只能用 GitHub 存档 | 校验 `SHA256SUMS.txt`；记录来源与版本 |
| R4 | **版本漂移** | 🟡 中 | NapCat 更新极快（v4.18.28，一天连发 3 版）；QQ 版本需匹配 | 锁定版本，升级前先在测试环境验证 |
| R5 | **QQ 号与常用号同 IP** | 🟡 中 | NapCat 官方安全页明确警告：避免同 IP 同时登录 Bot 与常用账号 | 使用独立 QQ 号；评估是否需要独立出口 IP |
| R6 | **单实例单账号** | 🟢 低 | 官方 collaborator 明确不支持多账号与账号切换 | 多账号需多实例（多目录 + 多端口） |
| R7 | **rpm 依赖不满足** | 🟢 低 | 常见 Electron 依赖已核查齐备，但**未实际安装验证** | 安装前跑 `rpm -qpR` + `dnf localinstall --assumeno` |

---

## 6. 明确未验证的项（不编造）

| # | 项 | 为什么没验证 |
|---|---|---|
| U1 | **LinuxQQ 实际内存占用** | 未安装 QQ（用户要求仅评估）→ 300–600 MB 只是估算 |
| U2 | **NapCat 实际内存占用** | 同上 |
| U3 | **腾讯风控是否放行** | 需真实扫码登录 |
| U4 | **rpm 依赖是否 100% 满足** | 未下载包、未跑 `rpm -qpR`；只核对了常见 Electron 依赖 |
| U5 | **Xvfb 下 LinuxQQ 能否正常渲染** | 未装 QQ，无法验证 |
| U6 | **消息高峰时的 CPU 表现** | 未运行，无数据 |

---

## 7. 如果决定部署（建议方案，待批准）

| 步 | 动作 | 说明 |
|---|---|---|
| 1 | 下载 NTQQ rpm + NapCat.Shell.zip | 从 `Rodert/qq-versions`；**校验 SHA256** |
| 2 | 依赖检查（dry-run） | `rpm -qpR` + `dnf localinstall --assumeno` |
| 3 | 补依赖 | `dnf install libXScrnSaver liberation-fonts`（4.2 MB） |
| 4 | 安装 NTQQ | `dnf localinstall QQ_3.2.32_260812_x86_64_01.rpm` |
| 5 | 部署 NapCat | 解压到 `/opt/QQ/resources/app/napcat`；按官方文档打 `loadNapCat.cjs` 补丁并改 `package.json` 的 `main` |
| 6 | **🛡️ 沙箱启动** | `systemd-run --scope -p MemoryLimit=700M -p OOMScoreAdjust=500 xvfb-run -a qq --no-sandbox` |
| 7 | 实测性能 | RSS / CPU / swap，对照装前基线（可用 ~1050 MB） |
| 8 | 扫码登录 | 验证 R2（风控）—— 需用户配合 |
| 9 | 观察 | 建议观察一个完整部署周期，确认 OOM 是否加剧 |

**回滚**：`dnf remove linuxqq`（或 `rpm -e linuxqq`）+ `rm -rf /opt/QQ` + 删除临时文件 → **完全可逆**。

---

## 8. 替代方案（评估中发现的更优可能）

### 8.1 Lagrange.Core —— 纯 C# 的 NTQQ 协议实现

| 项 | 值 |
|---|---|
| 仓库 | https://github.com/LagrangeDev/Lagrange.Core |
| 自述 | "An Implementation of NTQQ Protocol, with Pure C#" |
| 规模/活跃 | 2987 star，最近 push 2026-09-07 `[实测]` |
| **关键优势** | **不需要 QQ 客户端、不需要 Xvfb、不需要 LinuxQQ** —— 是个 .NET 应用 |
| 对本项目的意义 | ① 绕开 LinuxQQ 的下载与内存开销（省 300–600 MB）② **语言与项目技术栈一致（C#），可直接引用** |
| 代价 | 生态/文档/协议覆盖面弱于 NapCat；纯协议实现的风控风险**可能更高** `[未验证]` |

### 8.2 其他

- **本机 Windows 跑 NapCat**：OneKey 包内置 QQ，零风控、零内存压力、零成本；服务端跨平台设计使将来迁移零重写
- **升级服务器内存**：唯一能让「NapCat + 现有生产服务」稳定共存的方案

---

## 9. 一句话结论

**技术上完全可行**（依赖齐备、官方支持 Redhat 系、有可用下载源、有 cgroup 内存沙箱），
**但内存是真实天花板**（1.87 GB 总量、已 84 次 OOM、需常驻 400–800 MB），
**并且风控这一关无法预先验证**。

若部署，**必须带内存沙箱**；否则建议先考虑替代方案（Lagrange.Core / 本机 / 升配）。
