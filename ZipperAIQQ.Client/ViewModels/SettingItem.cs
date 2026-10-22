using CommunityToolkit.Mvvm.ComponentModel;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 一条可编辑的数值参数（客户端侧视图模型）。
/// </summary>
/// <remarks>
/// 范围来自服务端 schema（<c>getSettings</c>），客户端只负责引导；
/// **保存时服务端会再校验一遍** —— UI 的 Min/Max 不是安全边界。
/// </remarks>
public partial class SettingItem : ObservableObject
{
    public string Key { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public double Min { get; init; }

    public double Max { get; init; }

    public string Unit { get; init; } = string.Empty;

    public bool Stored { get; init; }

    [ObservableProperty]
    public partial double Value { get; set; }

    /// <summary>给界面看的范围提示。</summary>
    public string Hint => $"{Min:F0} ~ {Max:F0} {Unit}{(Stored ? "" : "（默认值）")}";

    /// <summary>本地预检：越界就标红（真正的拦截在服务端）。</summary>
    public bool IsOutOfRange => Value < Min || Value > Max;
}
