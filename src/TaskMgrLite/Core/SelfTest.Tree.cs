using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TaskMgrLite.ViewModels;

namespace TaskMgrLite.Core;


/// <summary>
/// 树构建、折叠、选中迁移、可见序列、随机森林与进程树杀计划。
/// </summary>
public static partial class SelfTest
{
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
}
