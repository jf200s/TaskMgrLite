using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using TaskMgrLite.Core;

namespace TaskMgrLite.ViewModels;

public enum MainSection
{
    Processes,
    Network
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string[] FakeNames =
    {
        "chrome.exe", "Code.exe", "svchost.exe", "explorer.exe", "msedge.exe",
        "Teams.exe", "OUTLOOK.EXE", "conhost.exe", "SearchHost.exe", "RuntimeBroker.exe",
        "dwm.exe", "TaskMgrLite.exe", "python.exe", "node.exe", "git.exe",
        "WindowsTerminal.exe", "SnippingTool.exe", "OneDrive.exe", "MsMpEng.exe", "Audiodg.exe"
    };

    private const double BytesToMb = 1024.0 * 1024.0;

    private readonly DispatcherTimer _fakeTick;
    private readonly DispatcherTimer _statsTimer;
    private readonly bool _statsEnabled;
    private readonly bool _probeExit;
    private readonly Random _random = new(42);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly uint _selfPid = (uint)Environment.ProcessId;
    private readonly Dispatcher _dispatcher;

    private ProcessCollector? _collector;
    private ProcessEnumerator? _statsEnumerator;
    private ProcessSample? _selfSample;

    // ---- 数据层：所有活着的进程 ----
    private readonly Dictionary<ProcKey, ProcessRowViewModel> _rowsByKey = new(1024);
    private readonly HashSet<ProcKey> _aliveKeys = new(1024);
    private readonly List<ProcKey> _deadKeys = new(64);
    private NodeRef[] _nodes = new NodeRef[1024];
    private int _nodeCount;

    // ---- 视图层：可见序列 + 折叠状态 ----
    private readonly TreeFlattener _flattener = new();
    private readonly List<FlatNode> _desired = new(1024);
    private readonly List<ProcKey> _desiredKeys = new(1024);
    private readonly HashSet<ProcKey> _collapsed = new(256);

    // 当前快照里“至少有一个合法子节点”的进程 key。
    // 由 TreeFlattener 在展平时顺手填好，用来释放“最后一个子进程已退出”的折叠状态。
    private readonly HashSet<ProcKey> _parentsWithChildren = new(256);

    private readonly HashSet<ProcKey> _syncScratch = new(1024);
    private readonly List<ProcessRowViewModel> _pendingAdds = new(1024);

    // ---- Round 3：命令行懒加载 + 进程结束 ----
    private readonly CommandLineService _commandLines;
    private readonly ProcessDetailsService _details;
    private readonly List<ProcKey> _killPlan = new(1024);
    private ProcessRowViewModel? _selectedRow;
    private bool _disposed;

    // 缓存委托：热路径上不产生闭包分配
    private readonly Predicate<ProcKey> _isDead;
    private readonly Predicate<ProcKey> _hasNoChildren;
    private readonly Func<ProcessRowViewModel, ProcKey> _keyOf;
    private readonly Func<ProcKey, ProcessRowViewModel> _resolveRow;

    private ProcessViewMode _viewMode = ProcessViewMode.Tree;

    // 顶层视图：进程 / 网络端口。切换只影响可见性和端口轮询，不动进程集合。
    private MainSection _section = MainSection.Processes;
    private NetworkViewModel? _network;
    private IReadOnlyDictionary<uint, string>? _processNames;
    private long _processNamesSequence = -1;
    private long _lastProcessSequence;

    // 列排序状态。属于视图层但不属于某个模式：切到树形时临时挂起（树序必须保持父子相邻），
    // 切回平铺时原样恢复。挂在 VM 上，模式切换和提权恢复共用同一份事实。
    private SortState _sort = SortState.Empty;

    private int _uiPending;
    private bool _firstApplied;
    private long _lastAllocatedBytes = -1;

    private readonly DispatcherTimer _searchDebounce;
    private string _searchText = string.Empty;
    private string _appliedSearchText = string.Empty;
    private string _statusText = "就绪";
    private readonly string _modeText;

    public MainViewModel(bool useRealData, bool stats, bool probeExit, UiState? restore = null)
    {
        UseRealData = useRealData;
        _statsEnabled = stats;
        _probeExit = probeExit;
        _modeText = useRealData ? "真实数据 · 1Hz 采集线程" : "假数据 · 400 行（--fake）";
        _dispatcher = Dispatcher.CurrentDispatcher;

        // 提权重启恢复：直接初始化字段，不走 SearchText setter 的 150ms 防抖。
        if (restore is UiState state)
        {
            UiState normalized = state.Normalize();
            _searchText = normalized.SearchText;
            _appliedSearchText = normalized.SearchText;
            _viewMode = normalized.ViewMode;
            _sort = new SortState(normalized.SortKey, normalized.SortDirection);

            foreach (ProcKey key in normalized.Collapsed)
                _collapsed.Add(key);
        }

        // 只分配一次，之后每帧复用
        _isDead = k => !_aliveKeys.Contains(k);
        _hasNoChildren = k => !_parentsWithChildren.Contains(k);
        _keyOf = static r => r.KeyStruct;
        _resolveRow = k => _rowsByKey[k];

        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = FilterRow;

        _commandLines = new CommandLineService(
            _dispatcher,
            isAlive: k => _rowsByKey.ContainsKey(k),
            onResolved: OnCommandLineResolved);

        _details = new ProcessDetailsService(
            _dispatcher,
            isAlive: k => _rowsByKey.ContainsKey(k),
            onResolved: OnDetailsResolved);

        ShowProcessesCommand = new RelayCommand(ShowProcesses);
        ShowNetworkCommand = new RelayCommand(ShowNetwork);

        _fakeTick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _fakeTick.Tick += (_, _) => MutateFakeRows();

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _statsTimer.Tick += OnStatsTick;

        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _searchDebounce.Tick += OnSearchDebounceTick;
    }

    public BulkObservableCollection<ProcessRowViewModel> Rows { get; } = new();
    public ICollectionView View { get; }
    public bool UseRealData { get; }
    public string ModeText => _modeText;

    public bool IsTreeMode => _viewMode == ProcessViewMode.Tree;
    public string ModeLabel => IsTreeMode ? "切到平铺" : "切到树形";

    // 列头排序箭头用；树形下箭头保留但变暗，表示排序已挂起。
    public string? SortKey => _sort.Key;
    public ListSortDirection SortDirection => _sort.Direction;
    public double SortArrowOpacity => IsTreeMode ? 0.3 : 1.0;

    public NetworkViewModel Network => _network ??= new NetworkViewModel(_dispatcher, GetProcessNameMap);
    public RelayCommand ShowProcessesCommand { get; }
    public RelayCommand ShowNetworkCommand { get; }

    public bool IsProcessView => _section == MainSection.Processes;
    public bool IsNetworkView => _section == MainSection.Network;

    public ProcessRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value)) return;

            OnPropertyChanged(nameof(HasSelection));

            if (value is null) return;

            ProcKey key = value.KeyStruct;
            if (!_rowsByKey.ContainsKey(key)) return;

            // 假数据行的 PID 是虚构的，绝不能拿它去 OpenProcess/读命令行。
            if (!ShouldLoadNative(UseRealData)) return;

            // 选中是唯一的详情触发点：命令行走已有服务，路径/用户走详情服务。
            _commandLines.Request(key);
            _details.Request(key);

            if (string.IsNullOrEmpty(value.CommandLine))
                value.CommandLine = "读取中…";

            if (string.IsNullOrEmpty(value.ImagePath))
                value.ImagePath = "读取中…";

            if (string.IsNullOrEmpty(value.UserName))
                value.UserName = "读取中…";
        }
    }

    public bool HasSelection => _selectedRow is not null;

    /// <summary>只有真实数据模式才允许把 PID 发给内核读取命令/路径/用户。</summary>
    internal static bool ShouldLoadNative(bool useRealData) => useRealData;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;

            // 打字期间只重置计时器，不碰集合视图；
            // 150ms 内没有新输入才真正 SetFlatMode + Refresh。
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();

        if (string.Equals(_appliedSearchText, _searchText, StringComparison.Ordinal))
            return;

        _appliedSearchText = _searchText;

        // 过滤会把扁平化后的父子行拆散，所以搜索一律切平铺
        if (!string.IsNullOrWhiteSpace(_appliedSearchText))
            SetFlatMode();

        View.Refresh();
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string CountText => IsTreeMode
        ? $"{Rows.Count} / {_rowsByKey.Count} 个进程"
        : $"{Rows.Count} 个进程";

    public void Start()
    {
        if (UseRealData)
        {
            StartCollector();
        }
        else
        {
            LoadFakeRows();
            StartupProbe.MarkDataReady();
            _fakeTick.Start();
        }

        if (_statsEnabled)
        {
            Log.WriteLine("[stats] 采样开始：专用工作集 / 工作集 / 私有提交 / Gen2 / 句柄 / 分配速率");
            _statsTimer.Start();
        }

        if (_probeExit)
        {
            var quit = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(3000)
            };
            quit.Tick += (_, _) =>
            {
                quit.Stop();
                Application.Current.Shutdown();
            };
            quit.Start();
        }
    }

    // ---------------- 采集线程 ----------------

    private void StartCollector()
    {
        _collector = new ProcessCollector(logStats: _statsEnabled);
        _collector.SnapshotReady += OnSnapshotReady;
        _collector.Start();
    }

    private void OnSnapshotReady(ProcessSnapshot snapshot)
    {
        if (Interlocked.CompareExchange(ref _uiPending, 1, 0) != 0)
            return;

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(PullSnapshot));
        }
        catch
        {
            // snapshot 仍归 SnapshotSlot 所有。这里只能让槽位丢弃当前最新快照，
            // 不能直接 snapshot.Release()，否则槽位会留下指向已复用缓冲的悬空引用。
            Interlocked.Exchange(ref _uiPending, 0);
            _collector?.DiscardLatest();
        }
    }

    private void PullSnapshot()
    {
        Interlocked.Exchange(ref _uiPending, 0);

        ProcessSnapshot? snapshot = _collector?.TakeLatest();
        if (snapshot is null) return;

        try
        {
            ApplySnapshot(snapshot);
        }
        finally
        {
            snapshot.Release();
        }
    }

    private void ApplySnapshot(ProcessSnapshot snapshot)
    {
        int count = snapshot.Count;
        if (count == 0)
        {
            StatusText = "采样失败：NTSTATUS 非 0";
            return;
        }

        ProcessSample[] buffer = snapshot.Buffer;
        EnsureNodeCapacity(count);

        _aliveKeys.Clear();
        for (int i = 0; i < count; i++)
        {
            ProcessSample s = buffer[i];
            ProcKey key = new(s.Pid, s.CreateTime);
            _aliveKeys.Add(key);
            _nodes[i] = new NodeRef(key, s.ParentPid);
        }
        _nodeCount = count;
        _lastProcessSequence = snapshot.Sequence;

        // 端口视图可见时，从这一帧不可变快照直接构建 PID → 名称映射。
        // 只在 1Hz 快照应用时做一次；2Hz 端口帧复用同一份字典。
        if (IsNetworkView)
            RebuildProcessNameMap(snapshot);

        // 1) 已退出（含 PID 复用的旧 key）
        _deadKeys.Clear();
        foreach (KeyValuePair<ProcKey, ProcessRowViewModel> kv in _rowsByKey)
        {
            if (!_aliveKeys.Contains(kv.Key))
                _deadKeys.Add(kv.Key);
        }

        for (int i = 0; i < _deadKeys.Count; i++)
        {
            ProcKey key = _deadKeys[i];
            ProcessRowViewModel row = _rowsByKey[key];
            _rowsByKey.Remove(key);
            _commandLines.Forget(key);
            _details.Forget(key);
            Rows.Remove(row);   // 树模式下不可见时是空操作
        }

        if (_selectedRow is not null && !_aliveKeys.Contains(_selectedRow.KeyStruct))
            SelectedRow = null;

        // 2) 就地更新 / 新增
        for (int i = 0; i < count; i++)
        {
            ProcessSample s = buffer[i];
            ProcKey key = new(s.Pid, s.CreateTime);

            if (_rowsByKey.TryGetValue(key, out ProcessRowViewModel? row))
            {
                row.CpuPercent = s.CpuPercent;
                row.MemoryMb = s.WorkingSetPrivate / BytesToMb;
                row.HandleCount = s.HandleCount;
            }
            else
            {
                _rowsByKey[key] = CreateRow(in s);
            }

            if (s.Pid == _selfPid)
                _selfSample = s;
        }

        // 3) 先剪掉已退出 / 被 PID 复用顶掉的 key。
        //    “最后一个子进程退出”的叶子剪枝要等展平后拿到本轮 parentsWithChildren 再做。
        CollapseState.Prune(_collapsed, _isDead);

        // 4) 重建可见序列（树或平铺）
        RebuildFromCurrentNodes();

        StatusText = IsTreeMode
            ? $"树形 · 可见 {Rows.Count} / 共 {_rowsByKey.Count} · 折叠 {_collapsed.Count}"
              + DescribePendingSort()
            : $"平铺 · {_rowsByKey.Count} 条" + DescribeStoredSort();

        OnPropertyChanged(nameof(CountText));

        if (!_firstApplied)
        {
            _firstApplied = true;
            StartupProbe.MarkDataReady();

            if (_statsEnabled)
            {
                try
                {
                    Process[] procs = Process.GetProcesses();
                    int managed = procs.Length;
                    foreach (Process p in procs) p.Dispose();
                    Log.WriteLine($"[real] first-snapshot rows={_rowsByKey.Count} managed={managed}");
                }
                catch { /* 交叉验证失败不影响主流程 */ }
            }
        }
    }

    // ---------------- Round 3：命令行 / 结束进程 ----------------

    /// <summary>UI 层只在行进入可视区或选中时调用。缓存命中会同步回填。</summary>
    public void RequestCommandLine(ProcessRowViewModel row)
    {
        if (!UseRealData) return;
        _commandLines.Request(row.KeyStruct);
    }

    private void OnCommandLineResolved(ProcKey key, CommandLineResult result)
    {
        if (!_rowsByKey.TryGetValue(key, out ProcessRowViewModel? row))
            return;

        row.CommandLine = result.IsOk && string.IsNullOrEmpty(result.Text)
            ? "（空）"
            : result.DisplayText;
    }

    private void OnDetailsResolved(ProcKey key, ProcessDetailsResult result)
    {
        if (_rowsByKey.TryGetValue(key, out ProcessRowViewModel? row))
        {
            row.ImagePath = result.PathDisplay;
            row.UserName = result.UserDisplay;
        }
    }

    /// <summary>
    /// “打开文件所在位置 / 复制路径”用。优先缓存；没有就后台读一次并回填。
    /// 详情面板的常规加载走 ProcessDetailsService，不会在 1Hz 路径上调用这里。
    /// </summary>
    public async Task<ProcessDetailsResult> GetDetailsAsync(ProcessRowViewModel row)
    {
        ProcKey key = row.KeyStruct;

        if (!UseRealData)
        {
            return new ProcessDetailsResult(
                string.IsNullOrEmpty(row.ImagePath) ? null : row.ImagePath,
                ProcessInfoStatus.Ok,
                string.IsNullOrEmpty(row.UserName) ? null : row.UserName,
                ProcessInfoStatus.Ok);
        }

        if (!_rowsByKey.ContainsKey(key))
            return new ProcessDetailsResult(null, ProcessInfoStatus.Exited, null, ProcessInfoStatus.Exited);

        if (_details.TryGetCached(key, out ProcessDetailsResult cached))
            return cached;

        ProcessDetailsResult result = await Task.Run(() => ProcessDetailsReader.Read(key));
        _details.Remember(key, result);
        return result;
    }

    /// <summary>空白处右键“刷新”：唤醒采集线程立即采一次，不改变 1Hz 节拍。</summary>
    public void RequestRefresh()
    {
        if (UseRealData)
        {
            _collector?.RequestRefresh();
            StatusText = "刷新请求已发送";
        }
        else
        {
            LoadFakeRows();
        }
    }

    /// <summary>
    /// 按当前快照生成结束计划并后台执行。
    /// 单杀只包含根；树杀用快照 DFS 后序，后代先于祖先。
    /// 计划在 UI 线程生成并复制，随后 UI 可以继续接收新快照。
    /// </summary>
    public async Task<KillReport> KillAsync(ProcessRowViewModel row, bool tree)
    {
        ProcKey root = row.KeyStruct;

        if (!UseRealData)
            return new KillReport(new[] { new KillOutcome(root, KillStatus.NotSupported, 0) });

        if (!_rowsByKey.TryGetValue(root, out ProcessRowViewModel? current) ||
            !ReferenceEquals(current, row))
        {
            return new KillReport(new[] { new KillOutcome(root, KillStatus.AlreadyExited, 0) });
        }

        _killPlan.Clear();

        if (tree)
        {
            if (!_flattener.TryCollectSubtreePostOrder(_nodes, _nodeCount, root, _killPlan))
                return new KillReport(new[] { new KillOutcome(root, KillStatus.AlreadyExited, 0) });
        }
        else
        {
            _killPlan.Add(root);
        }

        ProcKey[] keys = _killPlan.ToArray();
        KillReport report = await Task.Run(() => ProcessTerminator.KillMany(keys, _selfPid));

        StatusText = $"{(tree ? "结束进程树" : "结束进程")} · 成功 {report.Succeeded} / 失败 {report.Failed}";
        return report;
    }

    /// <summary>提权重启前抓取最小 UI 状态；包含搜索词、模式、折叠集合和列排序。</summary>
    public UiState CaptureUiState()
    {
        var collapsed = new ProcKey[_collapsed.Count];
        _collapsed.CopyTo(collapsed);
        return new UiState(
            _searchText,
            _viewMode,
            collapsed,
            DateTime.UtcNow,
            _sort.Key,
            _sort.Direction);
    }

    private void ShowProcesses()
    {
        if (_section == MainSection.Processes)
            return;

        _section = MainSection.Processes;
        _network?.Deactivate();

        OnPropertyChanged(nameof(IsProcessView));
        OnPropertyChanged(nameof(IsNetworkView));
    }

    private void ShowNetwork()
    {
        if (_section == MainSection.Network)
            return;

        _section = MainSection.Network;

        if (UseRealData)
            Network.Activate();
        else
            Network.ShowUnavailable("假数据模式不读取真实端口");

        OnPropertyChanged(nameof(IsProcessView));
        OnPropertyChanged(nameof(IsNetworkView));
    }

    /// <summary>
    /// 端口视图的 PID → 名称映射。只做内存复制，不调用 OpenProcess 或任何 P/Invoke。
    /// 2Hz 端口帧调用这里时，优先复用 1Hz 进程快照刚构建的映射；只有映射缺失
    /// 或进程快照序号变了才从当前行重建一次。
    /// </summary>
    private IReadOnlyDictionary<uint, string> GetProcessNameMap()
    {
        if (_processNames is not null && _processNamesSequence == _lastProcessSequence)
            return _processNames;

        var map = new Dictionary<uint, string>(_rowsByKey.Count);
        foreach (KeyValuePair<ProcKey, ProcessRowViewModel> kv in _rowsByKey)
            map[kv.Key.Pid] = kv.Value.Name;

        _processNames = map;
        _processNamesSequence = _lastProcessSequence;
        return map;
    }

    /// <summary>从当前不可变进程快照直接构建 PID → 名称映射，只在端口视图可见时调用。</summary>
    private void RebuildProcessNameMap(ProcessSnapshot snapshot)
    {
        var map = new Dictionary<uint, string>(snapshot.Count);
        ProcessSample[] buffer = snapshot.Buffer;

        for (int i = 0; i < snapshot.Count; i++)
            map[buffer[i].Pid] = buffer[i].Name;

        _processNames = map;
        _processNamesSequence = snapshot.Sequence;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _network?.Dispose();
        _fakeTick.Stop();
        _statsTimer.Stop();
        _searchDebounce.Stop();
        _commandLines.Dispose();
        _details.Dispose();
        _collector?.Dispose();
        _statsEnumerator?.Dispose();
    }

    // ---------------- 树 / 平铺 ----------------

    /// <summary>
    /// 从当前节点集重建可见序列。只依赖 _nodes/_collapsed/_viewMode，
    /// 不依赖快照，所以折叠、切模式、排序都能立刻生效。
    ///
    /// 树模式下展平会顺便填 _parentsWithChildren；展平完成后立即释放
    /// “没有子节点”的折叠 key。这一步不改变本轮可见行（叶子没有子行可藏），
    /// 只保证下一次有新子进程出生时按默认展开处理。
    /// </summary>
    private void RebuildFromCurrentNodes()
    {
        _desired.Clear();

        if (_viewMode == ProcessViewMode.Tree)
        {
            _flattener.Flatten(_nodes, _nodeCount, _collapsed, _desired, _parentsWithChildren);
            CollapseState.Prune(_collapsed, _hasNoChildren);
        }
        else
        {
            for (int i = 0; i < _nodeCount; i++)
                _desired.Add(new FlatNode(_nodes[i].Key, 0, false));
        }

        _desiredKeys.Clear();
        for (int i = 0; i < _desired.Count; i++)
            _desiredKeys.Add(_desired[i].Key);

        // 批量 ReplaceAll 会触发 Reset，ListView 可能清掉选中项。
        // 如果原来的选中行仍在新可见序列里，重建结束后把它补回来。
        ProcessRowViewModel? keepSelected = _selectedRow;
        bool restoreSelection = keepSelected is not null &&
                                _desiredKeys.Contains(keepSelected.KeyStruct);

        if (Rows.Count == 0 && _desired.Count > 0)
        {
            // 首屏一次 Reset，避免 500 次逐条通知
            _pendingAdds.Clear();
            for (int i = 0; i < _desired.Count; i++)
                _pendingAdds.Add(_rowsByKey[_desired[i].Key]);
            Rows.AddRangeReset(_pendingAdds);

            // 恢复模式下首屏直接按保存的排序显示；树形/无排序时是空操作。
            ApplyStoredSort();
        }
        else
        {
            VisibleListSync.Apply(
                Rows,
                _desiredKeys,
                _keyOf,
                _resolveRow,
                _syncScratch,
                bulkThreshold: 32,
                bulkBuffer: _pendingAdds,
                replaceAll: Rows.ReplaceAll);
        }

        for (int i = 0; i < _desired.Count; i++)
        {
            ProcessRowViewModel row = Rows[i];
            FlatNode node = _desired[i];
            row.Depth = node.Depth;
            row.HasChildren = node.HasChildren;
            row.IsExpanded = !_collapsed.Contains(node.Key);
        }

        if (restoreSelection && !ReferenceEquals(_selectedRow, keepSelected))
            SelectedRow = keepSelected;
    }

    public void ToggleExpanded(ProcessRowViewModel row)
    {
        if (_viewMode != ProcessViewMode.Tree) return;

        ProcKey key = row.KeyStruct;
        if (!_rowsByKey.ContainsKey(key)) return;

        // 当前已折叠 → 目标展开；当前已展开 → 目标折叠。
        SetExpanded(row, expand: _collapsed.Contains(key));
    }

    /// <summary>
    /// 显式设置展开状态。返回 false 表示没有状态变化（叶子、旧行、已经处于目标状态）。
    /// 折叠当前选中行的祖先时，选中迁移到被折叠的节点，详情面板不清空。
    /// </summary>
    public bool SetExpanded(ProcessRowViewModel row, bool expand)
    {
        if (_viewMode != ProcessViewMode.Tree) return false;

        ProcKey key = row.KeyStruct;

        // 点击可能落在上一帧的行上：快照刚把进程删掉、或刚删掉最后一个子进程，
        // WPF 还没来得及移除容器。这里必须用当前快照的权威集合再校验一次。
        if (!_rowsByKey.ContainsKey(key) || !_parentsWithChildren.Contains(key))
            return false;

        bool migrate = !expand &&
                       TreeSelection.ShouldMoveSelection(_desired, key, _selectedRow?.KeyStruct);

        if (!CollapseState.Set(_collapsed, key, expand, _parentsWithChildren))
            return false;

        RebuildFromCurrentNodes();
        OnPropertyChanged(nameof(CountText));

        // 先重建再设选中：ListView 删除被隐藏的行时会把 TwoWay 绑定清成 null。
        if (migrate)
            SelectedRow = row;

        StatusText = $"树形 · 可见 {Rows.Count} / 共 {_rowsByKey.Count} · 折叠 {_collapsed.Count}"
                     + DescribePendingSort();
        return true;
    }

    public void SetTreeMode()
    {
        if (_viewMode == ProcessViewMode.Tree) return;

        _viewMode = ProcessViewMode.Tree;

        // 树序和列排序互斥：树形下父子必须相邻，所以只挂起排序、不清除。
        // 排序条件保留在 _sort，切回平铺时恢复。
        if (View is ListCollectionView view && view.SortDescriptions.Count > 0)
        {
            using (view.DeferRefresh())
                view.SortDescriptions.Clear();
        }

        RebuildFromCurrentNodes();

        OnPropertyChanged(nameof(IsTreeMode));
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(SortArrowOpacity));
        OnPropertyChanged(nameof(CountText));

        StatusText = $"树形 · 可见 {Rows.Count} / 共 {_rowsByKey.Count} · 折叠 {_collapsed.Count}"
                     + DescribePendingSort();
    }

    public void SetFlatMode()
    {
        if (_viewMode == ProcessViewMode.Flat) return;

        _viewMode = ProcessViewMode.Flat;
        RebuildFromCurrentNodes();

        // 回到平铺时恢复切换前的排序，避免“切一趟树形排序就丢了”。
        ApplyStoredSort();

        OnPropertyChanged(nameof(IsTreeMode));
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(SortArrowOpacity));
        OnPropertyChanged(nameof(CountText));

        StatusText = $"平铺 · {_rowsByKey.Count} 条" + DescribeStoredSort();
    }

    public void ApplySort(SortState sort)
    {
        // 先落排序字段，再切模式：SetFlatMode 内部的 ApplyStoredSort 会直接
        // 用上新排序，避免"先按旧序排一遍、再按新序排一遍"的重复刷新。
        _sort = sort;
        OnPropertyChanged(nameof(SortKey));
        OnPropertyChanged(nameof(SortDirection));

        if (_viewMode == ProcessViewMode.Flat)
        {
            ApplyStoredSort();
        }
        else
        {
            SetFlatMode();   // 排序即平铺
        }

        StatusText = $"平铺 · {_rowsByKey.Count} 条" + DescribeStoredSort();
    }

    /// <summary>
    /// 列头点击：同一列在升序/降序之间翻转，换一列则从升序开始。
    /// 树形下点列头会先切平铺（排序即平铺），再应用排序。
    /// </summary>
    public void ToggleSort(string propertyName) => ApplySort(_sort.Next(propertyName));

    /// <summary>把当前挂起的排序条件应用到视图。树形或无排序时是空操作。</summary>
    private void ApplyStoredSort()
    {
        if (!_sort.ShouldApply(_viewMode)) return;
        if (View is not ListCollectionView view) return;

        string key = _sort.Key!;

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(key, _sort.Direction));
        }
    }

    /// <summary>树形下排序被挂起时，在状态栏说清楚它还在，只是没生效。</summary>
    private string DescribePendingSort() =>
        _sort.IsSuspended(_viewMode)
            ? $"（按 {SortState.ColumnName(_sort.Key!)} 的排序已保留，切回平铺恢复）"
            : string.Empty;

    /// <summary>平铺下显示当前生效的排序，让用户知道排序没有丢。</summary>
    private string DescribeStoredSort() =>
        !_sort.HasSort
            ? string.Empty
            : $" · 按 {SortState.ColumnName(_sort.Key!)} {(_sort.Direction == ListSortDirection.Ascending ? "升序" : "降序")}";

    private void EnsureNodeCapacity(int count)
    {
        if (_nodes.Length < count)
            Array.Resize(ref _nodes, Math.Max(count, _nodes.Length * 2));
    }

    private static ProcessRowViewModel CreateRow(in ProcessSample s) => new(
        pid: s.Pid,
        parentPid: s.ParentPid,
        name: s.Name,
        depth: 0,
        createTime: s.CreateTime,
        startTime: ProcessTime.FromFileTime(s.CreateTime))
    {
        CpuPercent = s.CpuPercent,
        MemoryMb = s.WorkingSetPrivate / BytesToMb,
        HandleCount = s.HandleCount,
        CommandLine = string.Empty   // 进入可视区/被选中时由 CommandLineService 懒加载
    };

    // ---------------- 假数据（UI 压测用） ----------------

    private void LoadFakeRows()
    {
        const int total = 400;
        EnsureNodeCapacity(total);

        SelectedRow = null;
        _rowsByKey.Clear();
        _collapsed.Clear();

        for (int i = 0; i < total; i++)
        {
            string name = FakeNames[_random.Next(FakeNames.Length)];
            uint pid = (uint)(1000 + i * 4);

            int parentIndex = i < 5 ? -1 : Math.Max(0, i - _random.Next(1, 6));
            uint parentPid = parentIndex < 0 ? 0u : (uint)(1000 + parentIndex * 4);

            long createTime = DateTime.UtcNow.Ticks + i;

            var row = new ProcessRowViewModel(
                pid, parentPid, name, 0, createTime, DateTime.Now.AddMinutes(-i))
            {
                CpuPercent = Math.Round(_random.NextDouble() * 8.0, 1),
                MemoryMb = Math.Round(8 + _random.NextDouble() * 420, 1),
                HandleCount = (uint)_random.Next(40, 1800),
                CommandLine = $"\"C:\\Program Files\\{name}\" --spike-id {i}",
                ImagePath = $@"C:\Fake\{name}",
                UserName = Environment.UserName
            };

            ProcKey key = new(pid, createTime);
            _rowsByKey[key] = row;
            _nodes[i] = new NodeRef(key, parentPid);
        }

        _nodeCount = total;
        RebuildFromCurrentNodes();
        OnPropertyChanged(nameof(CountText));

        StatusText = $"假数据 400 行 · 树形 · 每秒随机改写 120 行属性（--fake）";
    }

    private void MutateFakeRows()
    {
        if (Rows.Count == 0) return;

        int updates = Math.Min(120, Rows.Count);
        for (int i = 0; i < updates; i++)
        {
            ProcessRowViewModel row = Rows[_random.Next(Rows.Count)];
            row.CpuPercent = Math.Round(_random.NextDouble() * 12.0, 1);
            row.MemoryMb = Math.Max(0.5, Math.Round(row.MemoryMb + (_random.NextDouble() - 0.5) * 3, 1));
        }
    }

    // ---------------- 统计 ----------------

    private void OnStatsTick(object? sender, EventArgs e)
    {
        if (!UseRealData)
            RefreshSelfSampleFallback();

        SelfMetrics m = SelfMetricsReader.Capture(_selfSample);
        double seconds = _clock.Elapsed.TotalSeconds;

        double allocRate = 0;
        if (_lastAllocatedBytes >= 0)
            allocRate = (m.TotalAllocatedBytes - _lastAllocatedBytes) / BytesToMb / 2.0;
        _lastAllocatedBytes = m.TotalAllocatedBytes;

        Log.WriteLine(
            $"[stats] t={seconds:F0}s " +
            $"专用工作集={m.PrivateWorkingSetMb:F1}MB " +
            $"工作集={m.WorkingSetMb:F1}MB " +
            $"私有提交={m.PrivateCommitMb:F1}MB " +
            $"Gen0={m.Gen0} Gen1={m.Gen1} Gen2={m.Gen2} " +
            $"句柄={m.HandleCount} " +
            $"托管堆={m.ManagedHeapMb:F1}MB " +
            $"分配={allocRate:F2}MB/s " +
            $"可见={Rows.Count} 全量={_rowsByKey.Count} 折叠={_collapsed.Count}");
    }

    private void RefreshSelfSampleFallback()
    {
        _statsEnumerator ??= new ProcessEnumerator();
        if (!_statsEnumerator.SampleRaw(out RawProcessEntry[] raw, out int count, out _))
            return;

        for (int i = 0; i < count; i++)
        {
            if (raw[i].Pid != _selfPid) continue;

            RawProcessEntry e = raw[i];
            _selfSample = new ProcessSample(
                e.Pid, e.ParentPid, e.CreateTime, e.UserTime, e.KernelTime,
                e.WorkingSetPrivate, e.WorkingSet, e.HandleCount, e.ThreadCount,
                CpuPercent: -1, Name: string.Empty);
            return;
        }
    }

    // ---------------- 过滤 ----------------

    private bool FilterRow(object obj)
    {
        return obj is ProcessRowViewModel row &&
               ProcessFilter.Matches(row.Name, row.Pid, _appliedSearchText);
    }
}
