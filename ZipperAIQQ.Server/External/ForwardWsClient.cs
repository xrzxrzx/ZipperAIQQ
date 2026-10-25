using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ZipperAIQQ.Server.External;

/// <summary>
/// 正向 WS：本系统**主动连出**到外部应用（问题记录 03 #5）。
/// </summary>
/// <remarks>
/// 与反向端点共用同一套帧协议；配置在客户端保存后立即断开重连（不用重启服务）。
/// 未配置 <c>external.forwardUrl</c> 时每 3 秒看一次配置。
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
            var url = _external.Current.ForwardUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
            {
                _logger.Warning("external.forwardUrl 不是合法的 ws/wss 地址，正向 WS 暂停");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                continue;
            }

            using var changed = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            void OnChanged() => changed.Cancel();
            _external.Changed += OnChanged;
            try
            {
                using var socket = new ClientWebSocket();
                var token = await _external.GetForwardTokenAsync(stoppingToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                }

                await socket.ConnectAsync(uri, changed.Token).ConfigureAwait(false);
                _logger.Information("正向 WS 已连接：{Url}", uri);

                var session = _hub.Add($"out:{uri.Host}");
                try
                {
                    _hub.PublishHello(session);
                    var writer = WriteLoopAsync(socket, session, changed.Token);
                    var reader = ReadLoopAsync(socket, session, changed.Token);
                    await Task.WhenAny(writer, reader).ConfigureAwait(false);
                }
                finally
                {
                    _hub.Remove(session);
                }
            }
            catch (OperationCanceledException)
            {
                // 停止或配置变更：回到循环重新读取配置
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "正向 WS 连接失败，5 秒后重试");
            }
            finally
            {
                _external.Changed -= OnChanged;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
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
