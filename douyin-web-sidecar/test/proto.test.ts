/**
 * `proto/im.proto` 的自检：**字段号不许被改动**。
 *
 * 为什么值得单测：这份 proto 是从上游裁剪来的，字段号就是线上 wire 契约。
 * 谁手一抖改个数字，编译照过、单测里的合成数据也照过（自己编码自己解码是自洽的），
 * 但真机上就再也解不出消息了 —— 那种故障极难定位，所以在字段号上加锁。
 *
 * 另外跑一遍「编 → 解 → 归一化」的闭环，顺带验证 `Response.cmd=500 → 新消息` 的处理链。
 */

import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";
import protobuf from "protobufjs";

import { normalizePrivateMessage } from "../src/protocol.ts";

const here = path.dirname(fileURLToPath(import.meta.url));
const protoPath = path.resolve(here, "..", "proto", "im.proto");

const root = await protobuf.load(protoPath);

test("关键 message 存在", () => {
  for (const name of [
    "im_proto.Frame",
    "im_proto.StrangerNewMessageNotify",
    "im_proto.Request",
    "im_proto.RequestBody",
    "im_proto.SendMessageRequestBody",
    "im_proto.Response",
    "im_proto.ResponseBody",
    "im_proto.NewMessageNotify",
    "im_proto.MessageBody",
  ]) {
    assert.ok(root.lookupType(name), `缺少 message：${name}`);
  }
});

test("关键字段号与上游一致（改了这里就是改了线上契约）", () => {
  const expected: Record<string, Record<string, number>> = {
    "im_proto.Frame": { seqid: 1, logid: 2, service: 3, method: 4, headers: 5, payload: 8 },
    // ⚠️ protobufjs 把 snake_case 字段名转成 camelCase 作为键，这里按它的键写
    "im_proto.Response": { cmd: 1, statusCode: 3, body: 6, headers: 8 },
    "im_proto.ResponseBody": { hasNewMessageNotify: 500, strangerHasNewMessageNotify: 503 },
    "im_proto.NewMessageNotify": { conversationId: 2, conversationType: 3, message: 5 },
    "im_proto.MessageBody": {
      conversationId: 1,
      conversationType: 2,
      serverMessageId: 3,
      conversationShortId: 5,
      messageType: 6,
      sender: 7,
      content: 8,
      createTime: 10,
      referenceInfo: 18,
    },
  };

  for (const [typeName, fields] of Object.entries(expected)) {
    const type = root.lookupType(typeName);
    for (const [field, id] of Object.entries(fields)) {
      const actual = type.fields[field];
      assert.ok(actual, `${typeName} 缺少字段 ${field}`);
      assert.equal(actual.id, id, `${typeName}.${field} 字段号变了（应为 ${id}）`);
    }
  }
});

test("帧编解码闭环：service=5 的私信帧 → 归一化出文本", () => {
  const Response = root.lookupType("im_proto.Response");
  const Frame = root.lookupType("im_proto.Frame");

  const self = "1000000000";
  const peer = "1234567890";

  const responsePayload = Response.encode(
    Response.create({
      cmd: 500,
      sequenceId: 1,
      statusCode: 0,
      body: {
        hasNewMessageNotify: {
          conversationId: `0:1:${self}:${peer}`,
          conversationType: 1,
          notifyType: 1,
          message: {
            conversationId: `0:1:${self}:${peer}`,
            conversationType: 1,
            serverMessageId: "7709523357389440570",
            conversationShortId: "7400863009431650827",
            messageType: 7,
            sender: "1000000000",
            content: '{"text":"舅舅家","aweType":700}',
            createTime: "1795013288293",
          },
        },
      },
    }),
  ).finish();

  const bytes = Frame.encode(
    Frame.create({
      seqid: "1",
      logid: "2",
      service: 5,
      method: 1,
      payload: responsePayload,
    }),
  ).finish();

  const frame = Frame.toObject(Frame.decode(bytes), { longs: String, bytes: Buffer });
  assert.equal(Number(frame.service), 5);

  const response = Response.toObject(Response.decode(frame.payload as Uint8Array), {
    longs: String,
    bytes: Buffer,
  }) as {
    cmd: number;
    body?: { hasNewMessageNotify?: { message?: Record<string, unknown> } };
  };

  // 出站请求的关键字段号（抄错就发不出去，而且不会报错）
  const sendType = root.lookupType("im_proto.SendMessageRequestBody");
  assert.equal(sendType.fields.conversationId?.id, 1);
  assert.equal(sendType.fields.conversationType?.id, 2);
  assert.equal(sendType.fields.content?.id, 4);
  assert.equal(sendType.fields.ext?.id, 5);
  assert.equal(sendType.fields.messageType?.id, 6);
  assert.equal(sendType.fields.clientMessageId?.id, 8);
  assert.equal(root.lookupType("im_proto.RequestBody").fields.sendMessageBody?.id, 100);
  assert.equal(root.lookupType("im_proto.Request").fields.cmd?.id, 1);

  // StrangerNewMessageNotify.message 必须在 field 2（抄错类型不会报错，只会永远解不出消息）
  const strangerType = root.lookupType("im_proto.StrangerNewMessageNotify");
  assert.equal(strangerType.fields.message?.id, 2, "StrangerNewMessageNotify.message 字段号应为 2");

  assert.equal(Number(response.cmd), 500);

  const message = response.body?.hasNewMessageNotify?.message;
  assert.ok(message, "解不出 has_new_message_notify.message");

  const normalized = normalizePrivateMessage(
    {
      conversationId: String(message.conversationId),
      conversationType: Number(message.conversationType),
      serverMessageId: String(message.serverMessageId),
      conversationShortId: String(message.conversationShortId),
      messageType: Number(message.messageType),
      sender: String(message.sender),
      content: String(message.content),
      createTime: String(message.createTime),
    },
    self,
  );

  assert.ok(normalized);
  assert.equal(normalized.text, "舅舅家");
  assert.equal(normalized.threadId, peer);
  assert.equal(normalized.messageId, "7709523357389440570");
});
