using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace TaskMgrLite.ViewModels;

/// <summary>
/// 批量装载集合。
///
/// 注意：ListCollectionView 明确不支持多元素 Add 通知（会抛
/// "Range actions are not supported"），所以这里只能走 Reset。
/// Reset 会让视图重建一次，但只在“从空到满”时使用，没有选中态可丢；
/// 后续每秒新增的几个进程仍走普通 Add。
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void AddRangeReset(List<T> items)
    {
        if (items.Count == 0) return;

        CheckReentrancy();

        foreach (T item in items)
            Items.Add(item);

        RaiseReset();
    }

    /// <summary>
    /// 用一个完整的新序列替换当前内容，只发一次 Reset。
    /// 用于批量展开/折叠，避免几百次 Insert 把 ListCollectionView 拖死。
    /// </summary>
    public void ReplaceAll(IList<T> items)
    {
        CheckReentrancy();

        Items.Clear();
        for (int i = 0; i < items.Count; i++)
            Items.Add(items[i]);

        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
