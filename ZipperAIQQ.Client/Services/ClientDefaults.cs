namespace ZipperAIQQ.Client.Services;

/// <summary>客户端默认值（服务端地址 / LLM 端点 / 抖音 sidecar 端点）。</summary>
/// <remarks>
/// 以前这些字面量散在多个 ViewModel / 服务里，改默认端口要全文搜。
/// 与契约相关的字段名仍留在 DTO，这里只放"人类可改的默认值"。
/// </remarks>
public static class ClientDefaults
{
    public const string ServerUrl = "http://127.0.0.1:5180";

    public const string LlmBaseUrl = "https://api.deepseek.com";

    public const string DouyinEndpoint = "/ws/channel";

    public const string ReverseHost = "0.0.0.0";

    public const int ReversePort = 5181;
}
