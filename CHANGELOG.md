# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.5.2] - 2026-10-29

完整的发布说明见 [`docs/发布/v0.5.2.md`](docs/发布/v0.5.2.md)。

### Fixed
- **黑名单被「主人自动放行」绕过**：群被拉黑后仍回复。判定顺序改为 **黑名单 → 主人放行 → 白名单**；
  `OwnerScopeResolver` 不再把已拉黑会话写进 `AlwaysAllow`，`ChatFilter` 写入时也会剔除
- 客户端文案同步改为「主人放行（自动；黑名单优先）」

### Changed
- 行为变化：主人显式拉黑自己所在的群 / 私聊后，现在会真的被拦（黑名单语义优先）

## [0.5.1] - 2026-10-28

完整的发布说明见 [`docs/发布/v0.5.1.md`](docs/发布/v0.5.1.md)。

### Added
- 外部 WS token **加密落库**（`ISecretProtector`），旧明文启动时自动迁移
- 平台 selfId 启动回灌内存注册表（握手前也能正确判断 @）
- 邮件摘要按 `MaxConcurrentSummaries` 真并发
- 出站队列（512，DropOldest）、正向 WS 单帧（1MB）、网页抓取（4MB）上限
- 命令目录一致性测试、ChatAgent 内存历史测试（测试总数 348 → 373）

### Changed
- **契约**：`getExternal` 不再回显 `token`，改回 `tokenConfigured`；`setExternal` 的 `token` 改为三态（不传保持 / `""` 清除 / 非空更新）
- 状态页 / `/status` 显示运行时模型；`/compact` 使用运行时热窗
- 主人反馈按平台存键（`feedback.{platform}.*`，qq 镜像旧键）
- `ServiceRegistration` 拆分为 Mail / Channels / Agent partial；新增 `MetricFacts` 统一指标口径
- 客户端 DTO 从 1016 行拆为 8 个业务域文件

### Fixed
- 只写 `server.yaml` 无法启动（必需项校验移到 YAML 合并后）
- 清空管理员 / 白名单后重启复活
- 邮件管道源启动失败后永久停摆
- 外部 WS token 明文落库 / 明文回显
- `setBehavior` / `setConfig` 先写后校验导致的半套状态
- 缺 payload / 非对象 payload 返回 500（现为 400）
- 人设 `{"enabled": false}` 被误拒；全局人设缺 name 固定回退「小衣」
- 抖音 token 显式清空被启动配置复活；删除废弃 YAML `douyin.allowedSessions`
- 客户端 `async void` 崩溃、登录轮询超时被吞、二维码流泄漏、审计页订阅泄漏

## [0.5.0] - 2026-10-28

完整的发布说明见 [`docs/发布/v0.5.0.md`](docs/发布/v0.5.0.md)。

### Added
- 多通道骨架：`IMessageSource` + `CompositeMessageChannel`（合并入站 / 按平台路由 / 故障隔离 / 能力并集）
- 邮件接入（IMAP）：收信 → 摘要 → 汇总；账号管理与「立即同步」；凭据只回「已配置」
- 邮件汇总先交人格 agent 转告主人，失败才回落直发；润色可关
- 抖音接入：Node sidecar + `/ws/channel`（登录 / 收 / 发 / 联系人查询），服务端 25s 心跳
- 客户端「第三方平台接入」页：平台总览、邮件账号、抖音配置、**登录 / 退出 / 二维码**
- 平台化配置：主人 / 管理员 / 名单 / 通知 / 工具权限按平台一套（`access.{platform}.*`，含旧键回退）
- 运行时热切：邮件接入、人格润色、抖音启用改完即生效（不必重启）
- 消息查询跨平台：结果带 `[平台:会话]`、可按平台过滤；提示词注入当前平台与"当前说话的人是谁"
- `purgeSession`：删除指定会话全部消息

### Changed
- 会话白名单统一到「人员与名单」（`ChatFilter`，支持 `douyin:p*` 前缀通配）
- 会话标题按平台显示（`抖音 · 拉链`），不再回落原始 session id

### Fixed
- `saveDouyin` 契约不一致（`saved` 是计数）导致配置存不上
- 抖音登录二维码拿不到（`qrcodeAt` 可空 + 先连 Server 再登录）
- 退出登录后仍显示已登录（清登录态目录 + 丢弃客户端 + 服务端清 selfId）
- 163 水位卡死（旧 UID 重投→自行过滤取最新）；成功后清除 `LastError`
- 抖音卡片类消息把原始 JSON 当正文
- 客户端对话框/按钮主题不统一（显式 ControlTemplate）

## [0.4.0] - 2026-10-25

完整的发布说明见 [`docs/发布/v0.4.0.md`](docs/发布/v0.4.0.md)。

### Added
- 消息时间带日期（`MM-dd HH:mm:ss`）
- 图片即时下载、真实 MIME、GIF/WebP 首帧转 PNG、失败占位与 30 分钟缓存
- 定时任务支持目标会话（`qq:p*` / `qq:g*`）
- 主人反馈：撤回（带原文）/ 外部接口消息，可配目标
- 外部 WS：反向多监听块、正向多目标、客户端 Pivot 卡片配置与编辑对话框
- 网页搜索独立配置（模型配置页，自有 BaseUrl + 加密 Key）

### Fixed
- 表情包读不到（rkey 过期、MIME 误判）
- 图片失败静默
- CI 真实 QQ 号守卫误报、日志时区错位
- 外部接口编辑框主题不一致

## [0.3.0] - 2026-10-25

完整的发布说明见 [`docs/发布/v0.3.0.md`](docs/发布/v0.3.0.md)。

### Added
- 网页搜索对齐官方 `/v1/tools/search` + `/v1/tools/fetch`（`count` / `need_content`，官方优先 + 自建回落）
- `search.enabled` / `search.baseUrl` 配置
- 主人优先：主人可要求改称呼 / 设定 / 扮演，安全与工具权限不让渡
- 自我身份：`bot.selfId` 持久化、提示词注入「你自己是谁」、`getSelfInfo` 诊断
- 戳一戳：别人戳别人也响应，正文标注「不是你」；戳机器人用「戳了你」

### Fixed
- 网页搜索 404、参数名错误、fetch 未走官方接口
- 搜索失败被伪装成「没搜到」
- 戳 AI 无响应（`Target == self` 被误丢）
- 自己入群被当成别人入群
- 日志检索在 UTC 机器上的日期/时间范围错位

## [0.2.0] - 2026-10-23

完整的发布说明见 [`docs/发布/v0.2.0.md`](docs/发布/v0.2.0.md)。

### Added
- 语音消息转写（通道层 `fetch_ptt_text`，失败重试 + 熔断降级）
- 会话昵称、会话搜索、按时间区间导出、消息分段高亮
- 数据页：缓存命中率、按会话筛选、类别中文名
- 配置页拆分为模型配置 / 运行与行为 / 人设 / 人员与名单
- 工具 / 命令 / NapCat API 三类策略管理页（开关 + 最低角色）
- 通知类型扩到 17 类可配（加好友/加群请求只记录）
- 系统提示词（与人设分开、可编辑、可恢复默认）
- 服务端日志检索与等级高亮、AI 按时间检索记忆、网页搜索工具（实验）、重启服务端入口
- 拆分 `MainViewModel` 为 Shell / Status / Audit + 各页独立 VM

### Fixed
- 自己入群触发通知响应
- 未知表情统一显示为 `[表情:未知]`
- 黑白名单不能并存、主人不在白名单、拉黑管理员无提示
- 管理员重启后丢失
- 端点读取恒为 `judge`、端点名可被改名
- 保存无幂等、审计页不自动滚底
- 日志查询 500（文件共享模式）
- 策略层启动死循环依赖

### Changed
- 数据库 schema v12（自动迁移，只增列）
- 导航分组结构与展开状态记忆
- 重启需要外部守护脚本 `scripts/run-server.ps1`

### Known limitations
- 表情目录未补全（不编造名称）
- 网页搜索调用形态待实测
- #21 暂缓、#33 已撤销

## [0.1.0] - 2026-10-22

首个可运行版本：M1 通道 / 存储 / Agent / 命令 / 权限 + M2 客户端与服务端 API。
