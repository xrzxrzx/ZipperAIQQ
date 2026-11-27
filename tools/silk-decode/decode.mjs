#!/usr/bin/env node
// QQ 语音（SILK_V3）→ 16 kHz 单声道 PCM WAV
//
// 为什么需要它（2026-11-28 实测）：QQ 的语音文件叫「.amr」，头部其实是 `#!SILK_V3`；
//   - NapCat 的 fetch_ptt_text 在这台机器上拿不到文本（QQ 侧 ASR 卡死）；
//   - NapCat 的 get_record 要按文件名去 QQ 的消息索引里搜，而**语音刚到达的 1~2 分钟内查不到**
//     （实测：刚到 5 秒超时，两分钟后同一请求 30 ms 返回）；
//   - 机器上的 ffmpeg.exe 是坏的（not a valid application for this OS platform）。
// 所以唯一"消息一到达就能用"的原料是 OneBot 给的本地 silk 文件 —— 自己解。
// silk-wasm 是纯 JS/WASM（283 KB，MIT），实测 1.74 s 音频解码 38 ms。
//
// 用法：node decode.mjs <silkPath> <outWavPath> [sampleRate]
// 成功时最后一行输出 JSON：{"ok":true,"durationMs":1740,"pcmBytes":54400}
// 失败时退出码非 0，并输出 {"ok":false,"error":"..."}

import { readFileSync, writeFileSync } from "node:fs";
import { decode } from "./vendor/silk-wasm/lib/index.mjs";

const [, , silkPath, outPath, sampleRateArg] = process.argv;

if (!silkPath || !outPath) {
  console.log(JSON.stringify({ ok: false, error: "用法: node decode.mjs <silkPath> <outWavPath> [sampleRate]" }));
  process.exit(2);
}

const sampleRate = Number.parseInt(sampleRateArg ?? "16000", 10) || 16000;

try {
  const silk = readFileSync(silkPath);

  // 已经是 WAV 就直接说一声，别白解（调用方可能传错）
  if (silk.length >= 12 && silk.toString("ascii", 0, 4) === "RIFF" && silk.toString("ascii", 8, 12) === "WAVE") {
    writeFileSync(outPath, silk);
    console.log(JSON.stringify({ ok: true, alreadyWav: true, pcmBytes: silk.length - 44, durationMs: 0 }));
    process.exit(0);
  }

  const started = Date.now();
  const result = await decode(new Uint8Array(silk), sampleRate);
  const pcm = Buffer.from(result.data);

  if (pcm.length === 0) {
    console.log(JSON.stringify({ ok: false, error: "解码结果为空" }));
    process.exit(1);
  }

  // 封 WAV 头（16-bit mono PCM）—— ASR 那边只认能直接解码的音频
  const header = Buffer.alloc(44);
  header.write("RIFF", 0);
  header.writeUInt32LE(36 + pcm.length, 4);
  header.write("WAVE", 8);
  header.write("fmt ", 12);
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20);                       // PCM
  header.writeUInt16LE(1, 22);                       // mono
  header.writeUInt32LE(sampleRate, 24);
  header.writeUInt32LE(sampleRate * 2, 28);          // byte rate
  header.writeUInt16LE(2, 32);                       // block align
  header.writeUInt16LE(16, 34);                      // bits
  header.write("data", 36);
  header.writeUInt32LE(pcm.length, 40);

  writeFileSync(outPath, Buffer.concat([header, pcm]));

  console.log(JSON.stringify({
    ok: true,
    durationMs: result.duration ?? 0,
    pcmBytes: pcm.length,
    decodeMs: Date.now() - started,
  }));
} catch (error) {
  console.log(JSON.stringify({ ok: false, error: String(error?.message ?? error) }));
  process.exit(1);
}
