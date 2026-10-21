using Serilog;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace ZipperAIQQ.Transport.Server;

/// <summary>
/// 仅支持单客户端
/// </summary>
public class ServerTransport : WebSocketDuplexBase, IServerTransport
{
    //Func<JsonNode, JsonNode>：接收一个JsonNode类型（接收到的原始JSON）和返回JsonNode类型（待响应给客户端的原始JSON）的Handler
    ConcurrentDictionary<string, Func<JsonNode, Task<JsonNode>>> _apiHandlers;

    public Func<JsonNode, string?>? GetAPIAction;//获取不到时返回空
    public Func<JsonNode, JsonNode>? GetUnknownActionResponse;//获取未知API时的响应消息

    public ServerTransport(ILogger logger) : base(logger)
    {
        _apiHandlers = new ConcurrentDictionary<string, Func<JsonNode, Task<JsonNode>>>();
    }

    public void SetAPIActionGetter(Func<JsonNode, string?> getter)
    {
        GetAPIAction = getter;
    }

    public void SetUnknownActionResponseGetter(Func<JsonNode, JsonNode> getter)
    {
        GetUnknownActionResponse = getter;
    }

    public void RegisterApiHandler(string apiName, Func<JsonNode, Task<JsonNode>> handler)
    {
        if (_isDisposed)
            return;

        _apiHandlers[apiName] = handler;
    }

    public void UnregisterApiHandler(string apiName)
    {
        if (_isDisposed)
            return;

        _apiHandlers.TryRemove(apiName, out _);
    }

    /// <summary>
    /// 收到的消息只会是API请求
    /// </summary>
    /// <param name="message"></param>
    protected override async ValueTask OnMessageAsync(JsonNode json)
    {
        if (json == null)
        {
            _logger.Warning("无法解析消息 {Message}", json);  
            return;
        }

        var action = GetAPIAction!.Invoke(json);
        if (action == null)
        {
            _logger.Warning("收到非API请求消息 {Message}", json);
            return;
        }

        Func<JsonNode, Task<JsonNode>>? handler;
        _apiHandlers.TryGetValue(action, out handler);
        if(handler == null)
        {
            _logger.Warning("API {action} 未注册", action);

            var errorResponse = GetUnknownActionResponse?.Invoke(json);
            if(errorResponse == null)
            {
                _logger.Error("API {action} 未注册，且未设置未知API响应消息", action);
                return;
            }

            await SendAsync(errorResponse);

            return;
        }

        await HandleAPI(json, handler);
    }

    private async Task HandleAPI(JsonNode json, Func<JsonNode, Task<JsonNode>> handler)
    {
        var result = await handler.Invoke(json);
        await SendAsync(result);
    }
}