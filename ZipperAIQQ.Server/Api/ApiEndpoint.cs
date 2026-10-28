using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ZipperAIQQ.Server.Dto;

namespace ZipperAIQQ.Server.Api;

/// <summary>
/// <c>POST /api</c>：单端点命令通道（架构 §3.5）。
/// </summary>
/// <remarks>
/// 不用 REST 的理由见架构方案：客户端只需 <c>{action, payload}</c> 一种形状，
/// 加命令只动 dispatcher，不动路由。
/// </remarks>
public static class ApiEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api", async (HttpContext context) =>
        {
            var policy = context.RequestServices.GetRequiredService<HttpAccessPolicy>();
            var dispatcher = context.RequestServices.GetRequiredService<ActionDispatcher>();

            if (!policy.IsAllowed(HttpAccessPolicy.ExtractBearerToken(context.Request)))
            {
                await WriteAsync(context, StatusCodes.Status401Unauthorized,
                    ApiResponse.Fail(ApiResponse.Unauthorized, "未授权：缺少或错误的 token。")).ConfigureAwait(false);
                return;
            }

            ApiRequest? request;
            try
            {
                request = await context.Request
                    .ReadFromJsonAsync<ApiRequest>(ApiJson.Options, context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                await WriteAsync(context, StatusCodes.Status400BadRequest,
                    ApiResponse.Fail(ApiResponse.BadRequest, "请求体不是合法 JSON。")).ConfigureAwait(false);
                return;
            }

            if (request is null)
            {
                await WriteAsync(context, StatusCodes.Status400BadRequest,
                    ApiResponse.Fail(ApiResponse.BadRequest, "请求体为空。")).ConfigureAwait(false);
                return;
            }

            var response = await dispatcher.DispatchAsync(request, context.RequestAborted).ConfigureAwait(false);

            // HTTP 状态码跟随业务码：客户端用状态码分支比解析 body 更直接
            var status = response.Code switch
            {
                ApiResponse.Ok => StatusCodes.Status200OK,
                ApiResponse.BadRequest => StatusCodes.Status400BadRequest,
                ApiResponse.Unauthorized => StatusCodes.Status401Unauthorized,
                ApiResponse.NotFound => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status500InternalServerError,
            };

            await WriteAsync(context, status, response).ConfigureAwait(false);
        });
    }

    private static async Task WriteAsync(HttpContext context, int status, ApiResponse response)
    {
        context.Response.StatusCode = status;
        await context.Response
            .WriteAsJsonAsync(response, ApiJson.Options, context.RequestAborted)
            .ConfigureAwait(false);
    }
}
