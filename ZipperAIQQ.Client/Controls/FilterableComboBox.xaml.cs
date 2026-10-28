using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ZipperAIQQ.Client.Controls;

/// <summary>
/// 可输入模糊查找的下拉（问题记录 #17）。
/// </summary>
/// <remarks>
/// 用 <see cref="AutoSuggestBox"/> 而不是可编辑 ComboBox：后者在 WinUI3 里拿不到
/// 文本变化事件，过滤只能等回车，体验差一截。
/// </remarks>
public sealed partial class FilterableComboBox : UserControl
{
    private List<string> _all = [];
    private bool _syncing;

    public FilterableComboBox()
    {
        InitializeComponent();

        // 聚焦时恢复完整候选：选中一次后 ItemsSource 会被搜成只剩一项，
        // 不重置的话第二次点开就"下拉不了"（用户实测反馈）。
        //
        // 2026-10-28：**聚焦就展开候选列表**。之前只有"打字"才弹建议，
        // 点进去什么都不显示，用户以为"下拉框没了"。现在点一下就能看到全部候选，
        // 想筛再打字 —— 既像下拉，又保留模糊搜索。
        Box.GotFocus += (_, _) =>
        {
            ResetItems();
            OpenSuggestions();
        };

        Box.Loaded += (_, _) => ResetItems();

        // 点右侧放大镜：没输入任何字时也当"打开下拉"处理（否则点了像没反应）
        Box.QuerySubmitted += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.QueryText))
            {
                ResetItems();
                OpenSuggestions();
            }
        };
    }

    /// <summary>展开候选列表（AutoSuggestBox 的"下拉"就是建议浮层）。</summary>
    private void OpenSuggestions()
    {
        if (_all.Count == 0)
        {
            return;      // 没有候选就别弹一个空框
        }

        Box.IsSuggestionListOpen = true;
    }

    private void ResetItems()
    {
        Box.ItemsSource = _all.Take(100).ToList();
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IList<string>), typeof(FilterableComboBox),
        new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(string), typeof(FilterableComboBox),
        new PropertyMetadata(null, OnSelectedChanged));

    public static readonly DependencyProperty PlaceholderTextProperty = DependencyProperty.Register(
        nameof(PlaceholderText), typeof(string), typeof(FilterableComboBox),
        new PropertyMetadata("搜索…", OnPlaceholderChanged));

    public IList<string>? ItemsSource
    {
        get => (IList<string>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string? SelectedItem
    {
        get => (string?)GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (FilterableComboBox)d;
        control._all = e.NewValue is IList<string> list ? list.ToList() : [];
        control.ResetItems();
    }

    private static void OnSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (FilterableComboBox)d;
        if (control._syncing)
        {
            return;
        }

        control.Box.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FilterableComboBox)d).Box.PlaceholderText = e.NewValue as string ?? string.Empty;

    private void Box_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // 只在用户输入时过滤；程序设置 Text 不触发，避免选中后被自己过滤掉
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        var keyword = sender.Text?.Trim() ?? string.Empty;
        sender.ItemsSource = string.IsNullOrEmpty(keyword)
            ? _all.Take(100).ToList()
            : _all.Where(x => x.Contains(keyword, StringComparison.OrdinalIgnoreCase)).Take(100).ToList();
    }

    private void Box_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not string value)
        {
            return;
        }

        _syncing = true;
        try
        {
            SelectedItem = value;
            sender.Text = value;
            ResetItems();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Box_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var text = (args.ChosenSuggestion as string) ?? args.QueryText;
        if (!string.IsNullOrWhiteSpace(text) && _all.Contains(text))
        {
            SelectedItem = text;
        }
    }
}
