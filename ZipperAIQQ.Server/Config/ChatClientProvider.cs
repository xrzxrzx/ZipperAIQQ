using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Llm;
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
    private readonly ISecretProtector _protector;
    private readonly ILogger _logger;

    private volatile IChatClient? _current;
    private volatile string? _currentModel;

    public ChatClientProvider(IConfigStore store, ISecretProtector protector, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _protector = protector;
        _logger = logger;
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

        var key = _protector.Unprotect(endpoint.KeyCipher);
        if (string.IsNullOrEmpty(key))
        {
            _logger.Error("端点 {Name} 的 key 解不开（密钥环被换过？），保留现有 LLM 客户端", endpoint.Name);
            return;
        }

        IChatClient client;
        try
        {
            client = ChatClientFactory.Create(
                new LlmEndpoint(endpoint.Name, endpoint.BaseUrl, endpoint.Model, (LlmApiKind)endpoint.ApiKind),
                key);
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
