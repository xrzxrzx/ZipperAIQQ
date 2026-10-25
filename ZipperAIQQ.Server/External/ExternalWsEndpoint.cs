using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ZipperAIQQ.Server.Api;

namespace ZipperAIQQ.Server.External;

/// <summary>
/// <c>WS /ws/ext</c>：外部应用**连入**（反向 WS）。
/// </summary>
/// <remarks>
/// 鉴权复用 <see cref="HttpAccessPolicy"/>（Bearer token，loopback 免验）。
/// 连接建立后先收 <c>hello</c>，之后按订阅推送 <c>event</c>；客户端可发 <c>send</c>。
/// </remarks>
public static class ExternalWsEndpoint
{
    private const int MaxFrameBytes = 256 * 1024;

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseWebSockets();

        app.Map("/ws/ext", async context =>
        {
            var policy = context.RequestServices.GetRequiredService<HttpAccessPolicy>();
            var logger = context.RequestServices.GetRequiredService<ILogger>();
            var external = context.RequestServices.GetRequiredService<ExternalOptionsService>();

            var token = ExtractToken(context.Request);
            var block = external.Current.ReverseServers
                .FirstOrDefault(s => s.Port == context.Connection.LocalPort);
            var tokenAllowed =
                (!string.IsNullOrWhiteSpace(block?.Token) && string.Equals(block.Token, token, StringComparison.Ordinal));

            if (!policy.IsAllowed(token) && !tokenAllowed)
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

            var hub = context.RequestServices.GetRequiredService<ExternalWsHub>();
            var handler = context.RequestServices.GetRequiredService<ExternalCommandHandler>();

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var session = hub.Add($"in:{context.Connection.RemoteIpAddress}");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

            try
            {
                hub.PublishHello(session);

                var writer = WriteLoopAsync(socket, session, linked.Token);
                var reader = ReadLoopAsync(socket, session, handler, linked.Token);

                await Task.WhenAny(writer, reader).ConfigureAwait(false);
                await linked.CancelAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常断开
            }
            catch (WebSocketException ex)
            {
                logger.Debug(ex, "外部 WS 连接异常断开");
            }
            finally
            {
                hub.Remove(session);
            }
        });
    }

    private static async Task WriteLoopAsync(
        WebSocket socket, ExternalSession session, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in session.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(frame);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private static async Task ReadLoopAsync(
        WebSocket socket,
        ExternalSession session,
        ExternalCommandHandler handler,
        CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        var builder = new StringBuilder();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (builder.Length + result.Count > MaxFrameBytes)
            {
                session.Enqueue(ExternalProtocol.Ack(null, false, "帧太大。"));
                builder.Clear();
                continue;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            var json = builder.ToString();
            builder.Clear();

            try
            {
                await handler.HandleAsync(session, json, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                session.Enqueue(ExternalProtocol.Ack(null, false, ex.Message));
            }
        }
    }

    private static string? ExtractToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }
}
