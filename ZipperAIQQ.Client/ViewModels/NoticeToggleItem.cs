using CommunityToolkit.Mvvm.ComponentModel;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>一条通知响应开关（客户端侧）。</summary>
public partial class NoticeToggleItem : ObservableObject
{
    public string Type { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    /// <summary>common / other（#34）。</summary>
    public string Group { get; init; } = "other";

    [ObservableProperty]
    public partial bool Enabled { get; set; }
}
