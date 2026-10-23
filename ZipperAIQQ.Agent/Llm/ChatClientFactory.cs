using System.ClientModel;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using OpenAI;

namespace ZipperAIQQ.Agent.Llm;

/// <summary>端点的 API 形态。**必须显式配置**：「都是 OpenAI 兼容」≠「调法一样」。</summary>
public enum LlmApiKind
{
    /// <summary>OpenAI Responses API（DeepSeek）。</summary>
    Responses,

    /// <summary>OpenAI Chat Completions（OpenRouter 等）。</summary>
    ChatCompletions,

    /// <summary>
    /// OpenRouter **Decisions API**（Jev 这类「判断模型」用的，不是 chat 模型）。
    /// </summary>
    /// <remarks>
    /// 端点 <c>POST {origin}/api/alpha/decisions</c>，返回的是**带概率的类型化答案**（noul/choice/score），
    /// 不是生成的文本。实测 <c>typesafe/jev-1.13</c> 用它自己的话拒绝 chat/completions：
    /// "is a decisions model and cannot be used with the chat/completions endpoint"。
    /// </remarks>
    Decisions,
}

/// <summary>一个具名模型端点。**代码里不写死任何 URL 或模型名**（FR-8.13）。</summary>
public sealed record LlmEndpoint(string Name, string BaseUrl, string Model, LlmApiKind ApiKind);

/// <summary>按端点配置创建 <see cref="IChatClient"/>。</summary>
/// <remarks>
/// ⚠️ 不使用 previous_response_id：上下文由我们自己管理，每轮全量传（需求 D6）。
/// </remarks>
public static class ChatClientFactory
{
    public static IChatClient Create(LlmEndpoint endpoint, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException($"端点 {endpoint.Name} 的 BaseUrl 不合法：{endpoint.BaseUrl}");
        }

        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = baseUri });

        var raw = endpoint.ApiKind switch
        {
            LlmApiKind.Responses => client.GetResponsesClient().AsIChatClient(endpoint.Model),
            LlmApiKind.ChatCompletions => client.GetChatClient(endpoint.Model).AsIChatClient(),
            _ => throw new NotSupportedException($"不支持的 ApiKind：{endpoint.ApiKind}"),
        };

        // ⭐ 工具循环：没有这一层，模型调用工具后不会自动执行，只会把调用意图当文本吐出来。
        var invoking = new FunctionInvokingChatClient(raw);

        // 工具调用**必须留日志**：出问题时（"好友列表查不到""计划任务没建"）
        // 没有这条线就只能靠翻聊天记录猜。实测吃过这个亏。
        var inner = invoking.FunctionInvoker ?? ((ctx, token) => DefaultInvoker(ctx, token));
        invoking.FunctionInvoker = async (context, ct) =>
        {
            var name = context.Function.Name;
            var args = SafeArgs(context.Arguments);
            var sw = Stopwatch.StartNew();

            try
            {
                var result = await inner(context, ct).ConfigureAwait(false);
                sw.Stop();

                var summary = SafeResult(result);
                Serilog.Log.Information(
                    "工具 {Tool} 完成（{Ms}ms）参数={Args} 结果={Result}",
                    name, sw.ElapsedMilliseconds, args, summary);

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Serilog.Log.Error(ex,
                    "工具 {Tool} 抛异常（{Ms}ms）参数={Args}",
                    name, sw.ElapsedMilliseconds, args);
                throw;
            }
        };

        return invoking;
    }

    /// <summary>参数摘要（截断，避免长文本/密钥进日志）。</summary>
    private static string SafeArgs(IEnumerable<KeyValuePair<string, object?>>? args)
    {
        if (args is null)
        {
            return "{}";
        }

        var parts = args.Select(kv =>
        {
            var v = kv.Value?.ToString() ?? "null";
            if (v.Length > 60)
            {
                v = v[..60] + "…";
            }

            return $"{kv.Key}={v}";
        });

        var joined = string.Join(", ", parts);
        return joined.Length > 200 ? joined[..200] + "…" : joined;
    }

    /// <summary>结果摘要（只记类型与长度，不记正文）。</summary>
    private static string SafeResult(object? result)
    {
        if (result is null)
        {
            return "null";
        }

        var text = result.ToString() ?? string.Empty;
        return text.Length <= 80 ? text : text[..80] + $"…（共 {text.Length} 字）";
    }

    /// <summary>理论上的兜底：MEAI 默认已设置 FunctionInvoker，这里只防 NRE。</summary>
    private static ValueTask<object?> DefaultInvoker(FunctionInvocationContext context, CancellationToken ct)
        => context.Function.InvokeAsync(new AIFunctionArguments(context.Arguments), ct);
}
