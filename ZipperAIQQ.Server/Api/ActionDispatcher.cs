using System.Text.Json;
using Serilog;
using ZipperAIQQ.Server.Dto;

namespace ZipperAIQQ.Server.Api;

/// <summary>把请求里的 <c>action</c> 派发到对应处理器。</summary>
/// <remarks>
/// 未知 action 统一回 <c>404</c>（而不是静默成功）——
/// 客户端拼错 action 时要能立刻发现。
/// </remarks>
public sealed class ActionDispatcher
{
    private readonly IReadOnlyDictionary<string, IApiAction> _actions;
    private readonly ILogger _logger;

    public ActionDispatcher(IEnumerable<IApiAction> actions, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(logger);

        _actions = actions.ToDictionary(a => a.Name, StringComparer.Ordinal);
        _logger = logger;
    }

    public async Task<ApiResponse> DispatchAsync(ApiRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Action))
        {
            return ApiResponse.Fail(ApiResponse.BadRequest, "缺少 action。");
        }

        if (!_actions.TryGetValue(request.Action, out var action))
        {
            return ApiResponse.Fail(ApiResponse.NotFound, $"未知 action：{request.Action}");
        }

        try
        {
            // 客户端可以省略 payload（契约样例里就是 null）。Undefined / null 统一补成空对象，
            // 否则动作里的 TryGetProperty 会抛 InvalidOperationException（最后变成 500）。
            var payload = request.Payload;
            if (payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                payload = JsonSerializer.SerializeToElement(new { });
            }
            else if (payload.ValueKind != JsonValueKind.Object)
            {
                return ApiResponse.Fail(ApiResponse.BadRequest, "payload 必须是 JSON 对象。");
            }

            var data = await action.HandleAsync(payload, ct).ConfigureAwait(false);
            return ApiResponse.Success(data);
        }
        catch (ApiException ex)
        {
            // 参数 / 前置条件问题：客户端能自己修
            return ApiResponse.Fail(ex.Code, ex.Message, ex.Payload);
        }
        catch (Exception ex)
        {
            // 回给客户端的只有简短原因：堆栈里可能有路径 / 连接串
            _logger.Error(ex, "action {Action} 执行失败", request.Action);
            return ApiResponse.Fail(ApiResponse.ServerError, "服务端处理失败，详见服务端日志。");
        }
    }
}
