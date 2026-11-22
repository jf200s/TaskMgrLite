namespace TaskMgrLite.Core;

/// <summary>
/// 进程的稳定 identity。PID 会被复用，必须带创建时间。
/// </summary>
public readonly record struct ProcKey(uint Pid, long CreateTime);

/// <summary>扁平化输入：一个节点只需要 identity 和父 PID。</summary>
public readonly record struct NodeRef(ProcKey Key, uint ParentPid);

/// <summary>扁平化输出：可见行 + 缩进深度 + 是否有子节点。</summary>
public readonly record struct FlatNode(ProcKey Key, int Depth, bool HasChildren);

public enum ProcessViewMode
{
    Tree,
    Flat
}

/// <summary>
/// 折叠时的选中迁移判断。展平结果是前序：祖先之后、深度仍大于祖先的连续行
/// 就是它的子树。只在用户折叠时调用，O(n)，不进入 1Hz 热路径。
/// </summary>
public static class TreeSelection
{
    public static bool ShouldMoveSelection(
        IReadOnlyList<FlatNode> flat,
        ProcKey collapsed,
        ProcKey? selected)
    {
        if (selected is not ProcKey candidateKey)
            return false;

        int ancestor = IndexOf(flat, collapsed);
        if (ancestor < 0)
            return false;

        int candidate = IndexOf(flat, candidateKey);
        if (candidate <= ancestor)
            return false;

        int depth = flat[ancestor].Depth;
        for (int i = ancestor + 1; i <= candidate; i++)
        {
            if (flat[i].Depth <= depth)
                return false;
        }

        return true;
    }

    private static int IndexOf(IReadOnlyList<FlatNode> flat, ProcKey key)
    {
        for (int i = 0; i < flat.Count; i++)
        {
            if (flat[i].Key.Equals(key))
                return i;
        }

        return -1;
    }
}

/// <summary>
/// 把父子关系压成可见行序列（DFS 前序）。
///
/// 复杂度 O(n)：计数排序建 children 索引，显式栈做 DFS。
/// 所有缓冲跨调用复用；稳定状态下每次调用零托管分配。
///
/// 五个边界：
///   1. ParentPid == 0 → 根。Windows 里 Idle 进程本身就是 PID 0，
///      不能因为“PID 0 在表里”就把所有孤儿挂到 Idle 下面；
///   2. 父进程不存在（已退出）→ 该节点当根处理；
///   3. PID 复用 → 父进程的创建时间必须早于子进程，否则视为孤儿。
///      NtQuerySystemInformation 只给 InheritedFromUniqueProcessId，不给父创建时间，
///      这是快照内唯一能做的防串线校验；
///   4. 成环（理论上只可能来自脏数据）→ 访问标记 + 兜底补发，保证每个节点最多出现一次；
///   5. 折叠只影响“是否展开子节点”，节点自己被折叠时仍然可见。
/// </summary>
public sealed class TreeFlattener
{
    private readonly Dictionary<uint, int> _pidToIndex = new(1024);

    private int[] _childStart = new int[1025];
    private int[] _childCount = new int[1024];
    private int[] _cursor = new int[1024];
    private int[] _childIndex = new int[2048];
    private int[] _roots = new int[1024];
    private int[] _stackNode = new int[1025];
    private int[] _stackDepth = new int[1025];
    private byte[] _visited = new byte[1024];
    private byte[] _reachable = new byte[1024];

    /// <param name="parentsWithChildren">
    /// 可选。填成“当前快照里至少有一个合法子节点的进程 key”。
    /// 折叠集合用它做叶子剪枝：没有子节点的折叠状态没有意义，必须释放。
    /// 集合由调用方复用，方法内部只 Clear + Add，不新建对象。
    /// </param>
    public void Flatten(
        NodeRef[] nodes,
        int count,
        HashSet<ProcKey> collapsed,
        List<FlatNode> output,
        HashSet<ProcKey>? parentsWithChildren = null)
    {
        output.Clear();
        parentsWithChildren?.Clear();
        if (count <= 0) return;

        int rootCount = BuildIndex(nodes, count, parentsWithChildren);

        // ---- pass 1：结构可达性（忽略折叠）----
        // 必须单独算一遍。否则成环兜底会把「因为折叠而没发」的子树当成孤儿重新发出来，
        // 表现为「折叠了但子进程还在」。
        Array.Clear(_reachable, 0, count);
        int sp = 0;

        for (int r = rootCount - 1; r >= 0; r--)
        {
            _stackNode[sp] = _roots[r];
            _stackDepth[sp] = 0;
            sp++;
        }

        while (sp > 0)
        {
            sp--;
            int node = _stackNode[sp];

            if (_reachable[node] != 0) continue;
            _reachable[node] = 1;

            int childCount = _childCount[node];
            for (int c = _childStart[node] + childCount - 1; c >= _childStart[node]; c--)
            {
                _stackNode[sp] = _childIndex[c];
                _stackDepth[sp] = 0;
                sp++;
            }
        }

        // ---- pass 2：正常展开，折叠在这里生效 ----
        Array.Clear(_visited, 0, count);
        sp = 0;

        for (int r = rootCount - 1; r >= 0; r--)
        {
            _stackNode[sp] = _roots[r];
            _stackDepth[sp] = 0;
            sp++;
        }

        Drain(ref sp, nodes, collapsed, output);

        // ---- pass 3：成环兜底，只处理结构上就不可达的节点 ----
        for (int i = 0; i < count; i++)
        {
            if (_reachable[i] != 0 || _visited[i] != 0) continue;

            sp = 0;
            _stackNode[sp] = i;
            _stackDepth[sp] = 0;
            sp++;
            Drain(ref sp, nodes, collapsed, output);
        }
    }

    /// <summary>
    /// 建索引：pid → index、childCount、children 区间、roots。
    /// Flatten 与杀进程树的后序计划共用这一套父关系判定，避免两套规则漂移。
    /// </summary>
    private int BuildIndex(NodeRef[] nodes, int count, HashSet<ProcKey>? parentsWithChildren)
    {
        EnsureCapacity(count);

        // 同一 PID 理论上只出现一次；若脏数据里重复，保留创建时间最早的那个做父候选，
        // 避免“新进程顶替旧进程当爹”。
        _pidToIndex.Clear();
        for (int i = 0; i < count; i++)
        {
            uint pid = nodes[i].Key.Pid;
            if (!_pidToIndex.TryGetValue(pid, out int existing) ||
                nodes[i].Key.CreateTime < nodes[existing].Key.CreateTime)
            {
                _pidToIndex[pid] = i;
            }
        }

        Array.Clear(_childCount, 0, count);

        int rootCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (TryResolveParent(nodes, i, out int parent))
            {
                _childCount[parent]++;
                parentsWithChildren?.Add(nodes[parent].Key);
            }
            else
            {
                _roots[rootCount++] = i;
            }
        }

        // 前缀和 → 每个父节点的子节点区间
        int running = 0;
        for (int i = 0; i < count; i++)
        {
            _childStart[i] = running;
            running += _childCount[i];
        }
        _childStart[count] = running;

        for (int i = 0; i < count; i++) _cursor[i] = 0;

        for (int i = 0; i < count; i++)
        {
            if (TryResolveParent(nodes, i, out int parent))
            {
                int slot = _childStart[parent] + _cursor[parent]++;
                _childIndex[slot] = i;
            }
        }

        return rootCount;
    }

    /// <summary>
    /// 收集以 root 为根的整棵子树，按“子节点先于父节点”的顺序输出。
    ///
    /// 实现是 DFS 前序 + 反转。反转前序保证每个后代都在祖先之前；
    /// 兄弟之间顺序相反，但杀进程树不关心兄弟顺序。
    /// 与折叠状态无关：UI 上折叠的子进程也必须被杀掉。
    /// </summary>
    /// <returns>false 表示 root 已不在当前快照里，或 PID 已被新进程复用。</returns>
    public bool TryCollectSubtreePostOrder(
        NodeRef[] nodes,
        int count,
        ProcKey root,
        List<ProcKey> output)
    {
        output.Clear();
        if (count <= 0 || root.Pid == 0) return false;

        BuildIndex(nodes, count, parentsWithChildren: null);

        if (!_pidToIndex.TryGetValue(root.Pid, out int rootIndex))
            return false;

        if (!nodes[rootIndex].Key.Equals(root))
            return false;   // 同 PID 但创建时间不同：旧进程已退出，不能杀新进程

        Array.Clear(_reachable, 0, count);
        int sp = 0;
        _stackNode[sp++] = rootIndex;

        while (sp > 0)
        {
            int node = _stackNode[--sp];

            if (_reachable[node] != 0) continue;
            _reachable[node] = 1;
            output.Add(nodes[node].Key);

            int childCount = _childCount[node];
            for (int c = _childStart[node] + childCount - 1; c >= _childStart[node]; c--)
                _stackNode[sp++] = _childIndex[c];
        }

        output.Reverse();
        return true;
    }

    /// <summary>
    /// 判断 nodes[index] 的父候选是否合法。三个拒绝条件：
    ///   - ParentPid == 0：没有父进程。注意 Idle 进程自身就是 PID 0，不能反查它；
    ///   - 父候选是节点自己；
    ///   - 父候选的创建时间晚于子进程：PID 被复用，旧父已死，子进程应升为根。
    /// </summary>
    private bool TryResolveParent(NodeRef[] nodes, int index, out int parent)
    {
        parent = -1;

        uint parentPid = nodes[index].ParentPid;
        if (parentPid == 0) return false;

        if (!_pidToIndex.TryGetValue(parentPid, out parent))
            return false;

        if (parent == index)
        {
            parent = -1;
            return false;
        }

        long parentStart = nodes[parent].Key.CreateTime;
        long childStart = nodes[index].Key.CreateTime;

        // 0 表示快照没给创建时间；这时只按 PID 匹配，不误杀。
        if (parentStart != 0 && childStart != 0 && parentStart > childStart)
        {
            parent = -1;
            return false;
        }

        return true;
    }

    private void Drain(ref int sp, NodeRef[] nodes, HashSet<ProcKey> collapsed, List<FlatNode> output)
    {
        while (sp > 0)
        {
            sp--;
            int node = _stackNode[sp];
            int depth = _stackDepth[sp];

            if (_visited[node] != 0) continue;
            _visited[node] = 1;

            int childCount = _childCount[node];
            ProcKey key = nodes[node].Key;

            output.Add(new FlatNode(key, depth, childCount > 0));

            if (childCount == 0) continue;
            if (collapsed.Contains(key)) continue;

            // 倒序压栈，保证第一个子节点先出栈
            for (int c = _childStart[node] + childCount - 1; c >= _childStart[node]; c--)
            {
                _stackNode[sp] = _childIndex[c];
                _stackDepth[sp] = depth + 1;
                sp++;
            }
        }
    }

    private void EnsureCapacity(int count)
    {
        if (_childCount.Length < count)
        {
            int size = Math.Max(count, _childCount.Length * 2);
            Array.Resize(ref _childCount, size);
            Array.Resize(ref _cursor, size);
            Array.Resize(ref _roots, size);
            Array.Resize(ref _visited, size);
            Array.Resize(ref _reachable, size);
        }

        if (_childStart.Length < count + 1)
            Array.Resize(ref _childStart, Math.Max(count + 1, _childStart.Length * 2));

        if (_childIndex.Length < count)
            Array.Resize(ref _childIndex, Math.Max(count, _childIndex.Length * 2));

        if (_stackNode.Length < count + 1)
        {
            Array.Resize(ref _stackNode, Math.Max(count + 1, _stackNode.Length * 2));
            Array.Resize(ref _stackDepth, _stackNode.Length);
        }
    }
}

/// <summary>
/// 折叠状态维护。
///
/// 存的是“被显式折叠”的集合，而不是“展开”的集合。默认展开有三个好处：
///   - 新进程出生时天然可见，不需要往集合里写任何东西；
///   - 出生不会让集合增长，只有用户点击折叠才会；
///   - 集合大小上界 = 活着且当前有子进程的节点数，不是历史进程总数。
///
/// 每次快照后调用一次 Prune。shouldRemove 应同时覆盖两种情况：
///   1. key 已不在 alive 集合里（进程退出 / PID 复用成新 key）；
///   2. key 对应节点当前没有任何合法子节点（最后一个子进程退出）。
/// 第 2 条让“折叠”只在有东西可折叠时存在，避免幽灵状态。
/// </summary>
public static class CollapseState
{
    /// <param name="shouldRemove">必须是缓存的委托，避免每帧分配闭包。</param>
    /// <returns>被移除的 key 数，可用来判断是否需要刷新状态文本。</returns>
    public static int Prune(HashSet<ProcKey> collapsed, Predicate<ProcKey> shouldRemove)
        => collapsed.RemoveWhere(shouldRemove);

    /// <summary>
    /// 只在“当前确实有合法子节点”时接受折叠切换。
    /// 这一层用于挡住“点击落到上一帧旧行”的竞态：
    /// 进程已经退出、或最后一个子进程刚退出时，key 不在 parentsWithChildren 里，直接拒绝。
    /// </summary>
    /// <returns>true 表示状态确实翻转了；false 表示这是一次无效点击。</returns>
    public static bool Toggle(
        HashSet<ProcKey> collapsed,
        ProcKey key,
        HashSet<ProcKey> parentsWithChildren)
    {
        // 目标状态：当前已折叠 → 展开；当前已展开 → 折叠。
        return Set(collapsed, key, expand: collapsed.Contains(key), parentsWithChildren);
    }

    /// <summary>
    /// 显式设置展开状态。返回 false 表示没有变化：key 没有子节点、已经处于目标状态，
    /// 或者这是一次落在上一帧旧行上的无效操作。
    /// </summary>
    public static bool Set(
        HashSet<ProcKey> collapsed,
        ProcKey key,
        bool expand,
        HashSet<ProcKey> parentsWithChildren)
    {
        if (!parentsWithChildren.Contains(key))
            return false;

        return expand ? collapsed.Remove(key) : collapsed.Add(key);
    }
}

/// <summary>
/// 把“想要的可见序列”同步到实际集合上，只做必要的插入/移动/删除。
///
/// 正常情况下树上只有三种变化：新增节点、删除节点、整棵子树展开/折叠，
/// 所以两指针一遍就能处理完；只有“父进程退出导致子树改挂到根”这种少见情况
/// 才会走到线性查找 + 移动。
/// </summary>
public static class VisibleListSync
{
    public static void Apply<T>(
        IList<T> target,
        List<ProcKey> desiredKeys,
        Func<T, ProcKey> keyOf,
        Func<ProcKey, T> resolve,
        HashSet<ProcKey> scratch,
        int bulkThreshold = 32,
        List<T>? bulkBuffer = null,
        Action<IList<T>>? replaceAll = null)
    {
        // 批量展开/折叠：逐条 Insert 会触发几百次 CollectionChanged，
        // ListCollectionView 会同步重建/重排，直接把 UI 卡死。
        // 数量差超过阈值时整表 Reset 一次。
        if (bulkBuffer is not null &&
            replaceAll is not null &&
            Math.Abs(desiredKeys.Count - target.Count) > bulkThreshold)
        {
            bulkBuffer.Clear();
            for (int i = 0; i < desiredKeys.Count; i++)
                bulkBuffer.Add(resolve(desiredKeys[i]));

            replaceAll(bulkBuffer);
            return;
        }

        scratch.Clear();
        for (int i = 0; i < desiredKeys.Count; i++)
            scratch.Add(desiredKeys[i]);

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!scratch.Contains(keyOf(target[i])))
                target.RemoveAt(i);
        }

        int t = 0;

        for (int i = 0; i < desiredKeys.Count; i++)
        {
            ProcKey key = desiredKeys[i];

            if (t < target.Count && keyOf(target[t]).Equals(key))
            {
                t++;
                continue;
            }

            int existing = -1;
            for (int j = t + 1; j < target.Count; j++)
            {
                if (keyOf(target[j]).Equals(key))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                T item = target[existing];
                target.RemoveAt(existing);
                target.Insert(t, item);
            }
            else
            {
                target.Insert(t, resolve(key));
            }

            t++;
        }
    }
}
