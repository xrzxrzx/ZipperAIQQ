using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ZipperAIQQ.Server.External;

/// <summary>
/// 正向 WS：本系统**主动连出**到外部应用（问题记录 03 #5）。
/// </summary>
/// <remarks>
/// 正向也是**多个配置块**：每个 url 一条独立连接，各自重连；
/// 配置保存后所有连接立即断开、按新列表重建（不用重启服务）。
/// </remarks>
public sealed class ForwardWsClient : BackgroundService
{
    private readonly ExternalOptionsService _external;
    private readonly ExternalWsHub _hub;
    private readonly ExternalCommandHandler _handler;
    private readonly ILogger _logger;

    public ForwardWsClient(
        ExternalOptionsService external,
        ExternalWsHub hub,
        ExternalCommandHandler handler,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(external);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(logger);

        _external = external;
        _hub = hub;
        _handler = handler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var targets = _external.Current.ForwardServers
                .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Url))
                .DistinctBy(t => t.Url, StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var changed = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            void OnChanged() => changed.Cancel();
            _external.Changed += OnChanged;

            try
            {
                var tasks = targets
                    .Select(t => Task.Run(() => RunTargetAsync(t, changed.Token), changed.Token))
                    .ToArray();

                if (tasks.Length > 0)
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), changed.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 停止或配置变更
            }
            finally
            {
                _external.Changed -= OnChanged;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunTargetAsync(ExternalForwardServer target, CancellationToken ct)
    {
        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
        {
            _logger.Warning("正向 WS 地址不合法，跳过：{Url}", target.Url);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                if (!string.IsNullOrWhiteSpace(target.Token))
                {
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {target.Token}");
                }

                await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
                _logger.Information("正向 WS 已连接：{Url}", uri);

                var session = _hub.Add($"out:{uri.Host}:{uri.Port}");
                try
                {
                    _hub.PublishHello(session);
                    var writer = WriteLoopAsync(socket, session, ct);
                    var reader = ReadLoopAsync(socket, session, ct);
                    await Task.WhenAny(writer, reader).ConfigureAwait(false);
                }
                finally
                {
                    _hub.Remove(session);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "正向 WS 连接失败（{Url}），5 秒后重试", target.Url);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task WriteLoopAsync(
        ClientWebSocket socket, ExternalSession session, CancellationToken ct)
    {
        await foreach (var frame in session.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var bytes = Encoding.UTF8.GetBytes(frame);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(
        ClientWebSocket socket, ExternalSession session, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        var builder = new StringBuilder();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            // 与反向端点同口径：单帧上限 1MB，超了就断开重连，避免恶意/异常对端把内存打爆
            if (builder.Length > WsChannelEndpoint.MaxFrameBytes)
            {
                _logger.Warning(
                    "正向 WS 单帧超过 {Max} 字节，断开连接等待重连",
                    WsChannelEndpoint.MaxFrameBytes);
                return;
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            var json = builder.ToString();
            builder.Clear();
            await _handler.HandleAsync(session, json, ct).ConfigureAwait(false);
        }
    }
}
