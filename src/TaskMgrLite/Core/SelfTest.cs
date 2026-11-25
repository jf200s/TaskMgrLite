using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TaskMgrLite.ViewModels;

namespace TaskMgrLite.Core;

/// <summary>
/// 无依赖的自检套件（--selftest）。只测纯逻辑，不碰 WPF，
/// 这样界面还没接完就能先把状态机跑绿。
/// </summary>
public static class SelfTest
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        _passed = 0;
        _failed = 0;

        Check("pre-order + depth", PreOrder);
        Check("collapse hides subtree", CollapseHidesSubtree);
        Check("collapse keeps the node itself", CollapseKeepsNode);
        Check("child born while parent collapsed", ChildBornWhileCollapsed);
        Check("child born while collapsed, then expand", ChildBornWhileCollapsedThenExpand);
        Check("child dies while parent collapsed", ChildDiesWhileCollapsed);
        Check("parent dies, children become roots", ParentDiesChildrenBecomeRoots);
        Check("pid 0 is never a parent", PidZeroIsNeverParent);
        Check("young parent does not adopt older child", YoungParentDoesNotAdoptOlderChild);
        Check("older parent adopts child", OlderParentAdoptsChild);
        Check("duplicate pid keeps oldest as parent", DuplicatePidKeepsOldest);
        Check("pid reuse does not inherit collapse", PidReuseDoesNotInheritCollapse);
        Check("cycle terminates and emits once", CycleTerminates);
        Check("deep chain does not overflow", DeepChain);
        Check("collapsed set pruned on death", CollapsePrunedOnDeath);
        Check("last child death releases collapse", LastChildDeathReleasesCollapse);
        Check("parent dies while collapsed", ParentDiesWhileCollapsed);
        Check("stale or leaf toggle is rejected", ToggleRejectsStaleOrLeaf);
        Check("selection moves to collapsed ancestor", SelectionMovesToCollapsedAncestor);
        Check("selection stays outside collapsed subtree", SelectionStaysOutsideCollapsedSubtree);
        Check("collapse set supports explicit expand", CollapseSetExplicit);
        Check("leaf explicit expand is rejected", LeafExplicitExpandRejected);
        Check("ui state round-trips", UiStateRoundTrip);
        Check("ui state rejects stale", UiStateRejectsStale);
        Check("ui state rejects bad version", UiStateRejectsBadVersion);
        Check("ui state rejects malformed and oversized", UiStateRejectsMalformedAndOversized);
        Check("ui state rejects path outside root", UiStateRejectsPathOutsideRoot);
        Check("ui state accepts legacy json", UiStateAcceptsLegacyJson);
        Check("ui state drops unknown sort key", UiStateDropsUnknownSortKey);
        Check("elevation args preserve flags and path", ElevationArguments);
        Check("restore forces flat with search", UiStateNormalizeForcesFlat);
        Check("sort next flips same key", SortNextFlipsSameKey);
        Check("sort next resets on new key", SortNextResetsOnNewKey);
        Check("sort state suspends in tree and resumes in flat", SortStateModeGate);
        Check("sort first numeric click is descending", SortFirstNumericDescending);
        Check("sort first text click is ascending", SortFirstTextAscending);
        Check("sort command line is disabled", SortCommandLineDisabled);
        Check("network port diff is silent when unchanged", NetworkPortDiffSilentWhenUnchanged);
        Check("network port diff updates state without collection event", NetworkPortDiffStateOnly);
        Check("network port diff inserts and removes", NetworkPortDiffInsertRemove);
        Check("network port diff moves reordered rows", NetworkPortDiffReorder);
        Check("ok cache never expires", OkCacheNeverExpires);
        Check("negative cache expires at 30s", NegativeCacheExpiresAt30s);
        Check("error cache expires at 30s", ErrorCacheExpiresAt30s);
        Check("terminal cache statuses do not expire", TerminalCacheStatusesDoNotExpire);
        Check("cache timestamp refreshes", CacheTimestampRefreshes);
        Check("command line cache keeps lru bound", CommandLineCacheKeepsLruBound);
        Check("collapsed set bounded under churn", CollapseBoundedUnderChurn);
        Check("visible list sync inserts and moves", VisibleListSyncWorks);
        Check("visible list sync random churn", VisibleListSyncRandomChurn);
        Check("flatten steady state allocates nothing", FlattenSteadyStateDoesNotAllocate);
        Check("random forest matches oracle", RandomForestMatchesOracle);
        Check("tree kill plan is post-order", TreeKillPlanIsPostOrder);
        Check("tree kill plan rejects stale roots", TreeKillPlanRejectsStaleRoots);
        Check("tree kill plan handles cycles", TreeKillPlanHandlesCycles);
        Check("kill guards block system and self", KillGuardsBlockSystemAndSelf);
        Check("kill error classification", KillErrorClassification);
        Check("kill report summarizes failures", KillReportSummarizesFailures);
        Check("lru cache evicts least recently used", LruCacheEvictsLeastRecentlyUsed);
        Check("command line unicode string parser", CommandLineUnicodeParser);
        Check("search filter matches name and pid", SearchFilterMatches);
        Check("details status and display", DetailsStatusAndDisplay);
        Check("details error classification", DetailsErrorClassification);
        Check("file time formatting", FileTimeFormatting);
        Check("explorer select arguments", ExplorerSelectArguments);
        Check("row start time display", RowStartTimeDisplay);
        Check("collector dispose waits for worker", CollectorDisposeGuard);
        Check("command line buffer grows safely", CommandLineBufferGrow);
        Check("snapshot slot owns release", SnapshotSlotOwnership);
        Check("parse into validates bounds", ParseIntoBounds);
        Check("bulk visible sync resets once", BulkVisibleSync);
        Check("fake data never uses native lazy load", FakeDataNativeGuard);

        Log.WriteLine($"[test] {_passed} passed, {_failed} failed");
        return _failed;
    }

    // ---------------- cases ----------------

    private static void PreOrder()
    {
        // 1 -> 2 -> 4, 1 -> 3
        NodeRef[] nodes = Nodes((1, 0), (2, 1), (3, 1), (4, 2));
        List<FlatNode> flat = Flatten(nodes);

        ExpectPids(new uint[] { 1, 2, 4, 3 }, flat, "pre-order");
        ExpectDepths(new int[] { 0, 1, 2, 1 }, flat, "depths");
        Equal(0, flat[0].Depth, "root depth");
        True(flat[0].HasChildren, "root has children");
        True(!flat[2].HasChildren, "leaf has no children");
    }

    private static void CollapseHidesSubtree()
    {
        NodeRef[] nodes = Nodes((1, 0), (2, 1), (3, 1), (4, 2));
        List<FlatNode> flat = Flatten(nodes, new ProcKey(2, 1));

        // 折叠 2 时 2 自己仍然可见，隐藏的是子节点 4
        ExpectPids(new uint[] { 1, 2, 3 }, flat, "collapse 2");
    }

    private static void CollapseKeepsNode()
    {
        NodeRef[] nodes = Nodes((1, 0), (2, 1), (3, 1));
        List<FlatNode> flat = Flatten(nodes, new ProcKey(1, 1));

        ExpectPids(new uint[] { 1 }, flat, "collapse root");
    }

    private static void ChildBornWhileCollapsed()
    {
        // 父 2 被折叠，此时 5 才启动
        NodeRef[] nodes = Nodes((1, 0), (2, 1), (3, 1), (5, 2));
        List<FlatNode> flat = Flatten(nodes, new ProcKey(2, 1));

        ExpectPids(new uint[] { 1, 2, 3 }, flat, "new child stays hidden");
    }

    private static void ChildBornWhileCollapsedThenExpand()
    {
        ProcKey parent = new(2, 1);
        NodeRef[] nodes = Nodes((1, 0), (2, 1), (3, 1), (5, 2));

        var collapsed = new HashSet<ProcKey> { parent };
        var parents = new HashSet<ProcKey>();
        List<FlatNode> flat = Flatten(nodes, collapsed, parents);

        ExpectPids(new uint[] { 1, 2, 3 }, flat, "collapsed before expand");
        True(parents.Contains(parent), "parent with new child is tracked");

        collapsed.Remove(parent);
        flat = Flatten(nodes, collapsed, parents);

        ExpectPids(new uint[] { 1, 2, 5, 3 }, flat, "after expand");
        ExpectDepths(new int[] { 0, 1, 2, 1 }, flat, "after expand depths");
    }

    private static void ChildDiesWhileCollapsed()
    {
        NodeRef[] before = Nodes((1, 0), (2, 1), (3, 1), (4, 2));
        NodeRef[] after = Nodes((1, 0), (2, 1), (3, 1));   // 4 退出

        ExpectPids(new uint[] { 1, 2, 3 }, Flatten(before, new ProcKey(2, 1)), "before");
        ExpectPids(new uint[] { 1, 2, 3 }, Flatten(after, new ProcKey(2, 1)), "after");
    }

    private static void ParentDiesChildrenBecomeRoots()
    {
        // 2 退出，4 改挂到根；折叠集合里即使还有 2 也不应该影响 4
        NodeRef[] nodes = Nodes((1, 0), (4, 2));
        List<FlatNode> flat = Flatten(nodes, new ProcKey(2, 1));

        ExpectPids(new uint[] { 1, 4 }, flat, "orphan promoted");
        Equal(0, flat[1].Depth, "orphan depth");
    }

    private static void PidZeroIsNeverParent()
    {
        // Windows 的 Idle 进程就是 PID 0。它的 ParentPid 也是 0。
        // 如果只按 PID 查表，所有 ParentPid==0 的进程都会被挂到 Idle 下面。
        NodeRef[] nodes = Nodes((0, 0), (1, 0), (2, 0), (3, 1));
        List<FlatNode> flat = Flatten(nodes, new HashSet<ProcKey>());

        ExpectPids(new uint[] { 0, 1, 3, 2 }, flat, "pid 0 roots");
        ExpectDepths(new int[] { 0, 0, 1, 0 }, flat, "pid 0 must not nest orphans");
    }

    private static void YoungParentDoesNotAdoptOlderChild()
    {
        // PID 1 被复用：旧父已死，新进程创建时间 100，晚于子进程的 20。
        var nodes = new[]
        {
            new NodeRef(new ProcKey(1, 100), 0),
            new NodeRef(new ProcKey(2, 20), 1)
        };

        List<FlatNode> flat = Flatten(nodes, new HashSet<ProcKey>());

        ExpectPids(new uint[] { 1, 2 }, flat, "pid reuse roots");
        ExpectDepths(new int[] { 0, 0 }, flat, "young parent must not adopt");
    }

    private static void OlderParentAdoptsChild()
    {
        var nodes = new[]
        {
            new NodeRef(new ProcKey(1, 10), 0),
            new NodeRef(new ProcKey(2, 20), 1)
        };

        List<FlatNode> flat = Flatten(nodes, new HashSet<ProcKey>());

        ExpectPids(new uint[] { 1, 2 }, flat, "normal parent");
        ExpectDepths(new int[] { 0, 1 }, flat, "older parent adopts");
    }

    private static void DuplicatePidKeepsOldest()
    {
        // 脏数据里的重复 PID：父候选应选创建时间最早的，不能被新进程顶替。
        var nodes = new[]
        {
            new NodeRef(new ProcKey(1, 100), 0),   // 较新的同 PID
            new NodeRef(new ProcKey(1, 10), 0),    // 较旧的同 PID，应作为父候选
            new NodeRef(new ProcKey(2, 20), 1)     // 子进程
        };

        List<FlatNode> flat = Flatten(nodes, new HashSet<ProcKey>());

        ExpectPids(new uint[] { 1, 1, 2 }, flat, "duplicate pid order");
        ExpectDepths(new int[] { 0, 0, 1 }, flat, "oldest duplicate wins");
    }

    private static void PidReuseDoesNotInheritCollapse()
    {
        var collapsed = new HashSet<ProcKey> { new(Pid: 5, CreateTime: 1) };

        var nodes = new[]
        {
            new NodeRef(new ProcKey(5, CreateTime: 2), 0),   // 同 PID，新进程
            new NodeRef(new ProcKey(6, CreateTime: 2), 5)
        };

        var flat = new List<FlatNode>();
        new TreeFlattener().Flatten(nodes, nodes.Length, collapsed, flat);

        ExpectPids(new uint[] { 5, 6 }, flat, "pid reuse");
    }

    private static void CycleTerminates()
    {
        NodeRef[] nodes = Nodes((10, 11), (11, 10));

        var flat = new List<FlatNode>();
        new TreeFlattener().Flatten(nodes, nodes.Length, new HashSet<ProcKey>(), flat);

        Equal(2, flat.Count, "cycle node count");
        True(flat[0].Key.Pid != flat[1].Key.Pid, "both emitted once");
    }

    private static void DeepChain()
    {
        const int depth = 5000;
        var nodes = new NodeRef[depth];
        nodes[0] = new NodeRef(new ProcKey(1, 1), 0);

        for (int i = 1; i < depth; i++)
            nodes[i] = new NodeRef(new ProcKey((uint)(i + 1), 1), (uint)i);

        var flat = new List<FlatNode>();
        new TreeFlattener().Flatten(nodes, depth, new HashSet<ProcKey>(), flat);

        Equal(depth, flat.Count, "deep chain count");
        Equal(depth - 1, flat[depth - 1].Depth, "deepest depth");
    }

    private static void CollapsePrunedOnDeath()
    {
        var collapsed = new HashSet<ProcKey> { new(1, 1), new(2, 1), new(3, 1) };
        var alive = new HashSet<ProcKey> { new(1, 1), new(3, 1) };

        CollapseState.Prune(collapsed, k => !alive.Contains(k));

        Equal(2, collapsed.Count, "pruned count");
        True(!collapsed.Contains(new ProcKey(2, 1)), "dead key removed");
        True(collapsed.Contains(new ProcKey(3, 1)), "alive key kept");
    }

    private static void LastChildDeathReleasesCollapse()
    {
        ProcKey parent = new(1, 1);
        var collapsed = new HashSet<ProcKey> { parent };
        var parents = new HashSet<ProcKey>();

        // 有子进程时，折叠有效
        Flatten(Nodes((1, 0), (2, 1)), collapsed, parents);
        Equal(1, collapsed.Count, "collapse stays while a child exists");
        True(parents.Contains(parent), "parent tracked while child exists");

        // 最后一个子进程退出：展平后 parents 为空，折叠状态应被释放
        Flatten(Nodes((1, 0)), collapsed, parents);
        int removed = CollapseState.Prune(collapsed, k => !parents.Contains(k));
        Equal(1, removed, "leaf collapse removed");
        Equal(0, collapsed.Count, "collapse released");

        // 未来再有子进程出生时，父进程按默认展开处理
        List<FlatNode> flat = Flatten(Nodes((1, 0), (3, 1)), collapsed, parents);
        ExpectPids(new uint[] { 1, 3 }, flat, "new child visible after release");
        ExpectDepths(new int[] { 0, 1 }, flat, "new child depth");
    }

    private static void ParentDiesWhileCollapsed()
    {
        ProcKey deadParent = new(1, 1);
        var collapsed = new HashSet<ProcKey> { deadParent };
        var parents = new HashSet<ProcKey>();

        // 父 1 退出，子 2 升为根；3 是 2 的子进程
        List<FlatNode> flat = Flatten(Nodes((2, 1), (3, 2)), collapsed, parents);

        ExpectPids(new uint[] { 2, 3 }, flat, "orphan promoted");
        ExpectDepths(new int[] { 0, 1 }, flat, "orphan depth");

        int removed = CollapseState.Prune(collapsed, k => !parents.Contains(k));
        Equal(1, removed, "dead parent collapse removed");
        Equal(0, collapsed.Count, "dead parent collapse gone");
    }

    private static void ToggleRejectsStaleOrLeaf()
    {
        ProcKey parent = new(1, 1);
        ProcKey leaf = new(2, 1);
        ProcKey dead = new(3, 1);

        var collapsed = new HashSet<ProcKey>();
        var parents = new HashSet<ProcKey> { parent };

        True(CollapseState.Toggle(collapsed, parent, parents), "toggle on");
        True(collapsed.Contains(parent), "parent collapsed");

        True(CollapseState.Toggle(collapsed, parent, parents), "toggle off");
        True(!collapsed.Contains(parent), "parent expanded");

        True(!CollapseState.Toggle(collapsed, leaf, parents), "leaf rejected");
        True(!CollapseState.Toggle(collapsed, dead, parents), "dead key rejected");
        Equal(0, collapsed.Count, "invalid toggles leave set empty");
    }

    private static void SelectionMovesToCollapsedAncestor()
    {
        // 1(0) -> 2(1) -> 3(2), 1 -> 4(1), 5(0)
        List<FlatNode> flat = FlatRows((1, 0), (2, 1), (3, 2), (4, 1), (5, 0));
        ProcKey root = new(1, 1);

        True(TreeSelection.ShouldMoveSelection(flat, root, new ProcKey(2, 1)), "direct child");
        True(TreeSelection.ShouldMoveSelection(flat, root, new ProcKey(3, 1)), "grandchild");
        True(!TreeSelection.ShouldMoveSelection(flat, root, root), "self");
    }

    private static void SelectionStaysOutsideCollapsedSubtree()
    {
        // 1(0) -> 2(1) -> 3(2), 1 -> 4(1), 5(0)
        List<FlatNode> flat = FlatRows((1, 0), (2, 1), (3, 2), (4, 1), (5, 0));
        ProcKey branch = new(2, 1);

        True(!TreeSelection.ShouldMoveSelection(flat, branch, new ProcKey(4, 1)), "sibling branch");
        True(!TreeSelection.ShouldMoveSelection(flat, branch, new ProcKey(5, 1)), "outside");
        True(!TreeSelection.ShouldMoveSelection(flat, branch, branch), "self");
        True(!TreeSelection.ShouldMoveSelection(flat, branch, null), "null selection");
        True(!TreeSelection.ShouldMoveSelection(flat, new ProcKey(99, 1), new ProcKey(3, 1)),
            "missing ancestor");
    }

    private static void CollapseSetExplicit()
    {
        ProcKey parent = new(1, 1);
        var collapsed = new HashSet<ProcKey>();
        var parents = new HashSet<ProcKey> { parent };

        True(CollapseState.Set(collapsed, parent, expand: false, parents), "collapse changes");
        True(collapsed.Contains(parent), "parent collapsed");

        True(!CollapseState.Set(collapsed, parent, expand: false, parents), "repeat collapse no-op");

        True(CollapseState.Set(collapsed, parent, expand: true, parents), "expand changes");
        True(!collapsed.Contains(parent), "parent expanded");

        True(!CollapseState.Set(collapsed, parent, expand: true, parents), "repeat expand no-op");
    }

    private static void LeafExplicitExpandRejected()
    {
        ProcKey parent = new(1, 1);
        ProcKey leaf = new(2, 1);
        var collapsed = new HashSet<ProcKey>();
        var parents = new HashSet<ProcKey> { parent };

        True(!CollapseState.Set(collapsed, leaf, expand: false, parents), "leaf collapse rejected");
        True(!CollapseState.Set(collapsed, leaf, expand: true, parents), "leaf expand rejected");
        Equal(0, collapsed.Count, "leaf leaves set empty");
    }

    private static void UiStateRoundTrip()
    {
        string root = NewTempRoot();
        try
        {
            var state = new UiState(
                "svchost",
                ProcessViewMode.Tree,
                new[] { new ProcKey(10, 100), new ProcKey(20, 200) },
                DateTime.UtcNow,
                "Pid",
                ListSortDirection.Descending);

            string path = UiStateStore.Save(state, root);
            True(File.Exists(path), "state file written");

            UiState? loaded = UiStateStore.TryLoad(path, root);
            True(loaded.HasValue, "state loaded");
            if (!loaded.HasValue) return;

            UiState restored = loaded.Value;
            EqualText("svchost", restored.SearchText, "search restored");
            Equal((int)ProcessViewMode.Flat, (int)restored.ViewMode, "search forces flat");
            Equal(2, restored.Collapsed.Length, "collapsed count");
            True(restored.Collapsed[0].Equals(new ProcKey(10, 100)), "first key");
            EqualText("Pid", restored.SortKey!, "sort key restored");
            Equal((int)ListSortDirection.Descending, (int)restored.SortDirection, "sort direction restored");
            True(!File.Exists(path), "state file deleted after load");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void UiStateRejectsStale()
    {
        string root = NewTempRoot();
        try
        {
            var state = new UiState(
                "x",
                ProcessViewMode.Tree,
                Array.Empty<ProcKey>(),
                DateTime.UtcNow.AddMinutes(-10));

            string path = UiStateStore.Save(state, root);
            True(UiStateStore.TryLoad(path, root) is null, "stale rejected");
            True(!File.Exists(path), "stale file removed");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void UiStateRejectsBadVersion()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "bad-version.json");
            File.WriteAllText(
                path,
                "{\"version\":99,\"savedAtUtc\":\"" + DateTime.UtcNow.ToString("O") +
                "\",\"searchText\":\"x\",\"viewMode\":\"Tree\",\"collapsed\":[]}");

            True(UiStateStore.TryLoad(path, root) is null, "bad version rejected");
            True(!File.Exists(path), "bad version file removed");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void UiStateRejectsMalformedAndOversized()
    {
        string root = NewTempRoot();
        try
        {
            string malformed = Path.Combine(root, "malformed.json");
            File.WriteAllText(malformed, "{ not json");
            True(UiStateStore.TryLoad(malformed, root) is null, "malformed rejected");
            True(!File.Exists(malformed), "malformed file removed");

            string oversized = Path.Combine(root, "oversized.json");
            File.WriteAllText(oversized, new string('x', UiStateStore.MaxBytes + 1));
            True(UiStateStore.TryLoad(oversized, root) is null, "oversized rejected");
            True(!File.Exists(oversized), "oversized file removed");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void UiStateRejectsPathOutsideRoot()
    {
        string rootA = NewTempRoot();
        string rootB = NewTempRoot();
        try
        {
            var state = new UiState(
                "x",
                ProcessViewMode.Tree,
                Array.Empty<ProcKey>(),
                DateTime.UtcNow);

            string path = UiStateStore.Save(state, rootA);
            True(UiStateStore.TryLoad(path, rootB) is null, "outside path rejected");
            True(File.Exists(path), "outside path not deleted");
        }
        finally
        {
            DeleteTempRoot(rootA);
            DeleteTempRoot(rootB);
        }
    }

    private static void UiStateAcceptsLegacyJson()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "legacy.json");
            File.WriteAllText(
                path,
                "{\"version\":1,\"savedAtUtc\":\"" + DateTime.UtcNow.ToString("O") +
                "\",\"searchText\":\"\",\"viewMode\":\"Tree\",\"collapsed\":[]}");

            UiState? loaded = UiStateStore.TryLoad(path, root);
            True(loaded.HasValue, "legacy state loaded");
            if (!loaded.HasValue) return;

            True(loaded.Value.SortKey is null, "legacy has no sort key");
            Equal((int)ListSortDirection.Ascending, (int)loaded.Value.SortDirection,
                "legacy defaults to ascending");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void UiStateDropsUnknownSortKey()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "unknown-sort.json");
            File.WriteAllText(
                path,
                "{\"version\":1,\"savedAtUtc\":\"" + DateTime.UtcNow.ToString("O") +
                "\",\"searchText\":\"\",\"viewMode\":\"Flat\",\"collapsed\":[]," +
                "\"sortKey\":\"Bogus\",\"sortDirection\":\"Descending\"}");

            UiState? loaded = UiStateStore.TryLoad(path, root);
            True(loaded.HasValue, "state with unknown sort loaded");
            if (!loaded.HasValue) return;

            True(loaded.Value.SortKey is null, "unknown sort key dropped");
            Equal((int)ListSortDirection.Descending, (int)loaded.Value.SortDirection,
                "direction still parsed");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void ElevationArguments()
    {
        string arguments = ElevationHelper.BuildArguments(
            new[] { @"C:\app\TaskMgrLite.exe", "--real", @"--restore-ui=C:\old\state.json" },
            @"C:\Temp\new state.json");

        True(arguments.Contains("--real", StringComparison.Ordinal), "flag kept");
        True(!arguments.Contains("old", StringComparison.Ordinal), "old restore removed");
        True(arguments.Contains(
                "\"--restore-ui=C:\\Temp\\new state.json\"",
                StringComparison.Ordinal),
            "new restore quoted");
    }

    private static void UiStateNormalizeForcesFlat()
    {
        UiState searched = new UiState(
            "svchost",
            ProcessViewMode.Tree,
            Array.Empty<ProcKey>(),
            DateTime.UtcNow).Normalize();

        Equal((int)ProcessViewMode.Flat, (int)searched.ViewMode, "search forces flat");

        UiState empty = new UiState(
            "",
            ProcessViewMode.Tree,
            Array.Empty<ProcKey>(),
            DateTime.UtcNow).Normalize();

        Equal((int)ProcessViewMode.Tree, (int)empty.ViewMode, "empty keeps tree");
    }

    private static void SortNextFlipsSameKey()
    {
        SortState state = SortState.Empty.Next("Pid");
        Equal((int)ListSortDirection.Descending, (int)state.Direction, "numeric first click descending");

        state = state.Next("Pid");
        Equal((int)ListSortDirection.Ascending, (int)state.Direction, "same key flips to ascending");

        state = state.Next("Pid");
        Equal((int)ListSortDirection.Descending, (int)state.Direction, "same key flips back");
    }

    private static void SortNextResetsOnNewKey()
    {
        SortState state = SortState.Empty.Next("Pid");
        Equal((int)ListSortDirection.Descending, (int)state.Direction, "numeric default descending");

        state = state.Next("Name");
        EqualText("Name", state.Key!, "new key");
        Equal((int)ListSortDirection.Ascending, (int)state.Direction, "text default ascending");
    }

    private static void SortFirstNumericDescending()
    {
        string[] numeric = { "Pid", "ParentPid", "CpuPercent", "MemoryMb", "HandleCount" };

        foreach (string key in numeric)
        {
            True(SortState.IsNumericKey(key), key);
            SortState state = SortState.Empty.Next(key);
            EqualText(key, state.Key!, key);
            Equal((int)ListSortDirection.Descending, (int)state.Direction, key);
        }
    }

    private static void SortFirstTextAscending()
    {
        True(!SortState.IsNumericKey("Name"), "name is text");
        SortState state = SortState.Empty.Next("Name");
        EqualText("Name", state.Key!, "name key");
        Equal((int)ListSortDirection.Ascending, (int)state.Direction, "text starts ascending");
    }

    private static void SortCommandLineDisabled()
    {
        True(!SortState.CanSort("CommandLine"), "command line cannot sort");
        True(SortState.NormalizeKey("CommandLine") is null, "command line dropped from restore");
        True(!SortState.Empty.Next("CommandLine").HasSort, "no sort state created");
        True(SortState.CanSort("Name"), "name can sort");
    }

    private static void NetworkPortDiffSilentWhenUnchanged()
    {
        var target = new ObservableCollection<NetworkPortRowViewModel>();
        var byKey = new Dictionary<PortKey, NetworkPortRowViewModel>();
        var desiredKeys = new HashSet<PortKey>();
        var names = new Dictionary<uint, string> { [10] = "chrome.exe" };

        PortEntry[] desired =
        {
            PortEntryOf("TCP4", "127.0.0.1:443", "10.0.0.1:5000", "ESTABLISHED", 10, 443, 5000),
            PortEntryOf("UDP4", "0.0.0.0:5353", "—", "—", 20, 5353, 0)
        };

        int events = 0;
        target.CollectionChanged += (_, _) => events++;

        True(NetworkPortListSync.Apply(target, desired, names, byKey, desiredKeys),
            "first frame changes");
        Equal(2, target.Count, "two rows inserted");
        EqualText("chrome.exe", target[0].ProcessName, "process name resolved");
        EqualText("Unknown", target[1].ProcessName, "missing process name is unknown");

        events = 0;
        True(!NetworkPortListSync.Apply(target, desired, names, byKey, desiredKeys),
            "second frame is unchanged");
        Equal(0, events, "no CollectionChanged when data is unchanged");
    }

    private static void NetworkPortDiffStateOnly()
    {
        var target = new ObservableCollection<NetworkPortRowViewModel>();
        var byKey = new Dictionary<PortKey, NetworkPortRowViewModel>();
        var desiredKeys = new HashSet<PortKey>();
        var names = new Dictionary<uint, string> { [10] = "chrome.exe" };

        PortEntry[] listening =
        {
            PortEntryOf("TCP4", "127.0.0.1:443", "10.0.0.1:5000", "LISTEN", 10, 443, 5000)
        };
        PortEntry[] established =
        {
            PortEntryOf("TCP4", "127.0.0.1:443", "10.0.0.1:5000", "ESTABLISHED", 10, 443, 5000)
        };

        NetworkPortListSync.Apply(target, listening, names, byKey, desiredKeys);

        int events = 0;
        target.CollectionChanged += (_, _) => events++;

        True(!NetworkPortListSync.Apply(target, established, names, byKey, desiredKeys),
            "state change is not structural");
        Equal(0, events, "state update does not raise CollectionChanged");
        EqualText("ESTABLISHED", target[0].State, "state updated in place");
    }

    private static void NetworkPortDiffInsertRemove()
    {
        var target = new ObservableCollection<NetworkPortRowViewModel>();
        var byKey = new Dictionary<PortKey, NetworkPortRowViewModel>();
        var desiredKeys = new HashSet<PortKey>();
        var names = new Dictionary<uint, string>();

        PortEntry[] first =
        {
            PortEntryOf("TCP4", "0.0.0.0:80", "1.1.1.1:1000", "ESTABLISHED", 1, 80, 1000),
            PortEntryOf("TCP4", "0.0.0.0:81", "1.1.1.1:1001", "ESTABLISHED", 2, 81, 1001)
        };
        PortEntry[] second =
        {
            PortEntryOf("TCP4", "0.0.0.0:81", "1.1.1.1:1001", "ESTABLISHED", 2, 81, 1001),
            PortEntryOf("TCP4", "0.0.0.0:82", "1.1.1.1:1002", "ESTABLISHED", 3, 82, 1002)
        };

        NetworkPortListSync.Apply(target, first, names, byKey, desiredKeys);

        int events = 0;
        bool reset = false;
        target.CollectionChanged += (_, e) =>
        {
            events++;
            if (e.Action == NotifyCollectionChangedAction.Reset)
                reset = true;
        };

        True(NetworkPortListSync.Apply(target, second, names, byKey, desiredKeys),
            "insert/remove changes structure");
        Equal(2, target.Count, "two rows after churn");
        Equal(2, (int)target[0].Pid, "surviving row kept");
        Equal(3, (int)target[1].Pid, "new row inserted");
        True(events > 0, "collection notified");
        True(!reset, "no Reset redraw");
    }

    private static void NetworkPortDiffReorder()
    {
        var target = new ObservableCollection<NetworkPortRowViewModel>();
        var byKey = new Dictionary<PortKey, NetworkPortRowViewModel>();
        var desiredKeys = new HashSet<PortKey>();
        var names = new Dictionary<uint, string>();

        PortEntry[] first =
        {
            PortEntryOf("TCP4", "0.0.0.0:80", "1.1.1.1:1000", "ESTABLISHED", 1, 80, 1000),
            PortEntryOf("TCP4", "0.0.0.0:81", "1.1.1.1:1001", "ESTABLISHED", 2, 81, 1001),
            PortEntryOf("TCP4", "0.0.0.0:82", "1.1.1.1:1002", "ESTABLISHED", 3, 82, 1002)
        };
        PortEntry[] reordered = { first[2], first[0], first[1] };

        NetworkPortListSync.Apply(target, first, names, byKey, desiredKeys);

        int moves = 0;
        bool reset = false;
        target.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Move)
                moves++;
            if (e.Action == NotifyCollectionChangedAction.Reset)
                reset = true;
        };

        True(NetworkPortListSync.Apply(target, reordered, names, byKey, desiredKeys),
            "reorder changes structure");
        Equal(3, (int)target[0].Pid, "first row moved to front");
        Equal(1, (int)target[1].Pid, "second row follows");
        Equal(2, (int)target[2].Pid, "third row follows");
        Equal(1, moves, "one Move event");
        True(!reset, "reorder never resets");
    }

    private static PortEntry PortEntryOf(
        string protocol,
        string local,
        string remote,
        string state,
        uint pid,
        ushort localPort,
        ushort remotePort) => new(
            protocol,
            local,
            remote,
            state,
            pid,
            localPort,
            remotePort);

    private static void SortStateModeGate()
    {
        SortState none = SortState.Empty;
        True(!none.ShouldApply(ProcessViewMode.Flat), "empty has nothing to apply");
        True(!none.IsSuspended(ProcessViewMode.Tree), "empty has nothing to suspend");

        SortState sorted = none.Next("Pid");
        True(!sorted.ShouldApply(ProcessViewMode.Tree), "tree suspends sort");
        True(sorted.IsSuspended(ProcessViewMode.Tree), "tree reports suspension");
        True(sorted.ShouldApply(ProcessViewMode.Flat), "flat applies sort");
        True(!sorted.IsSuspended(ProcessViewMode.Flat), "flat is not suspended");

        True(SortState.NormalizeKey("Pid") is not null, "known key kept");
        True(SortState.NormalizeKey("Bogus") is null, "unknown key rejected");
    }

    private static void OkCacheNeverExpires()
    {
        var cache = new CommandLineCache();
        ProcKey key = new(1, 1);

        cache.Set(key, new CommandLineResult("cmd.exe --flag", CommandLineStatus.Ok), 0);

        True(cache.TryGet(key, 600_000, out CommandLineResult result), "ok hit after 10 min");
        EqualText("cmd.exe --flag", result.Text!, "ok text");
    }

    private static void NegativeCacheExpiresAt30s()
    {
        var cache = new CommandLineCache();
        ProcKey key = new(1, 1);

        cache.Set(key, new CommandLineResult(null, CommandLineStatus.AccessDenied), 0);

        True(cache.TryGet(key, 29_999, out _), "denied before ttl");
        True(!cache.TryGet(key, 30_000, out _), "denied at ttl");
        Equal(0, cache.Count, "expired entry removed");
    }

    private static void ErrorCacheExpiresAt30s()
    {
        var cache = new CommandLineCache();
        ProcKey key = new(1, 1);

        cache.Set(key, new CommandLineResult(null, CommandLineStatus.Error), 0);

        True(cache.TryGet(key, 29_999, out _), "error before ttl");
        True(!cache.TryGet(key, 30_000, out _), "error at ttl");
        Equal(0, cache.Count, "error entry removed");
    }

    private static void TerminalCacheStatusesDoNotExpire()
    {
        var cache = new CommandLineCache();
        CommandLineStatus[] statuses =
        {
            CommandLineStatus.Exited,
            CommandLineStatus.ProcessReused,
            CommandLineStatus.Unsupported
        };

        for (int i = 0; i < statuses.Length; i++)
        {
            ProcKey key = new((uint)(i + 1), 1);
            cache.Set(key, new CommandLineResult(null, statuses[i]), 0);
            True(cache.TryGet(key, 30_000, out _), statuses[i].ToString());
        }
    }

    private static void CacheTimestampRefreshes()
    {
        var cache = new CommandLineCache();
        ProcKey key = new(1, 1);
        var denied = new CommandLineResult(null, CommandLineStatus.AccessDenied);

        cache.Set(key, denied, 0);
        cache.Set(key, denied, 20_000);

        True(cache.TryGet(key, 49_999, out _), "refreshed entry before ttl");
        True(!cache.TryGet(key, 50_000, out _), "refreshed entry at ttl");
    }

    private static void CommandLineCacheKeepsLruBound()
    {
        var cache = new CommandLineCache(capacity: 2);
        var ok = new CommandLineResult("cmd", CommandLineStatus.Ok);

        ProcKey k1 = new(1, 1);
        ProcKey k2 = new(2, 1);
        ProcKey k3 = new(3, 1);

        cache.Set(k1, ok, 0);
        cache.Set(k2, ok, 0);

        // 命中 k1，把它移到表头；之后 k2 是最久未使用
        True(cache.TryGet(k1, 0, out _), "k1 hit");
        cache.Set(k3, ok, 0);

        True(cache.TryGet(k1, 0, out _), "k1 kept");
        True(!cache.TryGet(k2, 0, out _), "k2 evicted");
        Equal(2, cache.Count, "capacity kept");
    }

    private static void CollapseBoundedUnderChurn()
    {
        var collapsed = new HashSet<ProcKey>();
        var alive = new HashSet<ProcKey>();

        for (uint i = 1; i <= 500; i++)
        {
            alive.Add(new ProcKey(i, 1));
            if (i % 3 == 0) collapsed.Add(new ProcKey(i, 1));
        }

        var dead = new List<ProcKey>();
        var isDead = new Predicate<ProcKey>(k => !alive.Contains(k));

        for (uint tick = 0; tick < 500; tick++)
        {
            dead.Clear();
            foreach (ProcKey k in alive)
                if (k.Pid <= tick * 2) dead.Add(k);

            foreach (ProcKey k in dead) alive.Remove(k);

            for (uint i = 0; i < 10; i++)
            {
                uint pid = 10_000 + tick * 10 + i;
                alive.Add(new ProcKey(pid, 1));
                if (pid % 3 == 0) collapsed.Add(new ProcKey(pid, 1));
            }

            CollapseState.Prune(collapsed, isDead);
            True(collapsed.Count <= alive.Count, $"collapsed <= alive at tick {tick}");
        }
    }

    private static void VisibleListSyncWorks()
    {
        var rows = new List<TestRow>();
        var byKey = new Dictionary<ProcKey, TestRow>();

        TestRow Resolve(ProcKey k)
        {
            if (!byKey.TryGetValue(k, out TestRow? row))
            {
                row = new TestRow(k);
                byKey[k] = row;
            }
            return row;
        }

        rows.Add(Resolve(new ProcKey(1, 1)));
        rows.Add(Resolve(new ProcKey(2, 1)));
        rows.Add(Resolve(new ProcKey(3, 1)));

        var scratch = new HashSet<ProcKey>();

        // 插入 4 到 1 和 2 之间
        var desired = new List<ProcKey> { new(1, 1), new(4, 1), new(2, 1), new(3, 1) };
        VisibleListSync.Apply(rows, desired, r => r.Key, Resolve, scratch);
        ExpectRowPids(new uint[] { 1, 4, 2, 3 }, rows, "insert");

        // 把 3 移到 2 前面
        desired = new List<ProcKey> { new(1, 1), new(4, 1), new(3, 1), new(2, 1) };
        VisibleListSync.Apply(rows, desired, r => r.Key, Resolve, scratch);
        ExpectRowPids(new uint[] { 1, 4, 3, 2 }, rows, "move");

        // 删掉 4
        desired = new List<ProcKey> { new(1, 1), new(3, 1), new(2, 1) };
        VisibleListSync.Apply(rows, desired, r => r.Key, Resolve, scratch);
        ExpectRowPids(new uint[] { 1, 3, 2 }, rows, "remove");
    }

    private static void VisibleListSyncRandomChurn()
    {
        var random = new Random(20261122);
        var rows = new List<TestRow>();
        var byKey = new Dictionary<ProcKey, TestRow>();
        var desired = new List<ProcKey>();
        var scratch = new HashSet<ProcKey>();
        uint nextPid = 1;

        TestRow Resolve(ProcKey key)
        {
            if (!byKey.TryGetValue(key, out TestRow? row))
            {
                row = new TestRow(key);
                byKey[key] = row;
            }

            return row;
        }

        for (int tick = 0; tick < 500; tick++)
        {
            int edits = random.Next(1, 20);
            for (int e = 0; e < edits; e++)
            {
                int action = random.Next(4);

                if (action == 0 || desired.Count == 0)
                {
                    int at = random.Next(desired.Count + 1);
                    desired.Insert(at, new ProcKey(nextPid++, 1));
                }
                else if (action == 1)
                {
                    desired.RemoveAt(random.Next(desired.Count));
                }
                else if (action == 2)
                {
                    int from = random.Next(desired.Count);
                    ProcKey key = desired[from];
                    desired.RemoveAt(from);
                    desired.Insert(random.Next(desired.Count + 1), key);
                }
                else
                {
                    desired[random.Next(desired.Count)] = new ProcKey(nextPid++, 1);
                }
            }

            VisibleListSync.Apply(rows, desired, r => r.Key, Resolve, scratch);

            if (rows.Count != desired.Count)
                throw new Exception($"random sync tick {tick}: expected {desired.Count} rows, got {rows.Count}");

            var seen = new HashSet<ProcKey>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (!rows[i].Key.Equals(desired[i]))
                    throw new Exception($"random sync tick {tick}: row {i} order mismatch");

                if (!seen.Add(rows[i].Key))
                    throw new Exception($"random sync tick {tick}: duplicate key at row {i}");
            }
        }
    }

    private static void FlattenSteadyStateDoesNotAllocate()
    {
        const int count = 400;

        var nodes = new NodeRef[count];
        var collapsed = new HashSet<ProcKey>();
        var parents = new HashSet<ProcKey>();
        var output = new List<FlatNode>(count);
        var flattener = new TreeFlattener();

        for (int i = 0; i < count; i++)
        {
            uint pid = (uint)(i + 1);
            uint parentPid = i == 0 ? 0u : (uint)((i - 1) / 3 + 1);
            var key = new ProcKey(pid, 1000 + i);

            nodes[i] = new NodeRef(key, parentPid);
            if (i % 17 == 0) collapsed.Add(key);
        }

        // 预热：让数组、字典、HashSet 的容量都达到稳态
        for (int i = 0; i < 32; i++)
            flattener.Flatten(nodes, count, collapsed, output, parents);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
            flattener.Flatten(nodes, count, collapsed, output, parents);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        True(allocated == 0, $"steady state allocated {allocated} bytes over 200 flattens");
    }

    private static void RandomForestMatchesOracle()
    {
        var random = new Random(778899);

        for (int iteration = 0; iteration < 300; iteration++)
        {
            int count = random.Next(1, 250);
            var keys = new ProcKey[count];
            var nodes = new NodeRef[count];

            for (int i = 0; i < count; i++)
                keys[i] = new ProcKey((uint)i, random.Next(1, 5000));

            for (int i = 0; i < count; i++)
            {
                uint parentPid = random.Next(6) switch
                {
                    0 => 0u,
                    1 => (uint)random.Next(count + 50),   // 可能不存在
                    _ => (uint)random.Next(count)         // 可能自环 / 可能比子进程年轻
                };

                nodes[i] = new NodeRef(keys[i], parentPid);
            }

            var collapsed = new HashSet<ProcKey>();
            for (int i = 0; i < count; i++)
                if (random.Next(5) == 0) collapsed.Add(keys[i]);

            var actual = new List<FlatNode>(count);
            var parents = new HashSet<ProcKey>();
            new TreeFlattener().Flatten(nodes, count, collapsed, actual, parents);

            List<FlatNode> expected = OracleFlatten(nodes, collapsed, out HashSet<ProcKey> expectedParents);
            CompareFlat(expected, actual, iteration);

            if (parents.Count != expectedParents.Count)
            {
                throw new Exception(
                    $"oracle {iteration}: parents expected {expectedParents.Count}, got {parents.Count}");
            }

            foreach (ProcKey key in expectedParents)
                True(parents.Contains(key), $"oracle {iteration}: missing parent {key.Pid}:{key.CreateTime}");
        }
    }

    /// <summary>
    /// 朴素递归版展平，作为随机对照测试的 oracle。
    /// 和 TreeFlattener 的计数排序实现相互独立，只共享同一份规格。
    /// </summary>
    private static List<FlatNode> OracleFlatten(
        NodeRef[] nodes,
        HashSet<ProcKey> collapsed,
        out HashSet<ProcKey> parentKeys)
    {
        int count = nodes.Length;

        var byPid = new Dictionary<uint, int>();
        for (int i = 0; i < count; i++)
        {
            uint pid = nodes[i].Key.Pid;
            if (!byPid.TryGetValue(pid, out int existing) ||
                nodes[i].Key.CreateTime < nodes[existing].Key.CreateTime)
            {
                byPid[pid] = i;
            }
        }

        var parent = new int[count];
        Array.Fill(parent, -1);
        var childCount = new int[count];

        for (int i = 0; i < count; i++)
        {
            uint parentPid = nodes[i].ParentPid;
            if (parentPid == 0) continue;
            if (!byPid.TryGetValue(parentPid, out int p) || p == i) continue;

            long parentStart = nodes[p].Key.CreateTime;
            long childStart = nodes[i].Key.CreateTime;
            if (parentStart != 0 && childStart != 0 && parentStart > childStart) continue;

            parent[i] = p;
            childCount[p]++;
        }

        // 父集合包含“结构上有子节点”的所有节点，包括被祖先折叠藏起来的节点。
        // 不能从可见行反推，否则会漏掉隐藏父节点。
        parentKeys = new HashSet<ProcKey>();
        for (int i = 0; i < count; i++)
            if (childCount[i] > 0) parentKeys.Add(nodes[i].Key);

        var roots = new List<int>();
        for (int i = 0; i < count; i++)
            if (parent[i] < 0) roots.Add(i);

        var reachable = new bool[count];

        foreach (int root in roots) MarkReachable(root);

        void MarkReachable(int current)
        {
            if (reachable[current]) return;
            reachable[current] = true;

            for (int i = 0; i < count; i++)
                if (parent[i] == current) MarkReachable(i);
        }

        var emitted = new bool[count];
        var result = new List<FlatNode>(count);

        void Emit(int node, int depth)
        {
            if (emitted[node]) return;
            emitted[node] = true;

            ProcKey key = nodes[node].Key;
            result.Add(new FlatNode(key, depth, childCount[node] > 0));

            if (collapsed.Contains(key)) return;

            for (int i = 0; i < count; i++)
                if (parent[i] == node) Emit(i, depth + 1);
        }

        foreach (int root in roots) Emit(root, 0);

        for (int i = 0; i < count; i++)
            if (!reachable[i] && !emitted[i]) Emit(i, 0);

        return result;
    }

    private static void CompareFlat(List<FlatNode> expected, List<FlatNode> actual, int iteration)
    {
        if (expected.Count != actual.Count)
            throw new Exception($"oracle {iteration}: expected {expected.Count} nodes, got {actual.Count}");

        for (int i = 0; i < expected.Count; i++)
        {
            FlatNode e = expected[i];
            FlatNode a = actual[i];

            if (!e.Key.Equals(a.Key) || e.Depth != a.Depth || e.HasChildren != a.HasChildren)
            {
                throw new Exception(
                    $"oracle {iteration}: row {i} expected " +
                    $"{e.Key.Pid}@{e.Depth}/{(e.HasChildren ? 1 : 0)}, got " +
                    $"{a.Key.Pid}@{a.Depth}/{(a.HasChildren ? 1 : 0)}");
            }
        }
    }

    private static void TreeKillPlanIsPostOrder()
    {
        NodeRef[] nodes = Nodes((1, 0), (2, 1), (3, 1), (4, 2));
        var plan = new List<ProcKey>();
        var flattener = new TreeFlattener();

        True(flattener.TryCollectSubtreePostOrder(
            nodes, nodes.Length, new ProcKey(1, 1), plan), "plan whole tree");

        ExpectKeyPids(new uint[] { 3, 4, 2, 1 }, plan, "whole tree post-order");
        Equal(1, (int)plan[^1].Pid, "root must be last");

        True(flattener.TryCollectSubtreePostOrder(
            nodes, nodes.Length, new ProcKey(2, 1), plan), "plan subtree");
        ExpectKeyPids(new uint[] { 4, 2 }, plan, "subtree post-order");
    }

    private static void TreeKillPlanRejectsStaleRoots()
    {
        NodeRef[] nodes = Nodes((1, 0), (2, 1));
        var plan = new List<ProcKey>();
        var flattener = new TreeFlattener();

        True(!flattener.TryCollectSubtreePostOrder(
            nodes, nodes.Length, new ProcKey(99, 1), plan), "missing pid rejected");
        Equal(0, plan.Count, "missing plan empty");

        True(!flattener.TryCollectSubtreePostOrder(
            nodes, nodes.Length, new ProcKey(1, 999), plan), "reused pid rejected");
        Equal(0, plan.Count, "reused plan empty");

        True(!flattener.TryCollectSubtreePostOrder(
            nodes, nodes.Length, new ProcKey(0, 1), plan), "pid 0 rejected");
        Equal(0, plan.Count, "pid 0 plan empty");
    }

    private static void TreeKillPlanHandlesCycles()
    {
        NodeRef[] nodes = Nodes((10, 11), (11, 10));
        var plan = new List<ProcKey>();

        True(new TreeFlattener().TryCollectSubtreePostOrder(
            nodes, nodes.Length, new ProcKey(10, 1), plan), "cycle plan");

        Equal(2, plan.Count, "cycle plan count");
        Equal(10, (int)plan[^1].Pid, "cycle root last");
        True(plan[0].Pid != plan[1].Pid, "cycle nodes unique");
    }

    private static void KillGuardsBlockSystemAndSelf()
    {
        uint selfPid = (uint)Environment.ProcessId;

        Equal((int)KillStatus.SystemProcess,
            (int)ProcessTerminator.Kill(new ProcKey(0, 1), selfPid).Status, "idle blocked");
        Equal((int)KillStatus.SystemProcess,
            (int)ProcessTerminator.Kill(new ProcKey(4, 1), selfPid).Status, "system blocked");
        Equal((int)KillStatus.SelfProcess,
            (int)ProcessTerminator.Kill(new ProcKey(selfPid, 1), selfPid).Status, "self blocked");
    }

    private static void KillErrorClassification()
    {
        Equal((int)KillStatus.AccessDenied,
            (int)ProcessTerminator.ClassifyWin32Error(5), "access denied");
        Equal((int)KillStatus.AlreadyExited,
            (int)ProcessTerminator.ClassifyWin32Error(87), "invalid parameter");
        Equal((int)KillStatus.AlreadyExited,
            (int)ProcessTerminator.ClassifyWin32Error(1168), "not found");
        Equal((int)KillStatus.Failed,
            (int)ProcessTerminator.ClassifyWin32Error(12345), "unknown error");
    }

    private static void KillReportSummarizesFailures()
    {
        var report = new KillReport(new[]
        {
            new KillOutcome(new ProcKey(10, 1), KillStatus.Success, 0),
            new KillOutcome(new ProcKey(11, 1), KillStatus.AccessDenied, 5)
        });

        Equal(1, report.Succeeded, "report success");
        Equal(1, report.Failed, "report failure");
        True(report.HasAccessDenied, "report access denied");

        string summary = report.BuildSummary("结束进程");
        True(summary.Contains("失败 1", StringComparison.Ordinal), "summary failure count");

        string message = report.BuildMessage("结束进程");
        True(message.Contains("成功 1", StringComparison.Ordinal), "message success count");
        True(message.Contains("拒绝访问", StringComparison.Ordinal), "message reason");
        True(message.Contains("PID 11", StringComparison.Ordinal), "message pid");
    }

    private static void LruCacheEvictsLeastRecentlyUsed()
    {
        var cache = new LruCache<int, string>(3);

        cache.Set(1, "a");
        cache.Set(2, "b");
        cache.Set(3, "c");

        True(cache.TryGet(1, out string? one) && one == "a", "cache hit a");
        cache.Set(4, "d");

        True(!cache.TryGet(2, out _), "least recently used evicted");
        True(cache.TryGet(1, out _), "recently used kept");
        True(cache.TryGet(3, out _), "third kept");
        True(cache.TryGet(4, out _), "new entry kept");
        Equal(3, cache.Count, "cache capacity");

        True(cache.Remove(3), "cache remove");
        Equal(2, cache.Count, "cache count after remove");
    }

    private static void CommandLineUnicodeParser()
    {
        const string text = "C:\\Program Files\\demo.exe --flag 中文";

        byte[] bytes = Encoding.Unicode.GetBytes(text);
        int size = 16 + bytes.Length;
        IntPtr buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.WriteInt16(buffer, 0, (short)bytes.Length);
            Marshal.WriteInt16(buffer, 2, (short)bytes.Length);
            Marshal.WriteIntPtr(buffer, 8, IntPtr.Add(buffer, 16));
            Marshal.Copy(bytes, 0, IntPtr.Add(buffer, 16), bytes.Length);

            EqualText(text, CommandLineReader.ParseUnicodeString(buffer, size) ?? "<null>", "unicode text");

            Marshal.WriteInt16(buffer, 0, 0);
            EqualText(string.Empty, CommandLineReader.ParseUnicodeString(buffer, size) ?? "<null>", "empty text");

            Marshal.WriteInt16(buffer, 0, (short)bytes.Length);
            Marshal.WriteIntPtr(buffer, 8, IntPtr.Add(buffer, size));
            True(CommandLineReader.ParseUnicodeString(buffer, size) is null, "out of range pointer");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SearchFilterMatches()
    {
        True(ProcessFilter.Matches("chrome.exe", 1234, ""), "empty query");
        True(ProcessFilter.Matches("chrome.exe", 1234, null), "null query");
        True(ProcessFilter.Matches("chrome.exe", 1234, "CHROME"), "name case insensitive");
        True(ProcessFilter.Matches("chrome.exe", 1234, "234"), "pid substring");
        True(!ProcessFilter.Matches("chrome.exe", 1234, "firefox"), "no match");
    }

    private static void DetailsStatusAndDisplay()
    {
        var denied = new ProcessDetailsResult(
            null, ProcessInfoStatus.AccessDenied,
            null, ProcessInfoStatus.Exited);

        EqualText("（拒绝访问）", denied.PathDisplay, "path denied");
        EqualText("（进程已退出）", denied.UserDisplay, "user exited");
        True(denied.HasAccessDenied, "details access denied flag");

        var ok = new ProcessDetailsResult(
            @"C:\Windows\notepad.exe", ProcessInfoStatus.Ok,
            @"NT AUTHORITY\SYSTEM", ProcessInfoStatus.Ok);

        EqualText(@"C:\Windows\notepad.exe", ok.PathDisplay, "path ok");
        EqualText(@"NT AUTHORITY\SYSTEM", ok.UserDisplay, "user ok");
        True(!ok.HasAccessDenied, "details ok flag");
    }

    private static void DetailsErrorClassification()
    {
        Equal((int)ProcessInfoStatus.AccessDenied,
            (int)ProcessDetailsReader.ClassifyWin32Error(5), "details access denied");
        Equal((int)ProcessInfoStatus.Exited,
            (int)ProcessDetailsReader.ClassifyWin32Error(87), "details invalid parameter");
        Equal((int)ProcessInfoStatus.Exited,
            (int)ProcessDetailsReader.ClassifyWin32Error(1168), "details not found");
        Equal((int)ProcessInfoStatus.Error,
            (int)ProcessDetailsReader.ClassifyWin32Error(12345), "details unknown");
    }

    private static void FileTimeFormatting()
    {
        var utc = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        long fileTime = utc.ToFileTimeUtc();

        DateTime? local = ProcessTime.FromFileTime(fileTime);
        True(local.HasValue, "file time parsed");
        EqualText(local.GetValueOrDefault().ToString("yyyy-MM-dd HH:mm:ss"), ProcessTime.Format(fileTime), "file time format");
        True(ProcessTime.FromFileTime(0) is null, "zero file time");
    }

    private static void ExplorerSelectArguments()
    {
        EqualText(
            "/select,\"C:\\Program Files\\demo.exe\"",
            ExplorerLauncher.BuildSelectArguments(@"C:\Program Files\demo.exe"),
            "explorer select args");

        True(!ExplorerLauncher.TryOpen(null, out string error) && error.Length > 0, "null path rejected");
        True(!ExplorerLauncher.TryOpen(@"C:\__taskmgr_no_such_file__.exe", out _), "missing file rejected");
    }

    private static void RowStartTimeDisplay()
    {
        var row = new ProcessRowViewModel(
            1, 0, "demo.exe", 0, 0,
            new DateTime(2026, 11, 22, 10, 30, 45));

        EqualText("2026-11-22 10:30:45", row.StartTimeDisplay, "row start time");
    }

    private static void CollectorDisposeGuard()
    {
        bool disposed = false;

        // Simulate Join(2000) timing out: worker still alive.
        True(!ProcessCollector.SafeDispose(false, () => disposed = true), "worker alive must not dispose");
        True(!disposed, "dispose action must not run");

        // Worker exited: resources may be released once.
        True(ProcessCollector.SafeDispose(true, () => disposed = true), "worker exited may dispose");
        True(disposed, "dispose action must run");
    }

    private static void CommandLineBufferGrow()
    {
        IntPtr buffer = new(12345);
        int capacity = 123;
        bool released = false;

        // Simulate OOM in AllocHGlobal: old pointer and capacity must survive.
        bool threw = false;
        try
        {
            CommandLineReader.GrowBuffer(
                ref buffer,
                ref capacity,
                8192,
                _ => throw new OutOfMemoryException(),
                _ => released = true);
        }
        catch (OutOfMemoryException)
        {
            threw = true;
        }

        True(threw, "OOM should propagate");
        True(buffer == new IntPtr(12345), "old buffer must not change");
        Equal(123, capacity, "old capacity must not change");
        True(!released, "old buffer must not be released on failed alloc");

        // Success path: allocate new, then release old.
        IntPtr grown = new(67890);
        IntPtr releasedPointer = IntPtr.Zero;
        bool releasedAfterAlloc = false;

        CommandLineReader.GrowBuffer(
            ref buffer,
            ref capacity,
            5000,
            _ => { releasedAfterAlloc = !released; return grown; },
            pointer => releasedPointer = pointer);

        True(buffer == grown, "buffer should be replaced");
        Equal(5000, capacity, "capacity should grow");
        True(releasedAfterAlloc, "release must happen after successful alloc");
        True(releasedPointer == new IntPtr(12345), "old pointer should be released");
    }

    private static void SnapshotSlotOwnership()
    {
        int released = 0;
        ProcessSnapshot Make() => new(
            new ProcessSample[1], 0, 0, 0,
            _ => released++);

        var slot = new SnapshotSlot();
        ProcessSnapshot first = Make();
        ProcessSnapshot second = Make();

        slot.Publish(first);
        slot.Publish(second);
        Equal(1, released, "publish releases old snapshot");

        ProcessSnapshot? taken = slot.Take();
        True(ReferenceEquals(taken, second), "take returns latest");
        True(slot.Take() is null, "slot empty after take");

        taken!.Release();
        Equal(2, released, "ui releases taken snapshot");
        taken.Release();
        Equal(2, released, "release is idempotent");

        ProcessSnapshot third = Make();
        slot.Publish(third);
        slot.Discard();
        Equal(3, released, "discard releases latest");
        slot.Discard();
        Equal(3, released, "second discard is a no-op");
        True(slot.Take() is null, "discard clears slot");
    }

    private static void ParseIntoBounds()
    {
        const int header = ProcessEnumerator.HeaderSize;
        const int nameOffset = header;
        const int capacity = header + 32;

        IntPtr buffer = Marshal.AllocHGlobal(capacity);

        try
        {
            Marshal.Copy(new byte[capacity], 0, buffer, capacity);

            // One valid header with a name buffer inside the allocation.
            Marshal.WriteInt32(buffer, 0x00, 0);          // NextEntryOffset
            Marshal.WriteInt32(buffer, 0x04, 3);          // ThreadCount
            Marshal.WriteInt64(buffer, 0x08, 111);        // WorkingSetPrivate
            Marshal.WriteInt64(buffer, 0x20, 222);        // CreateTime
            Marshal.WriteInt16(buffer, 0x38, 4);          // ImageName.Length bytes
            Marshal.WriteIntPtr(buffer, 0x40, IntPtr.Add(buffer, nameOffset));
            Marshal.WriteIntPtr(buffer, 0x50, (IntPtr)123); // Pid
            Marshal.WriteIntPtr(buffer, 0x58, (IntPtr)4);   // ParentPid
            Marshal.WriteInt32(buffer, 0x60, 77);         // HandleCount
            Marshal.WriteInt64(buffer, 0x90, 333);        // WorkingSet
            Marshal.WriteInt16(buffer, nameOffset, (short)'a');
            Marshal.WriteInt16(buffer, nameOffset + 2, (short)'b');

            var entries = new RawProcessEntry[1];
            int count = ProcessEnumerator.ParseInto(buffer, capacity, ref entries);

            Equal(1, count, "valid entry count");
            Equal(123, (int)entries[0].Pid, "parsed pid");
            Equal(4, (int)entries[0].ParentPid, "parsed parent");
            Equal(222, (int)entries[0].CreateTime, "parsed create time");
            Equal(4, entries[0].NameByteLength, "parsed name length");
            True(entries[0].NamePointer == IntPtr.Add(buffer, nameOffset), "parsed name pointer");

            // Header truncated: no field may be read.
            var shortEntries = new RawProcessEntry[1];
            Equal(0, ProcessEnumerator.ParseInto(buffer, header - 1, ref shortEntries), "truncated header");

            // Name pointer outside the buffer must be zeroed.
            Marshal.WriteIntPtr(buffer, 0x40, IntPtr.Add(buffer, capacity));
            var dirtyEntries = new RawProcessEntry[1];
            Equal(1, ProcessEnumerator.ParseInto(buffer, capacity, ref dirtyEntries), "dirty name entry");
            Equal(0, dirtyEntries[0].NameByteLength, "dirty name length zeroed");
            True(dirtyEntries[0].NamePointer == IntPtr.Zero, "dirty name pointer zeroed");

            // NextEntryOffset pointing inside the fixed header must stop the walk.
            Marshal.WriteIntPtr(buffer, 0x40, IntPtr.Add(buffer, nameOffset));
            Marshal.WriteInt16(buffer, 0x38, 4);
            Marshal.WriteInt32(buffer, 0x00, 0x10);
            var badOffsetEntries = new RawProcessEntry[1];
            Equal(1, ProcessEnumerator.ParseInto(buffer, capacity, ref badOffsetEntries), "bad next offset stops after one");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void BulkVisibleSync()
    {
        var target = new BulkObservableCollection<TestRow>();
        var byKey = new Dictionary<ProcKey, TestRow>();
        var scratch = new HashSet<ProcKey>();
        var bulkBuffer = new List<TestRow>();

        TestRow Resolve(ProcKey key)
        {
            if (!byKey.TryGetValue(key, out TestRow? row))
            {
                row = new TestRow(key);
                byKey[key] = row;
            }
            return row;
        }

        for (uint i = 1; i <= 5; i++)
            target.Add(Resolve(new ProcKey(i, 1)));

        int resets = 0;
        target.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                resets++;
        };

        var desired = new List<ProcKey>();
        for (uint i = 1; i <= 100; i++)
            desired.Add(new ProcKey(i, 1));

        int bulkCalls = 0;
        VisibleListSync.Apply(
            target,
            desired,
            r => r.Key,
            Resolve,
            scratch,
            bulkThreshold: 32,
            bulkBuffer: bulkBuffer,
            replaceAll: items =>
            {
                bulkCalls++;
                target.ReplaceAll(items);
            });

        Equal(100, target.Count, "bulk target count");
        Equal(1, bulkCalls, "bulk replace called once");
        Equal(1, resets, "bulk reset raised once");

        for (int i = 0; i < desired.Count; i++)
            True(target[i].Key.Equals(desired[i]), $"bulk order at {i}");
    }

    private static void FakeDataNativeGuard()
    {
        True(!MainViewModel.ShouldLoadNative(useRealData: false), "fake mode must not hit native");
        True(MainViewModel.ShouldLoadNative(useRealData: true), "real mode may hit native");

        // 默认（无参数）必须是真实数据；只有显式 --fake 才进入假数据模式。
        True(App.ResolveUseRealData(Array.Empty<string>()), "no args means real data");
        True(App.ResolveUseRealData(new[] { "--stats" }), "--stats alone stays real");
        True(App.ResolveUseRealData(new[] { "--real" }), "--real stays real");
        True(App.ResolveUseRealData(new[] { "--real", "--fake" }), "--real wins over --fake");
        True(!App.ResolveUseRealData(new[] { "--fake" }), "--fake selects fake mode");
        True(!App.ResolveUseRealData(new[] { "--FAKE" }), "flag match is case-insensitive");

        var row = new ProcessRowViewModel(1000, 0, "fake.exe", 0, 1, DateTime.Now)
        {
            CommandLine = "\"C:\\Fake\\fake.exe\"",
            ImagePath = @"C:\Fake\fake.exe",
            UserName = "fake-user"
        };

        EqualText("\"C:\\Fake\\fake.exe\"", row.CommandLine, "fake command line stays local");
        EqualText(@"C:\Fake\fake.exe", row.ImagePath, "fake path stays local");
        EqualText("fake-user", row.UserName, "fake user stays local");
    }

    // ---------------- helpers ----------------

    private sealed class TestRow
    {
        public TestRow(ProcKey key) => Key = key;
        public ProcKey Key { get; }
    }

    private static NodeRef[] Nodes(params (uint Pid, uint Parent)[] items)
    {
        var nodes = new NodeRef[items.Length];
        for (int i = 0; i < items.Length; i++)
            nodes[i] = new NodeRef(new ProcKey(items[i].Pid, 1), items[i].Parent);
        return nodes;
    }

    private static List<FlatNode> FlatRows(params (uint Pid, int Depth)[] rows)
    {
        var flat = new List<FlatNode>(rows.Length);
        for (int i = 0; i < rows.Length; i++)
            flat.Add(new FlatNode(new ProcKey(rows[i].Pid, 1), rows[i].Depth, true));
        return flat;
    }

    private static string NewTempRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "TaskMgrLite-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private static List<FlatNode> Flatten(
        NodeRef[] nodes,
        HashSet<ProcKey> collapsed,
        HashSet<ProcKey>? parentsWithChildren = null)
    {
        var output = new List<FlatNode>(nodes.Length);
        new TreeFlattener().Flatten(nodes, nodes.Length, collapsed, output, parentsWithChildren);
        return output;
    }

    /// <summary>显式传 ProcKey，避免 params + 命名实参的歧义。</summary>
    private static List<FlatNode> Flatten(NodeRef[] nodes, ProcKey? collapsedKey = null)
    {
        var collapsed = new HashSet<ProcKey>();
        if (collapsedKey is ProcKey key)
            collapsed.Add(key);

        if (collapsedKey.HasValue)
            True(collapsed.Count == 1, "helper collapsed set");

        var output = new List<FlatNode>();
        new TreeFlattener().Flatten(nodes, nodes.Length, collapsed, output);
        return output;
    }

    private static void ExpectPids(uint[] expected, List<FlatNode> actual, string what)
    {
        // 失败时把完整序列和深度打出来，一次运行就能定位，不用再来一轮
        string expectedText = string.Join(",", expected);
        string actualText = string.Join(",", actual.Select(n => $"{n.Key.Pid}@{n.Depth}"));

        if (actual.Count != expected.Length)
            throw new Exception($"{what}: expected [{expectedText}], got [{actualText}]");

        for (int i = 0; i < expected.Length; i++)
        {
            if (actual[i].Key.Pid != expected[i])
                throw new Exception($"{what}: expected [{expectedText}], got [{actualText}]");
        }
    }

    private static void ExpectKeyPids(uint[] expected, List<ProcKey> actual, string what)
    {
        if (actual.Count != expected.Length)
            throw new Exception($"{what}: expected {expected.Length} keys, got {actual.Count}");

        for (int i = 0; i < expected.Length; i++)
        {
            if (actual[i].Pid != expected[i])
                throw new Exception($"{what}: row {i} expected pid {expected[i]}, got {actual[i].Pid}");
        }
    }

    private static void EqualText(string expected, string actual, string what)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Exception($"{what}: expected [{expected}], got [{actual}]");
    }

    private static void ExpectDepths(int[] expected, List<FlatNode> actual, string what)
    {
        for (int i = 0; i < expected.Length; i++)
            if (actual[i].Depth != expected[i])
                throw new Exception($"{what}: row {i} expected depth {expected[i]}, got {actual[i].Depth}");
    }

    private static void ExpectRowPids(uint[] expected, List<TestRow> rows, string what)
    {
        if (rows.Count != expected.Length)
            throw new Exception($"{what}: expected {expected.Length} rows, got {rows.Count}");

        for (int i = 0; i < expected.Length; i++)
            if (rows[i].Key.Pid != expected[i])
                throw new Exception($"{what}: row {i} expected pid {expected[i]}, got {rows[i].Key.Pid}");
    }

    private static void Equal(int expected, int actual, string what)
    {
        if (expected != actual)
            throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    private static void True(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    private static void Check(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Log.WriteLine($"[test] PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Log.WriteLine($"[test] FAIL  {name}: {ex.Message}");
        }
    }
}
