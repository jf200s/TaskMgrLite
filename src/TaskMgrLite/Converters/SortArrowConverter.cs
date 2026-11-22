using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;

namespace TaskMgrLite.Converters;

/// <summary>
/// 列头排序箭头：列 key 与当前排序 key 一致、且处于平铺模式时，
/// 升序返回 ▲，降序返回 ▼；树形挂起或没有排序时返回空串。
/// </summary>
public sealed class SortArrowConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4)
            return string.Empty;

        if (values[0] is not string key)
            return string.Empty;

        if (values[1] is not string sortKey ||
            !string.Equals(key, sortKey, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // 树形下排序只是挂起，箭头不显示；状态栏会说明排序已保留。
        if (values[3] is true)
            return string.Empty;

        return values[2] is ListSortDirection.Descending ? "▼" : "▲";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
