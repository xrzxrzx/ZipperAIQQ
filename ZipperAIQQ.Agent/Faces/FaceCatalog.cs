using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZipperAIQQ.Agent.Faces;

/// <summary>一个可用表情。</summary>
public sealed record FaceEntry
{
    /// <summary>system = 平台系统表情（face id）；custom = 自定义表情（图片）。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "system";

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("tags")]
    public string[] Tags { get; init; } = [];

    /// <summary>自定义表情的图片来源（URL 或本地路径）。</summary>
    [JsonPropertyName("file")]
    public string? File { get; init; }

    /// <summary>常用标记：无关键词查询时优先返回。</summary>
    [JsonPropertyName("common")]
    public bool Common { get; init; }
}

/// <summary>
/// 表情目录：从配置文件加载，供 AI **按需查询**。
/// </summary>
/// <remarks>
/// <para>
/// **为什么不把表情表塞进提示词**：100+ 个表情的光清单就要几百 token，而且大部分用不上。
/// 按需查询（list_faces）只把"这次可能用到的几个"放进上下文。
/// </para>
/// <para>
/// **为什么放配置文件而不是代码**：表情是**平台相关**的。
/// 以后接别的平台，换一份配置即可，不用改代码。
/// </para>
/// </remarks>
public sealed class FaceCatalog
{
    private readonly List<FaceEntry> _all;
    private readonly Dictionary<string, FaceEntry> _byName;
    private readonly Dictionary<string, FaceEntry> _byId;

    private FaceCatalog(List<FaceEntry> all)
    {
        _all = all;
        _byName = new Dictionary<string, FaceEntry>(StringComparer.OrdinalIgnoreCase);
        _byId = new Dictionary<string, FaceEntry>(StringComparer.Ordinal);
        foreach (var f in all)
        {
            if (!string.IsNullOrWhiteSpace(f.Name))
            {
                _byName[f.Name] = f;
            }

            if (f.Kind == "system" && !string.IsNullOrEmpty(f.Id))
            {
                _byId[f.Id] = f;
            }
        }
    }

    public static FaceCatalog Empty { get; } = new([]);

    public int Count => _all.Count;

    public static FaceCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return Empty;
        }

        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<FaceConfigFile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        return doc?.Faces is { Count: > 0 } faces ? new FaceCatalog(faces) : Empty;
    }

    /// <summary>按 face id 查（把收到的表情渲染成名称时走这里）。</summary>
    public FaceEntry? GetById(string id) =>
        _byId.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>按名称查（AI 用 <c>[表情:名称]</c> 发送时走这里）。</summary>
    public FaceEntry? GetByName(string name) =>
        _byName.TryGetValue(name.Trim(), out var entry) ? entry : null;

    /// <summary>
    /// 查询表情。无关键词时返回常用项；有关键词时按名称/标签模糊匹配。
    /// </summary>
    public IReadOnlyList<FaceEntry> Search(string? keyword, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            var commons = _all.Where(f => f.Common).Take(limit).ToList();
            return commons.Count > 0 ? commons : _all.Take(limit).ToList();
        }

        var k = keyword.Trim();
        return _all
            .Where(f => f.Name.Contains(k, StringComparison.OrdinalIgnoreCase)
                     || f.Tags.Any(t => t.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .ToList();
    }

    private sealed class FaceConfigFile
    {
        [JsonPropertyName("faces")]
        public List<FaceEntry>? Faces { get; init; }
    }
}