using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Channels;
using ZipperAIQQ.Server.Configuration;

namespace ZipperAIQQ.Server.External;

/// <summary>
/// <c>WS /ws/channel</c>：**通道接入**端点（sidecar → Server）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>/ws/ext</c> 的分工（决策二）：那个是「外部应用看/代发 QQ 消息」的**旁路**；
/// 这个是「平台 sidecar 把自己的收发接进来」的**通道语义**。两者不复用端点、不复用语义，
/// 只复用帧外形（<c>{type,time,data}</c>）。
/// </para>
/// <para>
/// 鉴权：loopback 免验（<see cref="HttpAccessPolicy"/>）；非 loopback 或配了
/// <see cref="DouyinOptions.Token"/> 时要求 Bearer token。**token 不落日志、不回显**。
/// </para>
/// <para>
/// 连接生命周期：sidecar 主动连入 → 包装成 <see cref="IDouyinConnection"/> → 交给
/// <see cref="DouyinChannel.AttachAsync"/>（由通道负责收帧与重连替换）→ 端点只等连接结束。
/// </para>
/// </remarks>
public static class WsChannelEndpoint
{
    /// <summary>单帧上限（1 MB）：抖音图片/长文本够用，同时挡住恶意的超大帧。</summary>
    /// <remarks>internal 而非 private：同文件的连接包装类也要用它（私有常量跨类不可见）。</remarks>
    internal const int MaxFrameBytes = 1024 * 1024;

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseWebSockets();

        app.Map("/ws/channel", async context =>
        {
            var services = context.RequestServices;
            // 配置走 DouyinOptionsService（S3-F）：客户端改了 enabled/token 后，下一次连接就按新值判
            var douyin = services.GetRequiredService<DouyinOptionsService>().Current;
            var logger = services.GetRequiredService<ILogger>();

            if (!douyin.Enabled)
            {
                // 没启用就当不存在：不泄露"这里有个端点"
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var policy = services.GetRequiredService<HttpAccessPolicy>();
            var token = ExtractToken(context.Request);
            var configured = douyin.Token;
            var tokenMatches = !string.IsNullOrWhiteSpace(configured)
                && string.Equals(configured, token, StringComparison.Ordinal);

            if (!policy.IsAllowed(token) && !tokenMatches)
            {
                logger.Warning("拒绝 /ws/channel 连接：token 不匹配（remote={Remote}）",
                    context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var channel = services.GetRequiredService<DouyinChannel>();

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var connection = new WebSocketDouyinConnection(socket, logger);

            logger.Information("sidecar 已连入 /ws/channel（remote={Remote}）", context.Connection.RemoteIpAddress);

            try
            {
                await channel.AttachAsync(connection, context.RequestAborted).ConfigureAwait(false);

                // 端点只负责"保持这次连接活着"：真正的收帧在通道内部。
                await Task.Delay(Timeout.Infinite, context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // sidecar 主动断开 / 服务停止：正常
            }
            catch (Exception ex)
            {
                logger.Error(ex, "sidecar 连接异常结束");
            }
            finally
            {
                // 关闭路径不跟随已取消的 token
                await connection.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                logger.Information("sidecar 已断开 /ws/channel");
            }
        });
    }

    /// <summary>从 <c>Authorization: Bearer</c> 或 <c>?token=</c> 取 token（sidecar 两种都常用）。</summary>
    private static string? ExtractToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(header))
        {
            const string prefix = "Bearer ";
            return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? header[prefix.Length..].Trim()
                : header.Trim();
        }

        var query = request.Query["token"].ToString();
        return string.IsNullOrWhiteSpace(query) ? null : query.Trim();
    }
}

/// <summary>
/// 把一条 <see cref="WebSocket"/> 包成 <see cref="IDouyinConnection"/>。
/// </summary>
/// <remarks>
/// 只做"文本帧的收与发"，不解释帧语义（协议在 <see cref="DouyinChannel"/> 里）——
/// 这样协议逻辑可以脱离网络单测。
/// </remarks>
internal sealed class WebSocketDouyinConnection : IDouyinConnection
{
    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>收到的帧先进通道：端点与通道两侧的读写节奏不必对齐。</summary>
    private readonly Channel<string> _inbound = System.Threading.Channels.Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly Task _pump;

    public WebSocketDouyinConnection(WebSocket socket, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(logger);

        _socket = socket;
        _logger = logger;
        _pump = Task.Run(PumpAsync);
    }

    public async Task SendAsync(string json, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket
                .SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public IAsyncEnumerable<string> ReceiveAsync(CancellationToken ct) =>
        _inbound.Reader.ReadAllAsync(ct);

    public async Task CloseAsync(CancellationToken ct)
    {
        if (_socket.State is WebSocketState.Closed or WebSocketState.Aborted)
        {
            return;
        }

        try
        {
            await _socket
                .CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 已断开的连接再关会抛 —— 关闭路径不该反过来炸调用方
            _logger.Debug(ex, "关闭 sidecar 连接时出错（忽略）");
        }
        finally
        {
            _inbound.Writer.TryComplete();
        }
    }

    /// <summary>读循环：把文本帧推进通道；连接关闭/出错就结束通道。</summary>
    private async Task PumpAsync()
    {
        var buffer = new byte[WsChannelEndpoint.MaxFrameBytes];

        try
        {
            while (!_socket.CloseStatus.HasValue)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket
                        .ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _inbound.Writer.TryComplete();
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > WsChannelEndpoint.MaxFrameBytes)
                    {
                        _logger.Warning("sidecar 单帧超过 {Max} 字节，已丢弃该连接", WsChannelEndpoint.MaxFrameBytes);
                        await _socket
                            .CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too big", CancellationToken.None)
                            .ConfigureAwait(false);
                        _inbound.Writer.TryComplete();
                        return;
                    }
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    await _inbound.Writer.WriteAsync(json).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "sidecar 连接读循环结束");
        }
        finally
        {
            _inbound.Writer.TryComplete();
        }
    }
}
