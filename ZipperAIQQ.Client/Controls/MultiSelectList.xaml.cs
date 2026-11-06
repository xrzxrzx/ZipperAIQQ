using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ZipperAIQQ.Client.Controls;

/// <summary>
/// 带搜索的多选列表（问题记录 #26）。
/// </summary>
/// <remarks>
/// 管理员 / 白名单 / 黑名单原来都是「逗号分隔的文本框」——要手打账号，
/// 而且完全看不出候选是谁。这里用勾选式列表 + 搜索。
/// </remarks>
public sealed partial class MultiSelectList : UserControl
{
    private List<string> _all = [];
    private bool _syncing;

    public MultiSelectList()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncSelectionFromValues();
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IList<string>), typeof(MultiSelectList),
        new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty SelectedValuesProperty = DependencyProperty.Register(
        nameof(SelectedValues), typeof(IList<string>), typeof(MultiSelectList),
        new PropertyMetadata(null, OnValuesChanged));

    public IList<string>? ItemsSource
    {
        get => (IList<string>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public IList<string>? SelectedValues
    {
        get => (IList<string>?)GetValue(SelectedValuesProperty);
        set => SetValue(SelectedValuesProperty, value);
    }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MultiSelectList)d;

        if (e.OldValue is INotifyCollectionChanged oldNotify)
        {
            oldNotify.CollectionChanged -= control.OnCollectionChanged;
        }

        control._all = e.NewValue is IList<string> list ? list.ToList() : [];
        if (e.NewValue is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged += control.OnCollectionChanged;
        }

        control.RefreshItems();
        control.SyncSelectionFromValues();
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MultiSelectList)d;

        if (e.OldValue is INotifyCollectionChanged oldNotify)
        {
            oldNotify.CollectionChanged -= control.OnValuesCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged += control.OnValuesCollectionChanged;
        }

        control.SyncSelectionFromValues();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _all = ItemsSource?.ToList() ?? [];
        RefreshItems();
        SyncSelectionFromValues();
    }

    private void OnValuesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => SyncSelectionFromValues();

    private void RefreshItems()
    {
        var keyword = SearchBox.Text?.Trim() ?? string.Empty;
        var view = string.IsNullOrEmpty(keyword)
            ? _all
            : _all.Where(x => x.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        // 内容没变就**不要**重设 ItemsSource：每换一次 ListView 都会滚回顶部，
        // 保存名单后「点一条 → 跳回最上面」就是这么来的（问题记录 05 #2）。
        if (List.ItemsSource is IEnumerable<string> current &&
            current.SequenceEqual(view, StringComparer.Ordinal))
        {
            return;
        }

        _syncing = true;
        try
        {
            List.ItemsSource = view;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncSelectionFromValues()
    {
        // 自己正在把 ListView 的选择写回 SelectedValues：那次写回会再触发集合事件，
        // 若在这里再清一遍 SelectedItems，用户点一下列表就会被打回原处/滚回顶部。
        if (_syncing)
        {
            return;
        }

        var values = SelectedValues;
        if (values is null || List.ItemsSource is not IEnumerable<string> view)
        {
            return;
        }

        var wanted = view.Where(values.Contains).ToList();

        // 已经一致就什么都不做：SelectedItems.Add 会把列表 ScrollIntoView 到最后一条
        if (List.SelectedItems.Count == wanted.Count &&
            wanted.All(item => List.SelectedItems.Contains(item)))
        {
            return;
        }

        var scroll = FindDescendant<ScrollViewer>(List);
        var offset = scroll?.VerticalOffset ?? 0;

        _syncing = true;
        try
        {
            List.SelectedItems.Clear();
            foreach (var item in wanted)
            {
                List.SelectedItems.Add(item);
            }
        }
        finally
        {
            _syncing = false;
        }

        // 程序化补选会把列表滚到最后一条被选中的项 —— 把原位置还回去
        if (scroll is not null && offset > 0)
        {
            DispatcherQueue.TryEnqueue(() => scroll.ChangeView(null, offset, null, true));
        }
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshItems();
        SyncSelectionFromValues();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SelectedValues is not { } values)
        {
            return;
        }

        _syncing = true;
        try
        {
            var selected = List.SelectedItems.OfType<string>().ToList();

            // 只改「当前可见项」的选择：搜索过滤不能让看不见的已选项消失
            foreach (var item in values.ToList())
            {
                if (List.ItemsSource is IEnumerable<string> view && view.Contains(item) && !selected.Contains(item))
                {
                    values.Remove(item);
                }
            }

            foreach (var item in selected)
            {
                if (!values.Contains(item))
                {
                    values.Add(item);
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }
}
