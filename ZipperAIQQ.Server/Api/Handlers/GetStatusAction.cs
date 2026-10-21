using System.Text.Json;
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

    public GetStatusAction(
        IClientTransport transport,
        IMessageStore messages,
        IScheduleStore schedules,
        ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(options);

        _transport = transport;
        _messages = messages;
        _schedules = schedules;
        _options = options;
    }

    public string Name => "getStatus";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var sessions = await _messages.ListSessionsAsync(500, ct).ConfigureAwait(false);
        var pending = await _schedules.CountPendingAsync(ct).ConfigureAwait(false);

        return new
        {
            uptimeSeconds = Environment.TickCount64 / 1000,
            channelConnected = _transport.IsConnected,
            model = _options.LlmModel,
            sessions = sessions.Count,
            pendingTasks = pending,
            httpAddress = _options.HttpAddress,
        };
    }
}
