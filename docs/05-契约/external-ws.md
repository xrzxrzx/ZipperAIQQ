# 外部应用 WebSocket 契约（v1）

> 问题记录 03 #5 · 端点：`/ws/ext`（反向：外部连入） / 正向：本系统连出 `external.forwardUrl`
> 帧为 UTF-8 JSON；时间一律 Unix 毫秒。

## 1. 鉴权

```
Authorization: Bearer <token>
```

- 复用服务端 HTTP token（`ZIPPER_HTTP_TOKEN`）。
- loopback 连接免 token（与 HTTP 一致）。
- 鉴权失败：HTTP 401，不升级 WebSocket。

## 2. 连接建立（服务端 → 客户端）

```json
{"type":"hello","time":1730000000000,"data":{"version":"1","selfId":"10001","capabilities":"Text, Image"}}
```

## 3. 事件推送（服务端 → 客户端）

只有客户端**订阅**过的类型才会推。

```json
{"type":"event","time":1730000000000,"data":{
  "kind":"message",
  "session":"qq:p10001",
  "payload":{
    "messageId":"123",
    "chatType":"Private",
    "sender":{"userId":"10001","nickname":"拉链","displayName":"拉链"},
    "text":"你好",
    "time":1730000000000,
    "capabilities":"Text, Image"
  }
}}
```

```json
{"type":"event","time":1730000000000,"data":{
  "kind":"notice",
  "session":"qq:g123",
  "payload":{
    "type":"MessageRecalled",
    "rawType":"notice.group_recall",
    "actor":"20001",
    "target":null,
    "relatedMessageId":"456",
    "time":1730000000000
  }
}}
```

## 4. 客户端命令

### 4.1 订阅 / 退订

```json
{"type":"subscribe","events":["message","notice"]}
```

- 默认订阅 message + notice；传空数组即全部关闭。
- 回 `ack`。

### 4.2 发送消息

```json
{"type":"send","requestId":"r1","session":"qq:p10001","text":"来自外部应用"}
```

- 需要服务端 `external.allowSend=true`，否则 `ack.ok=false`。
- `session` 格式：`qq:p<QQ号>` / `qq:g<群号>`。
- 可配置白名单 `external.allowedSessions`（逗号分隔，支持 `qq:p*` 前缀）。

### 4.3 心跳

```json
{"type":"ping"}
```

## 5. 应答（服务端 → 客户端）

```json
{"type":"ack","time":1730000000000,"data":{"requestId":"r1","ok":true,"error":null}}
```

失败示例：

```json
{"type":"ack","time":1730000000000,"data":{"requestId":"r1","ok":false,"error":"外部发消息未开启（external.allowSend=false）。"}}
```

## 6. 配置

| 键（env / yaml） | 默认 | 说明 |
|---|---|---|
| `ZIPPER_EXTERNAL_ALLOWSEND` / `external.allowSend` | `false` | 是否允许外部应用发消息 |
| `ZIPPER_EXTERNAL_ALLOWEDSESSIONS` / `external.allowedSessions` | 空 | 允许的会话（空=全部；支持 `qq:p*`） |
| `ZIPPER_EXTERNAL_FORWARDURL` / `external.forwardUrl` | 空 | 正向 WS 目标（ws/wss），空=不连出 |
| `ZIPPER_EXTERNAL_FORWARDTOKEN` / `external.forwardToken` | 空 | 正向 WS 的 Bearer token |

## 7. 行为说明

- 反向端点：`ws://<host>:5180/ws/ext`。
- 正向：配置 `forwardUrl` 后，服务启动即尝试连出；断线 5s→10s→30s 退避重连。
- 外部应用发的消息**不进入 Agent**（不会触发 AI 回复），只作为通道消息发出。
- 同一套帧协议同时用于反向与正向连接。
