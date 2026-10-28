using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZipperAIQQ.Client.Dto;

/// <summary>按平台读取（<c>getAccess</c> / <c>getOwner</c>）：S3-C 起带平台，省略 = qq。</summary>
public sealed class PlatformQueryDto
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "qq";
}

/// <summary>设置主人（<c>setOwner</c>）：S3-C 起按平台分别设置，互不覆盖。</summary>
public sealed class SetOwnerRequestDto
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "qq";

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = string.Empty;
}

/// <summary>人员与名单（<c>getAccess</c>）。</summary>
public sealed class AccessDto
{
    /// <summary>
    /// 本次响应对应的平台（S3-C 回显）。**旧服务端不返回 → 空串**，
    /// 客户端按"请求时带的平台"处理，别拿默认值冒充回显。
    /// </summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    /// <summary>
    /// 可切换的平台列表（如 <c>["qq"]</c>）。**旧服务端不返回 → 空列表**，
    /// 客户端退化成只有一个默认平台，不报错。
    /// </summary>
    [JsonPropertyName("platforms")]
    public List<string> Platforms { get; set; } = [];

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; set; }

    [JsonPropertyName("admins")]
    public List<string> Admins { get; set; } = [];

    [JsonPropertyName("filterMode")]
    public string FilterMode { get; set; } = "Blacklist";

    [JsonPropertyName("filterEntries")]
    public List<string> FilterEntries { get; set; } = [];

    /// <summary>黑白双名单（#20）。</summary>
    [JsonPropertyName("whitelist")]
    public List<string> Whitelist { get; set; } = [];

    [JsonPropertyName("blacklist")]
    public List<string> Blacklist { get; set; } = [];

    /// <summary>主人放行范围（私聊 + 主人所在的群）。</summary>
    [JsonPropertyName("alwaysAllow")]
    public List<string> AlwaysAllow { get; set; } = [];

    [JsonPropertyName("groupCandidates")]
    public List<GroupCandidateDto> GroupCandidates { get; set; } = [];

    [JsonPropertyName("filterCount")]
    public int FilterCount { get; set; }

    [JsonPropertyName("candidates")]
    public List<AccessCandidateDto> Candidates { get; set; } = [];
}

public sealed class AccessCandidateDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = string.Empty;

    /// <summary>下拉框显示用。</summary>
    [JsonIgnore]
    public string Display => $"{Nickname}（{UserId}）";
}

/// <summary>群候选（名单条目用）。</summary>
public sealed class GroupCandidateDto
{
    [JsonPropertyName("groupId")]
    public string GroupId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonIgnore]
    public string Display => $"群 · {Name}（{SessionId}）";
}
