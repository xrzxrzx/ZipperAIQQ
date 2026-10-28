using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>审计页 VM：配置变更记录（最新在最后，刷新后自动滚底，#7）。</summary>
public sealed partial class AuditViewModel : ObservableObject
{
    private readonly ServerApiClient _api;

    public AuditViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public ObservableCollection<AuditDto> Audits { get; } = [];

    [ObservableProperty]
    public partial string StatusLine { get; set; } = string.Empty;

    [RelayCommand]
    private async Task LoadAuditAsync()
    {
        try
        {
            var list = await _api.CallAsync<List<AuditDto>>("getAudit", new { limit = 50 })
                .ConfigureAwait(true);

            Audits.Clear();
            foreach (var item in (list ?? []).OrderBy(a => a.At))
            {
                Audits.Add(item);
            }

            StatusLine = $"审计 {Audits.Count} 条";
        }
        catch (Exception ex)
        {
            StatusLine = $"读取审计失败：{ex.Message}";
        }
    }

    /// <summary>心跳用：条数变了才重建。</summary>
    public async Task RefreshIncrementalAsync()
    {
        try
        {
            var list = await _api.CallAsync<List<AuditDto>>("getAudit", new { limit = 50 })
                .ConfigureAwait(true);

            if (list is null || list.Count == Audits.Count)
            {
                return;
            }

            Audits.Clear();
            foreach (var item in list.OrderBy(a => a.At))
            {
                Audits.Add(item);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"客户端异常：{ex}");
            // 心跳失败不打扰
        }
    }
}
