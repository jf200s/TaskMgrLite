using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using TaskMgrLite.ViewModels;

namespace TaskMgrLite.Views;

/// <summary>
/// 网络端口视图。DataContext 由 MainWindow 绑定为 NetworkViewModel，
/// 这里不设置 DataContext，只处理列头点击排序。
/// </summary>
public partial class NetworkView : UserControl
{
    public NetworkView()
    {
        InitializeComponent();

        // 表头由 GridView 模板生成，挂路由事件比逐列 EventSetter 稳定。
        PortList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Thumb) return;

        GridViewColumnHeader? header = FindAncestor<GridViewColumnHeader>(e.OriginalSource as DependencyObject);
        string? key = SortHeaderInfo.GetKey(header?.Content);
        if (key is null) return;
        if (DataContext is NetworkViewModel vm)
            vm.ToggleSort(key);
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
