using System.Text.Json;

namespace ZipperAIQQ.Server.Dto;

/// <summary>HTTP 命令请求：<c>{ action, payload }</c>（架构 §3.5）。</summary>
/// <remarks>
/// <paramref name="Payload"/> 用 <see cref="JsonElement"/> 而不是强类型 ——
/// 每个 action 自己解析，避免为 5 个 action 造 5 个请求 DTO。
/// </remarks>
public sealed record ApiRequest(string? Action, JsonElement Payload);
