using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Server.Alerts;
using ZipperAIQQ.Server.Billing;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Server.Config;

/// <summary>
/// 按库里的运行配置创建 LLM 客户端，并支持热替换（FR-8.4）。
/// </summary>
/// <remarks>
/// <para>
/// 每次 <see cref="ReloadAsync"/> 都重新读库 → 解密 → 建客户端 → 原子替换。
/// **任何一步失败都保留旧客户端** —— 一次配置写错不该让对话能力直接消失。
/// </para>
/// </remarks>
public sealed class ChatClientProvider : IChatClientProvider
{
    private readonly IConfigStore _store;
    private readonly ZipperAIQQ.Server.Ai.IDeepSeekKeyProvider _keys;
    private readonly ILogger _logger;
    private readonly ISpendReader _spend;
    private readonly IQuotaOptionsProvider _quota;
    private readonly IAlertSink? _alerts;

    private volatile IChatClient? _current;
    private volatile string? _currentModel;

    public ChatClientProvider(
        IConfigStore store,
        ZipperAIQQ.Server.Ai.IDeepSeekKeyProvider keys,
        ILogger logger,
        ISpendReader spend,
        IQuotaOptionsProvider quota,
        IAlertSink? alerts = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(spend);
        ArgumentNullException.ThrowIfNull(quota);

        _store = store;
        _keys = keys;
        _logger = logger;
        _spend = spend;
        _quota = quota;
        _alerts = alerts;
    }

    public bool HasClient => _current is not null;

    public IChatClient Current => _current
        ?? throw new InvalidOperationException("LLM 客户端尚未初始化：请先配置 Chat 角色端点。");

    /// <summary>当前生效的模型名（状态页用运行时真值，不是启动配置）。</summary>
    public string? CurrentModel => _currentModel;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var roles = await _store.ListRolesAsync(ct).ConfigureAwait(false);
        var chatRole = roles.FirstOrDefault(r => r.Role.Equals("Chat", StringComparison.OrdinalIgnoreCase));
        if (chatRole is null)
        {
            _logger.Warning("运行配置里没有 Chat 角色端点，保留现有 LLM 客户端");
            return;
        }

        var endpoint = await _store.GetEndpointAsync(chatRole.EndpointName, ct).ConfigureAwait(false);
        if (endpoint is null)
        {
            _logger.Warning("Chat 角色指向的端点 {Name} 不存在，保留现有 LLM 客户端", chatRole.EndpointName);
            return;
        }

        // 密钥收敛（问题记录 06 §5）：key 从**全局 provider** 取，不再看端点自己的密文。
        // 端点表里的旧密文只是迁移来源与留档，读完就不参与解析。
        var key = await _keys.GetAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key))
        {
            _logger.Error("没有可用的 API key（槽位表里没有能解开的 key），保留现有 LLM 客户端");
            return;
        }

        IChatClient client;
        try
        {
            client = ChatClientFactory.Create(
                new LlmEndpoint(endpoint.Name, endpoint.BaseUrl, endpoint.Model, (LlmApiKind)endpoint.ApiKind),
                key);

            // 闸门包在最外层：工具循环里的每次调用都会经过它（额度到线时拦住非关键调用）
            client = new SpendGuardChatClient(client, _spend, _quota, _logger, _alerts);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建 LLM 客户端失败（端点 {Name}），保留现有客户端", endpoint.Name);
            return;
        }

        var previous = _current;
        _current = client;
        _currentModel = endpoint.Model;

        if (previous is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _logger.Information("LLM 客户端已就绪：端点={Name} 模型={Model}", endpoint.Name, endpoint.Model);
    }
}
