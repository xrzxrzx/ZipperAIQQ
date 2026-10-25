using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 模型配置页：**普通 LLM（primary）与 JEV（judge）两个固定块**。
/// </summary>
/// <remarks>
/// 2026-10-23 按用户反馈重构：之前用「端点下拉 + 单独 JEV 卡」表达同一件事，
/// 既重复又容易误解。端点名本来就是固定的两个，直接做成两块。
/// </remarks>
public sealed partial class EndpointsViewModel : ObservableObject
{
    private readonly ServerApiClient _api;

    public EndpointsViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    // ── 普通 LLM（primary）──

    [ObservableProperty]
    public partial string LlmBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LlmModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LlmKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LlmStatus { get; set; } = string.Empty;

    // ── JEV（judge）──

    [ObservableProperty]
    public partial string JudgeBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JudgeModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JudgeKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JudgeStatus { get; set; } = string.Empty;

    // ── 网页搜索（独立于模型：自己的 baseUrl + key）──

    [ObservableProperty]
    public partial bool SearchEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string SearchBaseUrl { get; set; } = "https://api.deepseek.com";

    [ObservableProperty]
    public partial string SearchApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchStatus { get; set; } = string.Empty;

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var config = await _api.CallAsync<ConfigDto>("getConfig").ConfigureAwait(true);

            var primary = config?.Endpoints.FirstOrDefault(e =>
                string.Equals(e.Name, "primary", StringComparison.OrdinalIgnoreCase));
            LlmBaseUrl = primary?.BaseUrl ?? string.Empty;
            LlmModel = primary?.Model ?? string.Empty;
            LlmKey = string.Empty;

            var judge = config?.Endpoints.FirstOrDefault(e =>
                string.Equals(e.Name, "judge", StringComparison.OrdinalIgnoreCase));
            JudgeBaseUrl = judge?.BaseUrl ?? string.Empty;
            JudgeModel = judge?.Model ?? string.Empty;
            JudgeKey = string.Empty;

            var search = await _api.CallAsync<SearchDto>("getSearch").ConfigureAwait(true);
            SearchEnabled = search?.Enabled != false;
            SearchBaseUrl = string.IsNullOrWhiteSpace(search?.BaseUrl)
                ? "https://api.deepseek.com"
                : search.BaseUrl;
            SearchApiKey = string.Empty;
            SearchStatus = search?.HasKey == true ? "已配置 key" : "未配置 key";

            LlmStatus = "已加载";
            JudgeStatus = "已加载";
        }
        catch (Exception ex)
        {
            LlmStatus = $"加载失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveLlmAsync()
    {
        try
        {
            var result = await _api.CallAsync<SaveResultDto>("setConfig", new
            {
                endpoint = new
                {
                    name = "primary",
                    baseUrl = LlmBaseUrl.Trim(),
                    model = LlmModel.Trim(),
                    key = string.IsNullOrWhiteSpace(LlmKey) ? null : LlmKey,
                },
            }).ConfigureAwait(true);

            LlmStatus = result?.Skipped == true ? "无变化" : "普通 LLM 已保存";
            LlmKey = string.Empty;
        }
        catch (Exception ex)
        {
            LlmStatus = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveJudgeAsync()
    {
        try
        {
            var result = await _api.CallAsync<SaveResultDto>("setConfig", new
            {
                endpoint = new
                {
                    name = "judge",
                    baseUrl = JudgeBaseUrl.Trim(),
                    model = JudgeModel.Trim(),
                    key = string.IsNullOrWhiteSpace(JudgeKey) ? null : JudgeKey,
                },
            }).ConfigureAwait(true);

            JudgeStatus = result?.Skipped == true ? "无变化" : "JEV 端点已保存";
            JudgeKey = string.Empty;
        }
        catch (Exception ex)
        {
            JudgeStatus = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveSearchAsync()
    {
        try
        {
            var result = await _api.CallAsync<SearchDto>("setSearch", new
            {
                enabled = SearchEnabled,
                baseUrl = SearchBaseUrl.Trim(),
                apiKey = string.IsNullOrWhiteSpace(SearchApiKey) ? null : SearchApiKey,
            }).ConfigureAwait(true);

            SearchApiKey = string.Empty;
            SearchStatus = result?.HasKey == true
                ? "网页搜索已保存（已配置 key）"
                : "网页搜索已保存（未配置 key）";
        }
        catch (Exception ex)
        {
            SearchStatus = $"保存失败：{ex.Message}";
        }
    }
}
