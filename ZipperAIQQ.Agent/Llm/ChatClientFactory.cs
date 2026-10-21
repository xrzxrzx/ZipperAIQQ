using System.ClientModel;
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
        return new ChatClientBuilder(raw)
            .UseFunctionInvocation()
            .Build();
    }
}