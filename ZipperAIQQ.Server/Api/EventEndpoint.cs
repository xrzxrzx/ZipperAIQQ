using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ZipperAIQQ.Server.Dto;

namespace ZipperAIQQ.Server.Api;

/// <summary>
/// <c>WS /events</c>：客户端连上先收 <c>hello</c>，之后按 <c>{type,seq,time,data}</c> 推事件。
/// </summary>
/// <remarks>
/// 这个端点**只推不收**（命令走 <c>POST /api</c>），但仍要读取以感知客户端关闭。
/// </remarks>
public static class EventEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseWebSockets();

        app.Map("/events", async context =>
        {
            var policy = context.RequestServices.GetRequiredService<HttpAccessPolicy>();
            var logger = context.RequestServices.GetRequiredService<ILogger>();

            if (!policy.IsAllowed(HttpAccessPolicy.ExtractBearerToken(context.Request)))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("需要 WebSocket 升级请求。", context.RequestAborted);
                return;
            }

            var broadcaster = context.RequestServices.GetRequiredService<EventBroadcaster>();
            var (id, reader) = broadcaster.Subscribe();
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

            logger.Information("事件客户端已连接（{Id}，当前 {Count} 个）", id, broadcaster.ClientCount);

            // 后台读：只为感知客户端关闭（本端点不消费客户端消息）
            _ = Task.Run(async () =>
            {
                var buffer = new byte[1024];
                try
                {
                    while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
                    {
                        var result = await socket.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }
                    }
                }
                catch
                {
                    // 断开是常态，不记错误
                }
                finally
                {
                    await linked.CancelAsync().ConfigureAwait(false);
                }
            }, CancellationToken.None);

            try
            {
                await SendAsync(socket, BuildFrame("hello", EventBroadcaster.HelloData()), linked.Token)
                    .ConfigureAwait(false);

                // 连上先给一次当前通道状态，客户端不必再发命令查
                var transport = context.RequestServices.GetRequiredService<Transport.Client.IClientTransport>();
                await SendAsync(socket, BuildFrame("qq.state", new { connected = transport.IsConnected }), linked.Token)
                    .ConfigureAwait(false);

                while (!linked.IsCancellationRequested)
                {
                    var frame = await reader.ReadAsync(linked.Token).ConfigureAwait(false);
                    await SendAsync(socket, frame, linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常断开
            }
            catch (WebSocketException ex)
            {
                logger.Debug(ex, "事件客户端 {Id} 连接异常断开", id);
            }
            finally
            {
                broadcaster.Unsubscribe(id);
                logger.Information("事件客户端已断开（{Id}，剩余 {Count} 个）", id, broadcaster.ClientCount);
            }
        });
    }

    private static string BuildFrame(string type, object? data) =>
        JsonSerializer.Serialize(
            new
            {
                type,
                seq = 0, // hello / 初始帧不占全局序号，客户端只把它当普通帧处理
                time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                data,
            },
            ApiJson.Options);

    private static async Task SendAsync(WebSocket socket, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }
}
