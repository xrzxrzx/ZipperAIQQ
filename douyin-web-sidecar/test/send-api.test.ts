/**
 * 出站 API 的 wire 形状锁值单测（`POST /v1/message/send`）。
 *
 * 为什么值得单测：这条请求的结构是从**真机抓包**反推的（2026-11-20），
 * 字段号/枚举写错不会本地报错，只会在平台上表现为"发不出去"。
 * 这里把关键字段逐项解回来比对，改错立刻红。
 *
 * 从 `dist/` 引：`src/` 里的相对导入写的是 `./x.js`（NodeNext 要求），
 * 直接跑 `.ts` 源码时解析不到 —— 所以本测试要求先 `npm run build`（`npm test` 已串好）。
 */

import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import protobuf from "protobufjs";

import { ConversationRegistry, encodeSendRequest } from "../dist/send-api.js";

const here = path.dirname(fileURLToPath(import.meta.url));
const protoPath = path.resolve(here, "..", "proto", "im.proto");
const root = await protobuf.load(protoPath);
const Request = root.lookupType("im_proto.Request");

const TARGET = {
  conversationId: "0:1:1000000000:1234567890",
  conversationShortId: "7400863009431650827",
  conversationType: 1,
};

test("encodeSendRequest：cmd=100 且 conversation 三件套齐", () => {
  const bytes = encodeSendRequest(Request, TARGET, "在的喵", "cid-1", 42, 1795192768000);
  const decoded = Request.toObject(Request.decode(bytes), { longs: String, bytes: Buffer, defaults: false }) as {
    cmd: number;
    sequenceId: string;
    sdkVersion: string;
    buildNumber: string;
    body?: {
      sendMessageBody?: {
        conversationId: string;
        conversationType: number;
        conversationShortId: string;
        content: string;
        messageType: number;
        ext: Record<string, string>;
      };
    };
  };

  assert.equal(Number(decoded.cmd), 100, "SEND_MESSAGE 的 cmd 必须是 100");
  assert.equal(String(decoded.sequenceId), "42");
  assert.equal(decoded.sdkVersion, "0.1.8");
  assert.equal(decoded.buildNumber, "0d50935:feat/pc-im-group");

  const send = decoded.body?.sendMessageBody;
  assert.ok(send, "缺少 body.send_message_body（字段号 100）");
  assert.equal(send.conversationId, TARGET.conversationId);
  assert.equal(Number(send.conversationType), 1);
  assert.equal(String(send.conversationShortId), TARGET.conversationShortId);
  assert.equal(Number(send.messageType), 7, "文本消息 message_type 应为 7");
});

test("encodeSendRequest：content 是网页端同款 JSON（aweType=700）", () => {
  const bytes = encodeSendRequest(Request, TARGET, "你猜", "cid-2", 1, 1);
  const decoded = Request.toObject(Request.decode(bytes), { longs: String, bytes: Buffer, defaults: false }) as {
    body?: { sendMessageBody?: { content: string } };
  };

  const content = JSON.parse(String(decoded.body?.sendMessageBody?.content)) as {
    aweType: number;
    type: number;
    richTextInfos: unknown[];
    text: string;
  };

  assert.equal(content.aweType, 700);
  assert.equal(content.type, 0);
  assert.deepEqual(content.richTextInfos, []);
  assert.equal(content.text, "你猜");
});

test("encodeSendRequest：幂等键与时间进 ext（平台按 client_message_id 去重）", () => {
  const bytes = encodeSendRequest(Request, TARGET, "x", "cid-abc", 1, 1795192768000);
  const decoded = Request.toObject(Request.decode(bytes), { longs: String, bytes: Buffer, defaults: false }) as {
    body?: { sendMessageBody?: { ext: Record<string, string> } };
  };

  const ext = decoded.body?.sendMessageBody?.ext ?? {};
  assert.equal(ext["s:client_message_id"], "cid-abc");
  assert.equal(ext["s:stime"], "1795192768000");
  assert.equal(ext["s:mentioned_users"], "");
});

test("ConversationRegistry：记住 / 取出 / 空值不登记", () => {
  const registry = new ConversationRegistry();

  registry.remember("1234567890", TARGET);
  assert.equal(registry.get("1234567890")?.conversationId, TARGET.conversationId);
  assert.equal(registry.size, 1);

  // 没有 conversationId 的不要登记（否则出站会发到空会话）
  registry.remember("other", { conversationId: "", conversationType: 1 });
  assert.equal(registry.get("other"), undefined);
  assert.equal(registry.size, 1);

  // 同一个 threadId 再次收到消息会覆盖（shortId 可能变化）
  registry.remember("1234567890", { ...TARGET, conversationShortId: "999" });
  assert.equal(registry.get("1234567890")?.conversationShortId, "999");
});
