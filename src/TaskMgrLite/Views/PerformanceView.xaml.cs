using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TaskMgrLite.ViewModels;

namespace TaskMgrLite.Views;

/// <summary>
/// 性能页。DataContext 由 MainWindow 注入 PerformanceViewModel；
/// 这里只做生命周期接线，不写采集逻辑。
/// </summary>
public partial class PerformanceView : UserControl
{
    private PerformanceViewModel? _vm;

    public PerformanceView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += OnDataContextChanged;
    }

    // 关键：Collapsed 的元素也会触发 Loaded，所以不能无条件 Activate；
    // 只有真正可见时才启动 1Hz 采集线程。
    private void OnLoaded(object sender, RoutedEventArgs e)
        => SyncActivation();

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => (DataContext as PerformanceViewModel)?.Deactivate();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => SyncActivation();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
            _vm.ChartUpdated -= OnChartUpdated;

        if (e.OldValue is PerformanceViewModel oldVm)
            oldVm.Deactivate();

        _vm = e.NewValue as PerformanceViewModel;

        if (_vm is not null)
        {
            _vm.ChartUpdated += OnChartUpdated;
            AttachPoints(_vm);
        }

        SyncActivation();
    }

    private void AttachPoints(PerformanceViewModel vm)
    {
        CpuLine.Points = vm.CpuPoints;
        DiskReadLine.Points = vm.DiskReadPoints;
        DiskWriteLine.Points = vm.DiskWritePoints;
        NetworkRxLine.Points = vm.NetworkRxPoints;
        NetworkTxLine.Points = vm.NetworkTxPoints;
        GpuLine.Points = vm.GpuUsagePoints;
    }

    private void OnChartUpdated()
    {
        if (_vm is null)
            return;

        // 先更新集合，再让 Shape 丢弃缓存的 geometry。
        AttachPoints(_vm);

        CpuLine.InvalidateMeasure();
        DiskReadLine.InvalidateMeasure();
        DiskWriteLine.InvalidateMeasure();
        NetworkRxLine.InvalidateMeasure();
        NetworkTxLine.InvalidateMeasure();
        GpuLine.InvalidateMeasure();
    }

    private void SyncActivation()
    {
        if (DataContext is not PerformanceViewModel vm)
            return;

        if (IsLoaded && IsVisible)
            vm.Activate();
        else
            vm.Deactivate();
    }
}
