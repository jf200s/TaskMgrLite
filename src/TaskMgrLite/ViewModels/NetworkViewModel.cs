using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using TaskMgrLite.Core;

namespace TaskMgrLite.ViewModels;

/// <summary>
/// 独立的全系统端口视图模型。
///
/// 线程模型：
///   - 轮询循环只做「后台读全表 → 构造不可变 PortSnapshot → 投递到 UI」；
///   - 单循环串行，上一帧没读完就不会发起下一帧，天然不会堆积；
///   - UI 线程只做 Diff、属性更新和搜索过滤，不碰 P/Invoke；
///   - 与 1Hz 进程采集完全独立，不共用线程、快照槽位或定时器。
///
/// 生命周期：Activate 立即读一帧，之后每 500ms 一帧；Deactivate 取消循环并清空
/// 行、索引和搜索状态，端口数组交给 GC。
/// </summary>
public sealed class NetworkViewModel : ObservableObject, IDisposable
{
    private const int PollIntervalMs = 500;

    private static readonly IReadOnlyDictionary<uint, string> EmptyNames =
        new Dictionary<uint, string>();

    private readonly Dispatcher _dispatcher;
    private readonly Func<IReadOnlyDictionary<uint, string>> _processNamesProvider;
    private readonly Dictionary<PortKey, NetworkPortRowViewModel> _index = new();
    private readonly HashSet<PortKey> _desiredKeys = new();
    private readonly DispatcherTimer _searchDebounce;

    private CancellationTokenSource? _cts;
    private int _session;
    private long _sequence;
    private long _appliedSequence;
    private bool _isActive;
    private bool _disposed;

    private string _statusText = "未加载";
    private string _searchText = string.Empty;
    private string _appliedSearchText = string.Empty;
    private string? _sortKey;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public NetworkViewModel(
        Dispatcher dispatcher,
        Func<IReadOnlyDictionary<uint, string>> processNamesProvider)
    {
        _dispatcher = dispatcher;
        _processNamesProvider = processNamesProvider;

        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = FilterRow;

        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _searchDebounce.Tick += OnSearchDebounceTick;
    }

    public ObservableCollection<NetworkPortRowViewModel> Rows { get; } = new();
    public ICollectionView View { get; }

    public bool IsActive => _isActive;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string CountText => string.IsNullOrEmpty(_appliedSearchText)
        ? $"{Rows.Count} 个端口"
        : $"{CountVisible()} / {Rows.Count} 个端口";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? string.Empty))
                return;

            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
    }

    public string? SortKey
    {
        get => _sortKey;
        private set => Set(ref _sortKey, value);
    }

    public ListSortDirection SortDirection
    {
        get => _sortDirection;
        private set => Set(ref _sortDirection, value);
    }

    public void Activate()
    {
        if (_disposed || _isActive)
            return;

        _isActive = true;
        OnPropertyChanged(nameof(IsActive));

        _appliedSequence = 0;
        StatusText = "正在读取端口…";

        var cts = new CancellationTokenSource();
        _cts = cts;
        int session = ++_session;

        _ = PollLoopAsync(session, cts);
    }

    public void Deactivate()
    {
        if (!_isActive)
            return;

        _isActive = false;
        OnPropertyChanged(nameof(IsActive));

        // 只取消；由轮询循环的 finally 负责 Dispose，避免取消和释放互相踩。
        _session++;
        CancellationTokenSource? cts = _cts;
        _cts = null;

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 轮询循环已经退出并释放了 CTS，取消失败可以忽略。
        }

        _appliedSequence = 0;
        Rows.Clear();
        _index.Clear();
        _desiredKeys.Clear();

        _searchDebounce.Stop();
        _searchText = string.Empty;
        _appliedSearchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));

        StatusText = "未加载";
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>不可用状态（例如 --fake）：不启动轮询，只显示一行说明。</summary>
    public void ShowUnavailable(string message)
    {
        Deactivate();
        StatusText = message;
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>
    /// 按端口 Key 做 UI Diff。只有增/删/移动会触发 CollectionChanged；
    /// 数据不变时这里不会调用任何集合修改方法。
    /// </summary>
    private void ApplySnapshot(PortSnapshot snapshot)
    {
        if (snapshot.Sequence <= _appliedSequence)
            return;

        _appliedSequence = snapshot.Sequence;

        IReadOnlyDictionary<uint, string> names =
            _processNamesProvider() ?? EmptyNames;

        NetworkPortListSync.Apply(
            Rows,
            snapshot.Entries,
            names,
            _index,
            _desiredKeys);

        StatusText = PortQueryResult.Describe(snapshot.Status, snapshot.Count) + " · 2Hz";
        OnPropertyChanged(nameof(CountText));
    }

    private async Task PollLoopAsync(int session, CancellationTokenSource cts)
    {
        CancellationToken ct = cts.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                long started = Stopwatch.GetTimestamp();

                PortQueryResult result;
                try
                {
                    result = await Task.Run(
                        static () => PortReader.ReadAll(), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.WriteLine("[ports] poll failed " + ex);
                    result = PortQueryResult.Error(ex.Message);
                }

                if (ct.IsCancellationRequested)
                    break;

                var snapshot = new PortSnapshot(
                    result.Entries,
                    result.Status,
                    Interlocked.Increment(ref _sequence),
                    DateTime.UtcNow.Ticks);

                await _dispatcher.InvokeAsync(
                    () =>
                    {
                        if (session != _session || !_isActive)
                            return;

                        try
                        {
                            ApplySnapshot(snapshot);
                        }
                        catch (Exception ex)
                        {
                            Log.WriteLine("[ports] apply failed " + ex);
                        }
                    },
                    DispatcherPriority.DataBind);

                long elapsedMs =
                    (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency;
                int wait = PollIntervalMs - (int)elapsedMs;
                if (wait > 0)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Deactivate / Dispose 的正常退出路径。
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>列头排序。只影响 ICollectionView，不改变 Diff 的源顺序。</summary>
    public void ApplySort(string propertyName, ListSortDirection direction)
    {
        if (View is not ListCollectionView view)
            return;

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(propertyName, direction));
        }

        SortKey = propertyName;
        SortDirection = direction;
    }

    /// <summary>列头点击：同一列翻转，换列从升序开始。</summary>
    public void ToggleSort(string propertyName)
    {
        ListSortDirection direction =
            string.Equals(SortKey, propertyName, StringComparison.Ordinal)
                ? (SortDirection == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending)
                : ListSortDirection.Ascending;

        ApplySort(propertyName, direction);
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();

        if (string.Equals(_appliedSearchText, _searchText, StringComparison.Ordinal))
            return;

        _appliedSearchText = _searchText;
        View.Refresh();
        OnPropertyChanged(nameof(CountText));
    }

    private bool FilterRow(object obj)
    {
        if (obj is not NetworkPortRowViewModel row)
            return false;

        string term = _appliedSearchText.Trim();
        if (term.Length == 0)
            return true;

        return Contains(row.Protocol, term)
               || Contains(row.LocalEndpoint, term)
               || Contains(row.RemoteEndpoint, term)
               || Contains(row.State, term)
               || Contains(row.ProcessName, term)
               || Contains(row.PidDisplay, term)
               || Contains(row.LocalPortDisplay, term)
               || Contains(row.RemotePortDisplay, term);
    }

    private static bool Contains(string value, string term) =>
        value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private int CountVisible()
    {
        int count = 0;
        foreach (object _ in View)
            count++;

        return count;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Deactivate();
        _searchDebounce.Tick -= OnSearchDebounceTick;
    }
}
