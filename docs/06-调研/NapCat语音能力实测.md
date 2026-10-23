# NapCat 语音能力实测（U12）

> **时间**：2026-10-23
> **目的**：确定 #1「无法读取用户语音」的落地路径 —— NapCat 是否自带语音转文字
> **方式**：只读探测（WebSocket 调 API，不发消息、不改配置、不写文件）
> **相关**：需求 §8.3 U12；问题记录 #1；实现方案 T1.1

## 一、结论先行

| 问题 | 结论 | 依据 |
|---|---|---|
| NapCat 有取语音文件的 API | **有**（`get_record`） | 官方文档 + 运行期不再是 1404 |
| NapCat 有语音转文字 API | **有**（`fetch_ptt_text`） | **运行期实测**：不存在会返回 `1404 不支持的API` |
| `fetch_ptt_text` 的必填参数 | **`message_id`**（不是 `file`） | 实测：只给 `file` → `1400 Schema compilation error`；给 `message_id` → `1200 消息不存在` |
| 能否拿到转写文本 | **尚未验证** | 最近 8 个群各 50 条里没有语音段，缺一条真实语音 |

## 二、实测记录

### 2.1 鉴权

- 测试口（15001）WS 可连通；无 token / 用主配置（17011）的 token 均返回 `1403 token验证失败`。
- 使用测试口专用 token 后：`get_login_info` → `status=ok retcode=0`。
- 注意：同一 WS 连接上事件帧与 API 响应帧会交错，**必须按 `echo` 匹配**，否则会把 lifecycle 事件当成 API 响应（首次探测就踩了这个坑，结论作废重测）。

### 2.2 `fetch_ptt_text` 参数契约（关键发现）

| 传入 | 返回 |
|---|---|
| `{"file":"probe.amr"}` | `1400 Schema compilation error: Expected required property` |
| `{"file_id":"probe.amr"}` | `1400`（同上） |
| `{"file":{"file":"probe.amr"}}` | `1400`（同上） |
| `{"message_id":"1"}` | `1200 消息不存在` ✅ **schema 通过** |
| `{"file":"probe.amr","message_id":"1"}` | `1200 消息不存在` ✅ schema 通过 |

对照：不存在的 action 返回 **`1404 不支持的API`**，因此 `1400` 说明「action 存在、参数不对」。

### 2.3 `get_record`

- `{"file":...}` / `{"file_id":...}` / `{"file":{...}}` 均为 `1400 Schema compilation error`。
- 本项目**不需要**它（转写只需 `message_id`），后续要下载音频本体时再单独查参数。

### 2.4 真实语音扫描

- `get_group_list` → 8 个群。
- 对每个群调 `get_group_msg_history`（`count=50, message_seq=0`，不带 `message_seq` 会报「消息undefined不存在」）。
- 段类型统计：`at` / `face` / `image` / `json` / `text` —— **没有 `record`**。
- 结论：需要一条**真实语音**才能验证 `data.text` 的返回形状。

## 三、对本项目的影响

1. 代码已按实测契约调整：`AudioClip.MessageId`，调用参数为
   `{"message_id": "...", "file": "..."}`；没有 `message_id` 时**不发请求**。
2. 转写失败 / 连续 3 次失败 → 熔断禁用，语音降级为 `[语音]` 占位，**不阻塞消息**。
3. 不引入本地 ASR 模型（Q5 定稿）。

## 四、待办

- [ ] 在测试群/私聊发一条语音，取该消息 `message_id` 调 `fetch_ptt_text`，确认返回形状（预期 `data.text`）
- [ ] 若返回形状不同（如 `data` 为字符串），调整 `NapCatPttTextTranscriber.ExtractText`
- [ ] 端到端：服务端运行时收到语音 → 日志出现「语音已转写」→ `SegmentsJson` 里 `text` 非空
