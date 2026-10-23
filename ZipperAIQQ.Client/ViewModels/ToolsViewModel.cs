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

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    [ObservableProperty]
    public partial string Role { get; set; } = "Normal";

    public string Subtitle => $"{Key} · 默认 {Role}";
}

/// <summary>
/// 工具管理页（#28 #29）：三类能力的开关 + 最低角色 + 搜索。
/// </summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    public static readonly string[] Roles = ["Normal", "Whitelist", "Admin", "Owner"];

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

    public ObservableCollection<ToolPolicyRow> Tools { get; } = [];

    public ObservableCollection<ToolPolicyRow> Commands { get; } = [];

    public ObservableCollection<ToolPolicyRow> ChannelApis { get; } = [];

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
                    Role = item.Role,
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

    private static void Fill(ObservableCollection<ToolPolicyRow> target, IEnumerable<ToolPolicyRow> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
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
                    role = r.Role,
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
