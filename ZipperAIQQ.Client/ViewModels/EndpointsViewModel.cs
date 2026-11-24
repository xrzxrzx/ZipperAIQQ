using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 模型配置页：**一把 DeepSeek Key + 普通 LLM（primary）+ JEV（judge）+ 网页搜索地址**。
/// </summary>
/// <remarks>
/// <para>
/// 2026-10-23 按用户反馈重构：端点下拉 + 单独 JEV 卡表达同一件事既重复又容易误解，改成正并列。
/// </para>
/// <para>
/// <b>2026-11-23 密钥收敛（问题记录 06 §5）</b>：页面上只剩**一个** key 输入框。
/// 原来 LLM / JEV / 搜索各一个框、各存各的密文，与「对外一把 key」的需求直接冲突；
/// 现在三张卡只保留地址与模型，key 统一走 <c>setApiKey</c>。
/// judge 的旧 key 仍留在库里（legacy 留档），解析顺序由服务端决定，界面不再暴露输入框。
/// </para>
/// </remarks>
public sealed partial class EndpointsViewModel : ObservableObject
{
    private readonly ServerApiClient _api;

    public EndpointsViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    // ── DeepSeek Key（全链路唯一一把）──

    [ObservableProperty]
    public partial string ApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKeyStatus { get; set; } = "未加载";

    // ── 普通 LLM（primary）──

    [ObservableProperty]
    public partial string LlmBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LlmModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LlmStatus { get; set; } = string.Empty;

    // ── JEV（judge）──

    [ObservableProperty]
    public partial string JudgeBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JudgeModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JudgeStatus { get; set; } = string.Empty;

    // ── 网页搜索（地址独立；key 与全链路共用）──

    [ObservableProperty]
    public partial bool SearchEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string SearchBaseUrl { get; set; } = ClientDefaults.LlmBaseUrl;

    [ObservableProperty]
    public partial string SearchStatus { get; set; } = string.Empty;

    // ── 对话增强（DeepSeek 全家桶的服务端增强，T2.6）──

    [ObservableProperty]
    public partial bool EnhanceWebSearch { get; set; }

    [ObservableProperty]
    public partial bool EnhanceReadUrl { get; set; }

    [ObservableProperty]
    public partial bool EnhanceSafetyCheck { get; set; }

    [ObservableProperty]
    public partial bool EnhanceTitle { get; set; }

    [ObservableProperty]
    public partial string EnhanceStatus { get; set; } = string.Empty;

    /// <summary>副作用说明由**服务端**给（两边写死两份口径迟早不一致）。</summary>
    [ObservableProperty]
    public partial string EnhanceNote { get; set; } = string.Empty;

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

            var judge = config?.Endpoints.FirstOrDefault(e =>
                string.Equals(e.Name, "judge", StringComparison.OrdinalIgnoreCase));
            JudgeBaseUrl = judge?.BaseUrl ?? string.Empty;
            JudgeModel = judge?.Model ?? string.Empty;

            ApiKey = string.Empty;
            ApiKeyStatus = DescribeKey(config?.ApiKey);

            var search = await _api.CallAsync<SearchDto>("getSearch").ConfigureAwait(true);
            SearchEnabled = search?.Enabled != false;
            SearchBaseUrl = string.IsNullOrWhiteSpace(search?.BaseUrl)
                ? ClientDefaults.LlmBaseUrl
                : search.BaseUrl;
            SearchStatus = search?.HasKey == true ? "地址已加载（key 已配置）" : "地址已加载（缺 key）";

            var enhance = await _api.CallAsync<DialogEnhanceDto>("getDialogEnhance").ConfigureAwait(true);
            if (enhance is not null)
            {
                EnhanceWebSearch = enhance.WebSearch;
                EnhanceReadUrl = enhance.ReadUrl;
                EnhanceSafetyCheck = enhance.SafetyCheck;
                EnhanceTitle = enhance.Title;
                EnhanceStatus = enhance.Summary;
                EnhanceNote = enhance.Note;
            }

            LlmStatus = "已加载";
            JudgeStatus = "已加载";
        }
        catch (Exception ex)
        {
            LlmStatus = $"加载失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveApiKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            ApiKeyStatus = "请先填入 key（留空不会清掉已有的那把）";
            return;
        }

        try
        {
            var result = await _api
                .CallAsync<SetApiKeyResultDto>("setApiKey", new { key = ApiKey })
                .ConfigureAwait(true);

            // 明文只在这一个请求里出现：无论成功与否，界面上都立刻抹掉
            ApiKey = string.Empty;
            ApiKeyStatus = result?.Updated == true
                ? $"已保存（{DescribeKey(result.Config?.ApiKey)}）"
                : $"没有变化（{DescribeKey(result?.Config?.ApiKey)}）";
        }
        catch (Exception ex)
        {
            ApiKey = string.Empty;
            ApiKeyStatus = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveLlmAsync()
    {
        try
        {
            // 收敛后这里**不再带 key**：客户端没有第二把钥匙可填，服务端也从全局槽位取
            var result = await _api.CallAsync<SaveResultDto>("setConfig", new
            {
                endpoint = new
                {
                    name = "primary",
                    baseUrl = LlmBaseUrl.Trim(),
                    model = LlmModel.Trim(),
                },
            }).ConfigureAwait(true);

            LlmStatus = result?.Skipped == true ? "无变化" : "普通 LLM 已保存";
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
                },
            }).ConfigureAwait(true);

            JudgeStatus = result?.Skipped == true ? "无变化" : "JEV 端点已保存";
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
            }).ConfigureAwait(true);

            SearchStatus = result?.HasKey == true
                ? "网页搜索已保存（key 已配置）"
                : "网页搜索已保存（未配置 key）";
        }
        catch (Exception ex)
        {
            SearchStatus = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveEnhanceAsync()
    {
        try
        {
            var result = await _api.CallAsync<DialogEnhanceDto>("setDialogEnhance", new
            {
                webSearch = EnhanceWebSearch,
                readUrl = EnhanceReadUrl,
                safetyCheck = EnhanceSafetyCheck,
                title = EnhanceTitle,
            }).ConfigureAwait(true);

            if (result is not null)
            {
                // 以服务端回的状态为准：它可能因为我们没给某个键而保留旧值
                EnhanceWebSearch = result.WebSearch;
                EnhanceReadUrl = result.ReadUrl;
                EnhanceSafetyCheck = result.SafetyCheck;
                EnhanceTitle = result.Title;
                EnhanceNote = string.IsNullOrWhiteSpace(result.Note) ? EnhanceNote : result.Note;

                EnhanceStatus = result.Skipped
                    ? $"{result.Summary}（无变化）"
                    : result.Effective
                        ? result.Summary
                        : $"{result.Reason ?? "已保存，但未生效"}";
            }
        }
        catch (Exception ex)
        {
            EnhanceStatus = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>把服务端回的「已配置 / 掩码 / 留档把数」读成一句人话。</summary>
    private static string DescribeKey(ApiKeyStateDto? state)
    {
        if (state?.Configured != true)
        {
            return "未配置 key";
        }

        return state.Slots > 1
            ? $"已配置 {state.Masked}（另有 {state.Slots - 1} 把留档，不生效）"
            : $"已配置 {state.Masked}";
    }
}
