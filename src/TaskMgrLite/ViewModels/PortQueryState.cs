using System.Collections.ObjectModel;
using TaskMgrLite.Core;

namespace TaskMgrLite.ViewModels;

/// <summary>
/// 当前选中行的端口查询状态。Version + CurrentKey 是防串号的唯一事实：
/// 切换选中行会 Reset，所有在途结果在 Apply 时被拒绝。
/// </summary>
public sealed class PortQueryState : ObservableObject
{
    private string _statusText = "未加载";
    private bool _isBusy;

    public ObservableCollection<PortEntry> Entries { get; } = new();

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value))
                return;

            OnPropertyChanged(nameof(QueryButtonText));
        }
    }

    public string QueryButtonText => IsBusy ? "查询中..." : "查询端口";

    public ProcKey? CurrentKey { get; private set; }
    public PortQueryResult? CachedResult { get; private set; }
    public int Version { get; private set; }

    /// <summary>切换选中行 / 清空详情时调用；所有在途结果立即失效。</summary>
    public void Reset(ProcKey? key)
    {
        Version++;
        CurrentKey = key;
        IsBusy = false;
        StatusText = "未加载";
        CachedResult = null;
        Entries.Clear();
    }

    public int Begin()
    {
        Version++;
        IsBusy = true;
        StatusText = "查询中…";
        CachedResult = null;
        Entries.Clear();
        return Version;
    }

    /// <summary>只有当前 selection 的当前版本才能落结果；旧结果直接丢弃。</summary>
    public bool Apply(ProcKey key, int version, PortQueryResult result)
    {
        if (version != Version || CurrentKey != key)
            return false;

        CachedResult = result;
        Entries.Clear();
        foreach (PortEntry entry in result.Entries)
            Entries.Add(entry);

        StatusText = result.DisplayText;
        IsBusy = false;
        return true;
    }
}
