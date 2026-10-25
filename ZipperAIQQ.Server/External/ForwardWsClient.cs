using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Server.Configuration;

namespace ZipperAIQQ.Server.External;

/// <summary>
/// 正向 WS：本系统**主动连出**到外部应用（问题记录 03 #5）。
/// </summary>
/// <remarks>
/// 与反向端点共用同一套帧协议；断线后指数退避重连（5s → 10s → 30s，封顶 30s）。
/// 未配置 <c>external.forwardUrl</c> 时本服务什么都不做。
/// </remarks>
public sealed class ForwardWsClient : BackgroundService
{
    private readonly ServerOptions _options;
    private readonly ExternalWsHub _hub;
    private readonly ExternalCommandHandler _handler;
    private readonly ILogger _logger;

    public ForwardWsClient(
        ServerOptions options,
        ExternalWsHub hub,
        ExternalCommandHandler handler,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _hub = hub;
        _handler = handler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ExternalForwardUrl))
        {
            return;
        }

        if (!Uri.TryCreate(_options.ExternalForwardUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("ws" or "wss"))
        {
            _logger.Warning("external.forwardUrl 不是合法的 ws/wss 地址，正向 WS 不启动");
            return;
        }

        var delay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                if (!string.IsNullOrWhiteSpace(_options.ExternalForwardToken))
                {
                    socket.Options.SetRequestHeader(
                        "Authorization",
                        $"Bearer {_options.ExternalForwardToken}");
                }

                await socket.ConnectAsync(uri, stoppingToken).ConfigureAwait(false);
                _logger.Information("正向 WS 已连接：{Url}", uri);

                var session = _hub.Add($"out:{uri.Host}");
                try
                {
                    _hub.PublishHello(session);
                    var writer = WriteLoopAsync(socket, session, stoppingToken);
                    var reader = ReadLoopAsync(socket, session, stoppingToken);
                    await Task.WhenAny(writer, reader).ConfigureAwait(false);
                }
                finally
                {
                    _hub.Remove(session);
                }

                delay = TimeSpan.FromSeconds(5);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "正向 WS 连接失败，{Delay}s 后重试", (int)delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
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
