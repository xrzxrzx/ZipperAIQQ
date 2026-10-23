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
        control.Box.ItemsSource = control._all.Take(100).ToList();
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
