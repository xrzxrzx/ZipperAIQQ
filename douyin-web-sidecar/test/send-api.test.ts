/**
 * 出站 API 的 wire 形状锁值单测。
 *
 * 依据：2026-11-20/21 真机抓包 + 2026-11-21 打通（`status_code=0 OK`）。
 * 这些字段写错不会本地报错，只会在平台上表现为「发不出去」或 `empty token`。
 */

import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import protobuf from "protobufjs";

import { buildSendPayload, ConversationRegistry } from "../dist/send-api.js";

const here = path.dirname(fileURLToPath(import.meta.url));
const protoPath = path.resolve(here, "..", "proto", "im.proto");
const root = await protobuf.load(protoPath);
const Request = root.lookupType("im_proto.Request");

const TARGET = {
  conversationId: "0:1:1000000000:1234567890",
  conversationShortId: "7400863009431650827",
  conversationType: 1,
};

// 真机会话模板的最小形状（headers 里的凭证决定「发不发得出去」）
const TEMPLATE = {
  sdkVersion: "0.1.8",
  refer: 3,
  inboxType: 0,
  buildNumber: "0d50935:feat/pc-im-group",
  versionCode: "360000",
  authType: 1,
  headers: { session_aid: "6383", session_did: "did", identity_security_token: "tok" },
};

type Decoded = {
  cmd: number;
  sequenceId: string;
  channel: string;
  versionCode: string;
  authType: number;
  headers: Record<string, string>;
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

function decode(bytes: Buffer): Decoded {
  return Request.toObject(Request.decode(bytes), {
    longs: String,
    bytes: Buffer,
    defaults: false,
  }) as Decoded;
}

test("buildSendPayload：cmd=100、会话三件套、message_type=7", () => {
  const decoded = decode(buildSendPayload(Request, TEMPLATE, TARGET, "在的喵", "cid-1", 42, 1795192768000));

  assert.equal(Number(decoded.cmd), 100, "SEND_MESSAGE 的 cmd 必须是 100");
  assert.equal(String(decoded.sequenceId), "42");

  const send = decoded.body?.sendMessageBody;
  assert.ok(send, "缺少 body.send_message_body（字段号 100）");
  assert.equal(send.conversationId, TARGET.conversationId);
  assert.equal(Number(send.conversationType), 1);
  assert.equal(String(send.conversationShortId), TARGET.conversationShortId);
  assert.equal(Number(send.messageType), 7);
});

test("buildSendPayload：模板里的会话凭证原样带出去（否则平台报 empty token）", () => {
  const decoded = decode(buildSendPayload(Request, TEMPLATE, TARGET, "x", "cid", 1, 1));

  assert.equal(decoded.versionCode, "360000");
  assert.equal(Number(decoded.authType), 1);
  assert.deepEqual(decoded.headers, TEMPLATE.headers);
});

test("buildSendPayload：biz/access 必须在（缺了平台报 invalid access param）", () => {
  const decoded = decode(buildSendPayload(Request, TEMPLATE, TARGET, "x", "cid", 1, 1)) as Decoded & { biz: string; access: string; token: string };

  assert.equal(decoded.biz, "douyin_web");
  assert.equal(decoded.access, "web_sdk");
  assert.equal(decoded.token, "", "token 要写空串（proto2 存在性）");
});

test("buildSendPayload：不带 channel（实测发送家族没有这个字段）", () => {
  const decoded = decode(buildSendPayload(Request, TEMPLATE, TARGET, "x", "cid", 1, 1));
  // 模板里若没有 channel，就不能凭空补一个（抓包里没有这个字段）
  assert.equal((decoded as { channel?: string }).channel, undefined);
});

test("buildSendPayload：content 是网页端同款 JSON，ext 带幂等键与时间", () => {
  const decoded = decode(buildSendPayload(Request, TEMPLATE, TARGET, "你猜", "cid-abc", 1, 1795192768000));
  const send = decoded.body?.sendMessageBody;

  const content = JSON.parse(String(send?.content)) as {
    aweType: number;
    type: number;
    richTextInfos: unknown[];
    text: string;
  };

  assert.equal(content.aweType, 700);
  assert.equal(content.type, 0);
  assert.deepEqual(content.richTextInfos, []);
  assert.equal(content.text, "你猜");

  const ext = send?.ext ?? {};
  assert.equal(ext["s:client_message_id"], "cid-abc");
  assert.equal(ext["s:stime"], "1795192768000");
  assert.equal(ext["s:mentioned_users"], "");
});

test("ConversationRegistry：记住 / 取出 / 空值不登记 / noteSent 只改 lastSent", () => {
  const registry = new ConversationRegistry();

  registry.remember("1234567890", TARGET);
  assert.equal(registry.get("1234567890")?.conversationId, TARGET.conversationId);
  assert.equal(registry.size, 1);

  registry.remember("other", { conversationId: "", conversationType: 1 });
  assert.equal(registry.get("other"), undefined);
  assert.equal(registry.size, 1);

  registry.noteSent("1234567890", "刚发出去的那条");
  assert.equal(registry.get("1234567890")?.lastSent, "刚发出去的那条");
  assert.equal(registry.get("1234567890")?.conversationId, TARGET.conversationId);
});
