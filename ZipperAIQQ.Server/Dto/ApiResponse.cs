namespace ZipperAIQQ.Server.Dto;

/// <summary>
/// HTTP 命令响应：<c>{ code, message, data }</c>（与 <c>docs/05-契约/command-*.json</c> 一致）。
/// </summary>
public sealed record ApiResponse(int Code, string Message, object? Data)
{
    public const int Ok = 0;
    public const int BadRequest = 400;
    public const int Unauthorized = 401;
    public const int NotFound = 404;

    /// <summary>冲突：需要客户端二次确认后再提交。</summary>
    public const int Conflict = 409;

    public const int ServerError = 500;

    public static ApiResponse Success(object? data) => new(Ok, "ok", data);

    public static ApiResponse Fail(int code, string message, object? data = null) =>
        new(code, message, data);
}
