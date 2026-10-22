using System.Text.Json;

namespace ZipperAIQQ.Server.Dto;

/// <summary>契约的统一 JSON 选项。</summary>
/// <remarks>
/// <para>Web 默认（camelCase），与 <c>docs/05-契约</c> 样例一致。</para>
/// <para>
/// <b>刻意关掉大小写不敏感</b>：字段名写错要马上暴露（契约漂移），
/// 宽松匹配只会把它藏到"运行时看起来没事"。
/// </para>
/// </remarks>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };
}
