using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;

namespace TaskMgrLite.Converters;

/// <summary>
/// 列头排序箭头：列 key 与当前排序 key 一致时，升序返回 ▲，降序返回 ▼。
/// 树形下仍保留箭头，由 Opacity 绑定变暗表示排序已挂起。
/// </summary>
public sealed class SortArrowConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3)
            return string.Empty;

        if (values[0] is not string key)
            return string.Empty;

        if (values[1] is not string sortKey ||
            !string.Equals(key, sortKey, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return values[2] is ListSortDirection.Descending ? "▼" : "▲";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
