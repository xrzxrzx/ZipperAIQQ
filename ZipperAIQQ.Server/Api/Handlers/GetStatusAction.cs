using System.Text.Json;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Schedules;
using ZipperAIQQ.Transport.Client;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>服务端状态：运行时长、通道连接、模型、会话数、待执行任务数。</summary>
public sealed class GetStatusAction : IApiAction
{
    private readonly IClientTransport _transport;
    private readonly IMessageStore _messages;
    private readonly IScheduleStore _schedules;
    private readonly ServerOptions _options;
    private readonly IChatClientProvider _clients;

    public GetStatusAction(
        IClientTransport transport,
        IMessageStore messages,
        IScheduleStore schedules,
        ServerOptions options,
        IChatClientProvider clients)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clients);

        _transport = transport;
        _messages = messages;
        _schedules = schedules;
        _options = options;
        _clients = clients;
    }

    public string Name => "getStatus";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var sessions = await _messages.ListSessionsAsync(500, ct).ConfigureAwait(false);
        var pending = await _schedules.CountPendingAsync(ct).ConfigureAwait(false);

        return new
        {
            // 进程运行时长，不是系统运行时长 —— Environment.TickCount64 是**系统**开机时间，
            // 之前用它导致「刚重启的服务显示已运行 25 小时」（实测踩到）。
            uptimeSeconds = (long)(DateTimeOffset.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds,
            channelConnected = _transport.IsConnected,
            model = _clients.CurrentModel ?? _options.LlmModel,
            sessions = sessions.Count,
            pendingTasks = pending,
            httpAddress = _options.HttpAddress,
        };
    }
}
