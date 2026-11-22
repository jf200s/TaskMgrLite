using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using TaskMgrLite.Core;
using TaskMgrLite.Interop;
using TaskMgrLite.ViewModels;

namespace TaskMgrLite;

public partial class MainWindow : Window
{
    private bool _commandLineScanQueued;

    private readonly DispatcherTimer _toastTimer;
    private readonly ContextMenu _rowMenu;
    private readonly ContextMenu _blankMenu;
    private KillReport? _lastKillReport;
    private string _lastKillLabel = string.Empty;

    public MainWindow()
    {
        InitializeComponent();

        // 表头在 GridView 里是模板生成的，直接挂路由事件比给每列写 EventSetter 稳
        ProcessList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));

        // 命令行懒加载的触发源：首屏、滚动、选中、过滤后容器重建
        ProcessList.Loaded += OnListLoaded;
        ProcessList.SelectionChanged += OnListSelectionChanged;
        ProcessList.PreviewKeyDown += OnListPreviewKeyDown;
        ProcessList.ItemContainerGenerator.StatusChanged += OnGeneratorStatusChanged;
        ProcessList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnListScrollChanged));

        _rowMenu = (ContextMenu)FindResource("RowContextMenu");
        _blankMenu = (ContextMenu)FindResource("BlankContextMenu");
        ProcessList.ContextMenu = _blankMenu;

        // 右键先选中光标下的行，再弹 ContextMenu
        ProcessList.PreviewMouseRightButtonDown += OnListPreviewMouseRightButtonDown;

        _toastTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        _toastTimer.Tick += (_, _) => HideToast();

        Closed += (_, _) => _toastTimer.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            // 暗色标题栏：20 是 DWMWA_USE_IMMERSIVE_DARK_MODE，19 是旧 build 的属性号
            int on = 1;
            NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            NativeMethods.DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));

            // Windows 11 圆角。老系统返回非 0，直接忽略。
            int corner = NativeMethods.DWMWCP_ROUND;
            int cornerHr = NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            // Mica：只有系统接受 backdrop 类型时才扩展玻璃帧。
            // 失败就保持 XAML 里的纯暗色背景，不做透明化，避免老系统黑屏。
            int backdrop = NativeMethods.DWMSBT_MAINWINDOW;
            int hr = NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            Log.WriteLine($"[dwm] corner-hr={cornerHr} backdrop-hr={hr}");

            if (hr == 0)
            {
                var margins = new NativeMethods.MARGINS
                {
                    Left = -1,
                    Right = -1,
                    Top = -1,
                    Bottom = -1
                };

                NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

                if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                    target.BackgroundColor = Colors.Transparent;

                // 90% 暗色罩在 Mica 上：保留一点材质感，同时保证浅色系统主题下文字可读。
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x14, 0x17));
            }
        }
        catch
        {
            // 系统不支持 DWM 材质/圆角就保持默认暗色窗口，不影响功能
        }
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        // 点在列宽拖拽手柄上不排序
        if (e.OriginalSource is Thumb) return;

        GridViewColumnHeader? header = FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Content is not TextBlock { Tag: string key }) return;
        if (DataContext is not MainViewModel vm) return;

        // 方向判定问 VM：排序状态是 VM 的唯一事实，窗口不再自己记一份，
        // 否则切树形挂起排序后这里会算错方向。
        vm.ToggleSort(key);
    }

    private void OnExpanderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProcessRowViewModel row }
            && DataContext is MainViewModel vm)
        {
            vm.ToggleExpanded(row);
        }

        e.Handled = true;
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        if (vm.IsTreeMode) vm.SetFlatMode();
        else vm.SetTreeMode();
    }

    // ---------------- Round 3：结束进程 / 命令行懒加载 ----------------

    private async void OnKillProcessClick(object sender, RoutedEventArgs e)
    {
        await KillSelectedAsync(tree: false);
    }

    private async void OnKillTreeClick(object sender, RoutedEventArgs e)
    {
        await KillSelectedAsync(tree: true);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.RequestRefresh();
    }

    private async void OnOpenLocationClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (ProcessList.SelectedItem is not ProcessRowViewModel row) return;

        try
        {
            ProcessDetailsResult details = await vm.GetDetailsAsync(row);

            if (string.IsNullOrEmpty(details.ImagePath))
            {
                ShowToast($"无法读取进程路径：{details.PathDisplay}", details.HasAccessDenied);
                return;
            }

            if (!ExplorerLauncher.TryOpen(details.ImagePath, out string error))
                ShowToast("无法打开文件所在位置：" + error, details.HasAccessDenied);
        }
        catch (Exception ex)
        {
            ShowToast("打开文件所在位置失败：" + ex.Message, canElevate: false);
        }
    }

    private void OnCopyPidClick(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is not ProcessRowViewModel row) return;

        try
        {
            Clipboard.SetText(row.Pid.ToString());
            ShowToast($"已复制 PID {row.Pid}");
        }
        catch (Exception ex)
        {
            ShowToast("复制 PID 失败：" + ex.Message);
        }
    }

    private async void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (ProcessList.SelectedItem is not ProcessRowViewModel row) return;

        try
        {
            ProcessDetailsResult details = await vm.GetDetailsAsync(row);

            if (string.IsNullOrEmpty(details.ImagePath))
            {
                ShowToast($"无法复制路径：{details.PathDisplay}", details.HasAccessDenied);
                return;
            }

            Clipboard.SetText(details.ImagePath);
            ShowToast("已复制进程路径");
        }
        catch (Exception ex)
        {
            ShowToast("复制路径失败：" + ex.Message);
        }
    }

    private async Task KillSelectedAsync(bool tree)
    {
        if (DataContext is not MainViewModel vm) return;

        if (ProcessList.SelectedItem is not ProcessRowViewModel row)
        {
            MessageBox.Show(this, "请先选择一个进程。", "结束进程",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            KillReport report = await vm.KillAsync(row, tree);
            string label = tree
                ? $"结束进程树（{row.Name}）"
                : $"结束进程（{row.Name}）";

            ShowKillToast(report, label);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "结束进程失败：" + ex.Message, "结束进程",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowKillToast(KillReport report, string label)
    {
        _lastKillReport = report;
        _lastKillLabel = label;

        ToastText.Text = report.BuildSummary(label);
        ToastDetailsButton.Visibility = report.HasFailures ? Visibility.Visible : Visibility.Collapsed;
        ToastElevateButton.Visibility = report.HasAccessDenied ? Visibility.Visible : Visibility.Collapsed;
        ToastActions.Visibility =
            report.HasFailures || report.HasAccessDenied ? Visibility.Visible : Visibility.Collapsed;

        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ShowToast(string message) => ShowToast(message, canElevate: false);

    private void ShowToast(string message, bool canElevate)
    {
        _lastKillReport = null;
        _lastKillLabel = string.Empty;

        ToastText.Text = message;
        ToastDetailsButton.Visibility = Visibility.Collapsed;
        ToastElevateButton.Visibility = canElevate ? Visibility.Visible : Visibility.Collapsed;
        ToastActions.Visibility = canElevate ? Visibility.Visible : Visibility.Collapsed;

        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.Visibility = Visibility.Collapsed;
    }

    private void OnToastCloseClick(object sender, RoutedEventArgs e) => HideToast();

    private void OnToastDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_lastKillReport is not KillReport report) return;

        HideToast();
        MessageBox.Show(
            this,
            report.BuildMessage(_lastKillLabel),
            "结束进程详情",
            MessageBoxButton.OK,
            report.HasFailures ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void OnToastElevateClick(object sender, RoutedEventArgs e)
    {
        HideToast();

        UiState? state = (DataContext as MainViewModel)?.CaptureUiState();
        if (!ElevationHelper.TryRestartElevated(state))
            ShowToast("提权启动被取消或失败。");
    }

    private void OnListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ListViewItem? item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);

        if (item is not null)
        {
            item.IsSelected = true;
            ProcessList.ContextMenu = _rowMenu;
        }
        else
        {
            // 空白处 / 表头：只提供刷新
            ProcessList.ContextMenu = _blankMenu;
        }
    }

    private void OnListLoaded(object sender, RoutedEventArgs e) => QueueCommandLineScan();

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e) => QueueCommandLineScan();

    /// <summary>
    /// 树形模式的极简键盘支持：Left 折叠、Right 展开。
    /// 只有状态真的变化才 Handled，避免叶子和平铺模式吃掉默认按键。
    /// </summary>
    private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right)) return;
        if (DataContext is not MainViewModel vm || !vm.IsTreeMode) return;
        if (ProcessList.SelectedItem is not ProcessRowViewModel row) return;

        bool expand = e.Key == Key.Right;
        if (vm.SetExpanded(row, expand))
            e.Handled = true;
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            if (ProcessList.SelectedItem is ProcessRowViewModel row)
            {
                vm.SelectedRow = row;
                ProcessList.ContextMenu = _rowMenu;
            }
            else
            {
                vm.SelectedRow = null;
                ProcessList.ContextMenu = _blankMenu;
            }
        }

        QueueCommandLineScan();
    }

    private void OnGeneratorStatusChanged(object? sender, EventArgs e)
    {
        if (ProcessList.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
            QueueCommandLineScan();
    }

    /// <summary>
    /// 把同一轮布局里的多次滚动/选中事件合并成一次扫描。
    /// 扫描只遍历已经生成的容器（虚拟化下通常 20–40 个），不会碰全量行。
    /// </summary>
    private void QueueCommandLineScan()
    {
        if (_commandLineScanQueued) return;
        _commandLineScanQueued = true;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _commandLineScanQueued = false;
                RequestVisibleCommandLines();
            }));
    }

    private void RequestVisibleCommandLines()
    {
        if (DataContext is not MainViewModel vm) return;

        int count = ProcessList.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (ProcessList.ItemContainerGenerator.ContainerFromIndex(i) is not ListViewItem item)
                continue;

            if (!item.IsVisible)
                continue;

            if (item.DataContext is ProcessRowViewModel row)
                vm.RequestCommandLine(row);
        }

        // 选中行即使刚滚出视口，也按需求加载
        if (ProcessList.SelectedItem is ProcessRowViewModel selected)
            vm.RequestCommandLine(selected);
    }

    /// <summary>
    /// 首帧后打一条诊断：如果 realizedRows 接近 items，说明虚拟化没生效，
    /// 那 500+ 行的布局成本和树模式都会是灾难。
    /// </summary>
    public void LogUiDiagnostics()
    {
        int containers = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(ProcessList);

        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            if (node is ListViewItem) containers++;

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }

        Log.WriteLine(
            $"[ui] items={ProcessList.Items.Count} realizedRows={containers} " +
            $"canContentScroll={ScrollViewer.GetCanContentScroll(ProcessList)} " +
            $"isVirtualizing={VirtualizingPanel.GetIsVirtualizing(ProcessList)} " +
            $"scrollUnit={VirtualizingPanel.GetScrollUnit(ProcessList)}");
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed) return typed;

            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}
