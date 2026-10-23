using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;
using ZipperAIQQ.Client.Views;
using Windows.Graphics;

namespace ZipperAIQQ.Client;

/// <summary>主窗口：Mica 背景 + 自定义标题栏 + NavigationView 导航。</summary>
public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;

    /// <summary>
    /// 恢复期标志：初始化时 NavView 会自己触发一次 PaneOpened/PaneClosed，
    /// 那会把刚从文件读出来的状态又覆盖掉（实测：重启后又被写回 true）。
    /// 恢复期间的这类事件一律忽略。
    /// </summary>
    private bool _restoringPane = true;

    public MainWindow(MainViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        // x:Bind 读的就是这个属性，必须在 InitializeComponent 之前赋值
        Vm = vm;
        InitializeComponent();

        _appWindow = AppWindow;
        InitializeTitleBar();

        // 任务栏/Alt+Tab 图标也用头像（.ico 多尺寸比 png 稳）
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "avatar.ico");
        if (File.Exists(icon))
        {
            _appWindow.SetIcon(icon);
        }

        // 初始窗口：按屏幕工作区取 78% × 82%，并居中（以前固定 1200×820 偏小）
        var display = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var width = Math.Min(1720, (int)(work.Width * 0.78));
        var height = Math.Min(1080, (int)(work.Height * 0.82));

        _appWindow.Resize(new SizeInt32(width, height));
        _appWindow.Move(new PointInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2)));

        NavView.SelectedItem = NavView.MenuItems[0];

        // 恢复上次的侧栏展开状态与选中项（关应用时记着，下次打开就还原）
        NavView.IsPaneOpen = Vm.IsPaneOpen;
        var restored = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, Vm.SelectedNavTag, StringComparison.Ordinal));
        if (restored is not null)
        {
            NavView.SelectedItem = restored;
        }

        _restoringPane = false;

        // 初始页 = 上次选中的页（以前这里硬写 Navigate(StatusPage)，
        // 会把刚恢复的选中项覆盖掉，表现为"切页不生效"）
        ContentFrame.Navigate(PageForTag(Vm.SelectedNavTag));

        // 0.5s 自动刷新（用户要求：数据要实时更新）
        Vm.StartAutoRefresh();

        // 启动即尝试连接（地址有默认值；连不上就在状态条上提示，不影响界面）
        _ = Vm.ConnectCommand.ExecuteAsync(null);

        // 窗口关掉：记录侧栏状态（初始化期间的事件不可靠，这里才准）+ 停掉定时器
        Closed += (_, _) =>
        {
            Vm.IsPaneOpen = NavView.IsPaneOpen;
            Vm.StopAutoRefresh();
        };
    }

    public MainViewModel Vm { get; }

    /// <summary>隐藏系统标题栏，内容扩展到标题栏区域，系统按钮透明化。</summary>
    private void InitializeTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _appWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;

        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonForegroundColor = Colors.Gray;
        _appWindow.TitleBar.ButtonInactiveForegroundColor = Colors.Gray;
    }

    private void AppTitleBar_Loaded(object sender, RoutedEventArgs e) => UpdateTitleBarLayout();

    private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarLayout();

    /// <summary>为系统按钮预留左右空间，并把标题栏设为拖动区域。</summary>
    private void UpdateTitleBarLayout()
    {
        if (AppTitleBar.XamlRoot is null)
        {
            return;
        }

        var scale = AppTitleBar.XamlRoot.RasterizationScale > 0
            ? AppTitleBar.XamlRoot.RasterizationScale
            : 1.0;

        LeftPaddingColumn.Width = new GridLength(_appWindow.TitleBar.LeftInset / scale);
        RightPaddingColumn.Width = new GridLength(_appWindow.TitleBar.RightInset / scale);

        var totalWidth = AppTitleBar.ActualWidth * scale;
        if (totalWidth <= 0)
        {
            return;
        }

        var leftInset = _appWindow.TitleBar.LeftInset;
        var rightInset = _appWindow.TitleBar.RightInset;
        var dragWidth = Math.Max(0, totalWidth - leftInset - rightInset);

        _appWindow.TitleBar.SetDragRectangles(
        [
            new RectInt32((int)leftInset, 0, (int)dragWidth, (int)(AppTitleBar.ActualHeight * scale)),
        ]);
    }

    private void NavView_PaneOpened(NavigationView sender, object args)
    {
        if (_restoringPane)
        {
            return;
        }

        Vm.IsPaneOpen = true;
    }

    private void NavView_PaneClosed(NavigationView sender, object args)
    {
        if (_restoringPane)
        {
            return;
        }

        Vm.IsPaneOpen = false;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag?.ToString() is not { } tag)
        {
            return;
        }

        if (_restoringPane)
        {
            return;
        }

        Vm.SelectedNavTag = tag;
        Vm.CurrentPage = tag;

        ContentFrame.Navigate(PageForTag(tag));
    }

    /// <summary>导航项 Tag → 页面类型。</summary>
    private static Type PageForTag(string tag) => tag switch
    {
        "sessions" => typeof(SessionsPage),
        "data" => typeof(DataPage),
        "models" => typeof(EndpointsPage),
        "runtime" => typeof(RuntimePage),
        "persona" => typeof(PersonaPage),
        "access" => typeof(AccessPage),
        "tools" => typeof(ToolsPage),
        "audit" => typeof(AuditPage),
        _ => typeof(StatusPage),
    };
}
