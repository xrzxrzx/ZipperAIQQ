using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>一行可编辑的策略（工具 / 命令 / NapCat API 共用）。</summary>
public sealed partial class ToolPolicyRow : ObservableObject
{
    public string Category { get; init; } = string.Empty;

    public string CategoryLabel { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string? HardFloor { get; init; }

    /// <summary>
    /// 该行适用的平台码（<c>getToolCatalog</c> 的 <c>platforms</c>，S3-D / 决策五）。
    /// **空表 = 通用**：旧服务端不返回 <c>platforms</c> 时就是空表，归到「通用」组。
    /// </summary>
    public IReadOnlyList<string> Platforms { get; init; } = [];

    /// <summary>分组键：空串 = 通用，否则是平台码（如 <c>qq</c>）。</summary>
    public string PlatformGroupKey => ToolPlatformGrouping.KeyOf(Platforms);

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    /// <summary>角色（界面显示中文，保存时映射回英文枚举）。</summary>
    [ObservableProperty]
    public partial string Role { get; set; } = "普通";

    public string DefaultRoleDisplay { get; init; } = "普通";

    public string Subtitle => $"{Key} · 默认 {DefaultRoleDisplay}";
}

/// <summary>
/// 工具页里的一组（S3-D / 决策五）：**分组标题 + 该组的行**。
/// </summary>
/// <remarks>
/// 分组是**展示层**的东西：开关与角色下拉仍按原来的 <c>setToolPolicy</c> 全量保存，
/// 平台归属不写回（它由服务端的工具声明决定，客户端改它没有意义）。
/// </remarks>
public sealed class ToolPolicyGroup
{
    public ToolPolicyGroup(string key, IEnumerable<ToolPolicyRow> rows)
    {
        Key = key;
        Title = ToolPlatformGrouping.TitleOf(key);
        foreach (var row in rows)
        {
            Rows.Add(row);
        }
    }

    /// <summary>分组键：空串 = 通用，否则是平台码（如 <c>qq</c>）。</summary>
    public string Key { get; }

    /// <summary>界面标题：「通用」/「QQ 专用」/「抖音专用」。</summary>
    public string Title { get; }

    public ObservableCollection<ToolPolicyRow> Rows { get; } = [];
}

/// <summary>
/// 平台分组口径（S3-D / 决策五）：平台码 → 可读文案 + 组的先后。
/// </summary>
/// <remarks>
/// <para>
/// 平台码与 <c>ChatRef.Platform</c> 同口径（<c>qq</c> / <c>douyin</c>）；**未知平台码原样显示**，
/// 将来加平台不用改客户端。文案与「人员与名单」页（S3-C）保持一致。
/// </para>
/// <para>
/// 分组规则（按服务端 <c>platforms</c>）：**只有一个平台 → 该平台专用组**；
/// **空表 / 多平台 → 通用组**（多个平台都能用，那就是通用）。
/// </para>
/// </remarks>
internal static class ToolPlatformGrouping
{
    /// <summary>通用组的键（空串，免得和真实平台码撞上）。</summary>
    internal const string GeneralKey = "";

    /// <summary>已知平台的展示顺序（通用之后依次）。</summary>
    private static readonly string[] KnownPlatformOrder = ["qq", "douyin"];

    private static readonly Dictionary<string, string> PlatformLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qq"] = "QQ",
        ["douyin"] = "抖音",
    };

    /// <summary>平台表 → 分组键：单个平台给该平台码，空表 / 多平台给通用。</summary>
    internal static string KeyOf(IReadOnlyList<string> platforms)
    {
        string? single = null;
        foreach (var platform in platforms)
        {
            if (string.IsNullOrWhiteSpace(platform))
            {
                continue;
            }

            var code = platform.Trim();
            if (single is null)
            {
                single = code;
            }
            else if (!string.Equals(single, code, StringComparison.OrdinalIgnoreCase))
            {
                return GeneralKey;
            }
        }

        return single ?? GeneralKey;
    }

    /// <summary>平台码 → 界面文案；空串 = 通用，未知平台码原样显示。</summary>
    internal static string LabelOf(string key) =>
        string.IsNullOrEmpty(key)
            ? "通用"
            : PlatformLabels.TryGetValue(key, out var label) ? label : key;

    /// <summary>分组标题：「通用」/「QQ 专用」/「抖音专用」（中文后直接接，拉丁字母后留一个空格）。</summary>
    internal static string TitleOf(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "通用";
        }

        var label = LabelOf(key);
        return char.IsAscii(label[^1]) ? $"{label} 专用" : $"{label}专用";
    }

    /// <summary>组的排序：通用 → QQ → 抖音 → 未知平台（排在后面，按码排）。</summary>
    internal static int OrderOf(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return 0;
        }

        var index = Array.FindIndex(
            KnownPlatformOrder,
            known => string.Equals(known, key, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? KnownPlatformOrder.Length + 1 : index + 1;
    }
}

/// <summary>
/// 工具管理页（#28 #29）：三类能力的开关 + 最低角色 + 搜索。
/// </summary>
/// <remarks>
/// S3-D（D1.4 / 决策五）：每类能力**按平台分组展示**（通用 / QQ 专用 / 抖音专用）。
/// 分组只影响显示，保存仍是原来的全量 <c>setToolPolicy</c>。
/// </remarks>
public sealed partial class ToolsViewModel : ObservableObject
{
    public static readonly string[] Roles = ["普通", "白名单", "管理员", "主人"];

    private static readonly Dictionary<string, string> ToChinese = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Normal"] = "普通",
        ["Whitelist"] = "白名单",
        ["Admin"] = "管理员",
        ["Owner"] = "主人",
    };

    private static readonly Dictionary<string, string> ToEnglish = ToChinese
        .ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    /// <summary>角色下拉的选项（x:Bind 需要实例属性）。</summary>
    public IReadOnlyList<string> RoleOptions => Roles;

    private readonly ServerApiClient _api;
    private readonly List<ToolPolicyRow> _tools = [];
    private readonly List<ToolPolicyRow> _commands = [];
    private readonly List<ToolPolicyRow> _apis = [];

    public ToolsViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    /// <summary>工具（按平台分组）。</summary>
    public ObservableCollection<ToolPolicyGroup> Tools { get; } = [];

    /// <summary>命令（按平台分组）。</summary>
    public ObservableCollection<ToolPolicyGroup> Commands { get; } = [];

    /// <summary>NapCat API（按平台分组）。</summary>
    public ObservableCollection<ToolPolicyGroup> ChannelApis { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var items = await _api.CallAsync<List<ToolCatalogItemDto>>("getToolCatalog").ConfigureAwait(true) ?? [];

            _tools.Clear();
            _commands.Clear();
            _apis.Clear();

            foreach (var item in items)
            {
                var row = new ToolPolicyRow
                {
                    Category = item.Category,
                    CategoryLabel = item.CategoryLabel,
                    Key = item.Key,
                    Label = item.Label,
                    HardFloor = item.HardFloor,
                    Enabled = item.Enabled,
                    Role = ToChinese.GetValueOrDefault(item.Role, "普通"),
                    DefaultRoleDisplay = ToChinese.GetValueOrDefault(item.DefaultRole, "普通"),
                    Platforms = item.Platforms,
                };

                switch (item.Category)
                {
                    case "command":
                        _commands.Add(row);
                        break;
                    case "channelapi":
                        _apis.Add(row);
                        break;
                    default:
                        _tools.Add(row);
                        break;
                }
            }

            ApplyFilter();
            Status = $"共 {items.Count} 项（{_tools.Count} 工具 / {_commands.Count} 命令 / {_apis.Count} API）";
        }
        catch (Exception ex)
        {
            Status = $"加载失败：{ex.Message}";
        }
    }

    private void ApplyFilter()
    {
        var keyword = SearchText.Trim();
        bool Match(ToolPolicyRow r) =>
            string.IsNullOrEmpty(keyword) ||
            r.Label.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            r.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase);

        Fill(Tools, _tools.Where(Match));
        Fill(Commands, _commands.Where(Match));
        Fill(ChannelApis, _apis.Where(Match));
    }

    /// <summary>按平台分组填充一张卡：**空组不出现**（没内容就别留个光秃秃的标题）。</summary>
    private static void Fill(ObservableCollection<ToolPolicyGroup> target, IEnumerable<ToolPolicyRow> rows)
    {
        target.Clear();

        var groups = rows
            .GroupBy(r => r.PlatformGroupKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => ToolPlatformGrouping.OrderOf(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            target.Add(new ToolPolicyGroup(group.Key, group));
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var items = _tools.Concat(_commands).Concat(_apis)
                .Select(r => new
                {
                    category = r.Category,
                    key = r.Key,
                    enabled = r.Enabled,
                    role = ToEnglish.GetValueOrDefault(r.Role, "Normal"),
                })
                .ToList();

            if (items.Count == 0)
            {
                Status = "没有可保存的项。";
                return;
            }

            var result = await _api.CallAsync<SaveResultDto>("setToolPolicy", new { items })
                .ConfigureAwait(true);

            Status = $"已保存 {result?.Updated ?? 0} 项（即时生效）";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }
}
