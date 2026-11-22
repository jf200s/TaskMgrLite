# Round 2：树形结构与折叠

本文只覆盖 Round 2 的树状态机。采集、CPU 差值、命令行懒加载仍按 Round 0/1 的口径，不在本轮改动。

## 1. 目标与非目标

目标：

- 300+ 进程全展开时，首屏与每次 1Hz 刷新的展平成本保持 O(n)。
- 展开/折叠在一帧内完成（16ms 预算）。
- 不引入 WPF TreeView，不破坏 ListView 虚拟化。
- 不破坏 0.1MB/s 的分配率底线。

非目标（后续轮次）：

- 键盘树导航、拖拽、多选。
- 进程分组（按应用/服务）。
- 命令行列的真实数据（Round 3）。
- 结束进程树（需要单独的错误报告状态机）。

## 2. 状态分层

```
不可变快照 (1Hz, 采集线程)
        │  原子交换，UI 线程取走
        ▼
RowTable: ProcKey -> ProcessRowViewModel
        │  只改 CPU / 内存 / 句柄，不动树状态
        ▼
TreeIndex: pid -> children 区间 + parentsWithChildren
        │  每个快照重建一次，O(n)
        ▼
CollapsedSet: HashSet<ProcKey>（用户意图）
        │  和快照完全解耦
        ▼
VisibleList: List<ProcessRowViewModel>（派生结果）
        │  只做必要的插入/移动/删除
        ▼
ListView 虚拟化
```

四条硬规矩：

1. 树状态只存在于 UI 线程。
2. 快照只负责“结构是什么、数值是多少”，不负责“哪些节点展开”。
3. `ProcKey = (Pid, CreateTime)`，PID 复用不会串行。
4. 可见行永远是派生物，不反向修改树状态。

## 3. 为什么存 collapsed，而不是 expanded

默认是展开的，所以集合记录“被用户显式折叠”的节点：

- 新进程出生时天然可见，不需要往集合里写任何东西。
- 集合只在用户点击时增长，不会因为进程 churn 而增长。
- 集合大小上界是“活着且当前至少有一个合法子节点的进程数”，不是历史进程总数。

如果反过来存 expanded，每次出生都要插入、每次死亡都要删除，集合会跟着进程 churn 反复扩容，而且“新进程默认是什么状态”要靠额外规则补。

## 4. O(n) 展平算法

实现在 `Core/TreeModel.cs` 的 `TreeFlattener.Flatten`。分两段：

### 4.1 建索引（O(n)）

1. 扫描一遍，建立 `pid -> nodeIndex`。同一 PID 若有重复脏数据，保留 `CreateTime` 最早的那个作为父候选。
2. 再扫描一遍，用 `TryResolveParent` 判定每个节点的父是否合法：
   - 合法：父节点 `_childCount[parent]++`，并把父 key 写进 `parentsWithChildren`。
   - 不合法：该节点进入 `_roots`。
3. 对 `_childCount` 做前缀和，得到每个父节点的子节点区间 `[childStart, childStart + childCount)`。
4. 再扫描一遍，把子节点索引填进 `_childIndex`。因为按 nodeIndex 升序扫描，兄弟顺序就是快照顺序，稳定且不需要排序。

所有数组和 `Dictionary` 都是字段级复用，跨调用不重新分配。

### 4.2 DFS 前序（O(n)）

用显式栈，不递归，避免深链爆栈。关键是分三遍：

1. **结构可达性**：忽略折叠，从根做一次 DFS，标记 `_reachable`。
2. **正常展开**：从根做第二次 DFS，遇到 `collapsed.Contains(key)` 就不再压子节点，但节点自己仍然输出。
3. **成环兜底**：只处理“结构上不可达、且还没输出”的节点。每个环只会被输出一次。

第 1 遍不能省。否则第 3 遍会把“因为祖先折叠而没输出”的子树当成孤儿重新发出来，表现为“折叠了但子进程还在”。

复杂度：建索引 4 遍 + DFS 3 遍，全部 O(n)；缓冲区 O(n)。稳定状态下每次调用零托管分配。

## 5. 父关系判定的五个边界

`TryResolveParent` 按顺序拒绝：

1. **`ParentPid == 0`**：没有父进程。Windows 的 Idle 进程本身就是 PID 0，不能因为“PID 0 在表里”就把所有 `ParentPid == 0` 的进程挂到 Idle 下面。这是本轮修掉的一个真 bug。
2. **父 PID 不在快照里**：父进程已退出，当前节点当根。
3. **父候选是节点自己**：当根。
4. **父候选的 `CreateTime` 晚于子进程**：PID 被复用，旧父已死，子进程当根。`NtQuerySystemInformation` 只给 `InheritedFromUniqueProcessId`，不给父创建时间；这是快照内唯一能做的防串线校验。
5. **重复 PID**：保留创建时间最早的节点作为父候选，避免新进程顶替旧进程当爹。

创建时间为 0（快照没给）时跳过第 4 条，只按 PID 匹配，避免误杀。

## 6. collapsed 生命周期

每次应用快照：

1. 用 `_aliveKeys` 剪掉已退出 / 被 PID 复用顶掉的 key。
2. 树模式下展平，同时重建 `_parentsWithChildren`。
3. 展平结束后，用 `_parentsWithChildren` 剪掉“当前没有子节点”的 key。

第 3 条对应产品口径：**被折叠父进程的最后一个子进程退出后，折叠状态自动清除**。理由：

- 没有子节点的折叠状态没有意义。
- 避免“父进程已经变叶子，但集合里还记着折叠”的幽灵状态。
- 未来再有子进程出生时，父进程按默认展开处理，不会出现“新子进程被一个看不见的历史状态藏起来”。

集合容量是历史峰值，不会主动缩容；但元素个数严格有界，不随运行时间增长。

## 7. 状态机边界

所有边界都发生在 UI 线程。WPF Dispatcher 保证“应用快照”和“点击展开器”不会真正并发，但点击可能落在上一帧的行对象上。

| 事件 | 处理 | 可见结果 |
|---|---|---|
| 父进程已折叠，子进程此刻启动 | 新 key 不在 collapsed 里；展平时父节点命中折叠，不压子节点 | 子进程保持隐藏；父节点 `HasChildren=true`，显示 ▸ |
| 父进程折叠期间，子进程退出 | 子进程从 RowTable 移除；它本来就不在 VisibleList 里，所以列表无变化 | 可见行不变；若退出的是最后一个子进程，父节点折叠状态被释放 |
| 最后一个子进程退出 | 展平后 `parentsWithChildren` 不含父 key，`CollapseState.Prune` 移除 | 父节点变回普通叶子；未来新子进程默认可见 |
| 父进程折叠期间，父进程自己退出 | 父 key 被 alive 剪枝移除；子进程因父 PID 不存在升为根 | 子进程以 depth=0 立即出现；即使它曾被父节点藏着 |
| 点击落在上一帧的旧行 | `ToggleExpanded` 先查 `_rowsByKey`，再查 `_parentsWithChildren`；任一不满足直接拒绝 | 不会把已退出/已变叶子的 key 塞回集合 |
| 父 PID 被新进程复用 | `TryResolveParent` 比较创建时间，新父晚于子 → 视为孤儿 | 子进程升为根；不会突然被挂到一个折叠的新父下面 |
| 成环脏数据 | 结构可达性 + 访问标记 + 兜底补发 | 每个节点最多出现一次，不会死循环 |
| 搜索 / 点列头排序 | 排序即切平铺；搜索强制平铺 | 树序和列排序互斥；collapsed 集合保留，切回树时恢复 |

`CollapseState.Toggle` 只接受 `parentsWithChildren` 里的 key，这是状态机的最后一道闸。ViewModel 额外检查 `_rowsByKey`，挡住“进程已经退出、WPF 容器还没回收”的点击。

## 8. 可见行同步

`VisibleListSync.Apply` 把“期望的 key 序列”同步到 `ObservableCollection`：

1. 用期望 key 集合删掉目标里多余的项。
2. 两个指针从左往右匹配；遇到顺序不对的项，在剩余区间找它并移动到当前位置；找不到就插入。

正常情况下 1Hz 刷新只有少量出生/死亡，成本接近 O(n)。展开/折叠大子树时最坏是 O(n²) 的元素移动；n≈500 时实测约 29µs，远低于一帧预算，所以本轮不为它引入 ListView 不支持的批量 Add 通知。首屏从 0 到满仍走一次 Reset，避免 500 次逐条通知。

已知取舍：如果用户选中了一个即将被折叠藏起来的子进程，WPF 在删除该项时会清掉选中态。是否改成“选中父进程”需要单独定 UX，本轮不做。

## 9. 测试

`--selftest` 是无依赖的纯逻辑套件，不创建窗口。本轮从 12 个用例扩到 23 个：

- 基础：前序、depth、折叠节点自身仍可见、折叠隐藏整棵子树。
- 出生/退出：折叠期间子进程出生、出生后展开、折叠期间子进程退出、最后一个子进程退出自动释放折叠。
- 父进程死亡：子进程升为根；折叠的父死亡后 key 被清理。
- PID 边界：PID 0 不当父、PID 复用不继承折叠、年轻父不收养老子进程、重复 PID 保留最旧。
- 结构脏数据：成环只输出一次、5000 深链不爆栈。
- 状态机闸门：无效点击（叶子/已死 key）被拒绝。
- 集合上界：churn 下 collapsed 数量不超过 alive。
- 可见行：插入/移动/删除；500 轮随机编辑后顺序与期望完全一致、无重复。
- 零分配：400 节点、预热后连续 200 次展平，分配字节数必须为 0。
- 随机对照：300 轮随机森林（含 PID 0、缺失父、自环、年轻父、随机折叠）与朴素递归 oracle 的 pid/depth/HasChildren 完全一致。

验证记录：

- 临时 net8.0 宿主（只含 `TreeModel.cs` + `SelfTest.cs`）实际运行：**23 passed, 0 failed**。
- 全项目用 `net8.0-windows` + `EnableWindowsTargeting` 交叉编译：**0 warning, 0 error**。代码未使用 .NET 11 专属 API。
- 核心逻辑微基准（Linux .NET 8，非 Windows 目标机）：500 节点 27µs/次，5000 节点 140µs/次，20000 节点 560µs/次；500 行可见列表从折叠到全展开约 29µs/次（含目标列表重置）。

Windows 11 本机实测（.NET 11.0.100，自包含 R2R）：

- 构建：**0 warning, 0 error**；`--selftest`：**23 passed, 0 failed**。
- 5 次启动：826 / 1329 / 921 / 864 / 884 ms，中位 **884ms**；最差 1329ms 仍在 1.5s 内。
- 129 秒真实进程长跑（490–537 个进程）：
  - 专用工作集：min 46.7 / mean 50.1 / max **56.1MB**（<80MB）
  - 工作集 max 147.9MB；私有提交 max 99.6MB
  - GC：Gen0=1，Gen1=0，Gen2=0
  - 句柄：684–695，无增长趋势
  - 分配速率：mean 0.10MB/s，max 0.18MB/s
  - 可见行 = 全量进程数，折叠 0（本轮长跑未做交互折叠）
- 496 项时 `realizedRows=26`，`isVirtualizing=True`，`scrollUnit=Item`，ListView 虚拟化仍生效。

## 10. 本轮改动文件

- `src/TaskMgrLite/Core/TreeModel.cs`
  - `TreeFlattener.Flatten` 增加 `parentsWithChildren` 输出；
  - `TryResolveParent` 处理 PID 0、自环、创建时间倒挂、重复 PID；
  - `CollapseState` 增加 `Toggle`，`Prune` 改为返回移除数量。
- `src/TaskMgrLite/ViewModels/MainViewModel.cs`
  - 新增 `_parentsWithChildren` 与缓存谓词 `_hasNoChildren`；
  - 展平后做叶子折叠剪枝；
  - `ToggleExpanded` 增加当前快照 / 父集合校验。
- `src/TaskMgrLite/Core/SelfTest.cs`
  - 新增 11 个状态机边界、随机对照、零分配和可见行 churn 用例。
- `design-round2.md`
  - 本文。

## 11. 下一轮再定

1. 折叠导致选中行消失时，选中态应该落到父进程、还是清空？
2. 兄弟节点的稳定顺序：目前跟随 `NtQuerySystemInformation` 的返回顺序。若目标机上观察到抖动，需要按 PID 或名称做稳定排序。
3. 5000+ 进程的压力场景：展平本身仍是 O(n)，但 `VisibleListSync` 的最坏 O(n²) 需要重新评估。
4. 键盘导航与 UIA：扁平化列表没有原生 TreeView 的语义，需要补 `AutomationPeer` 或自定义键盘处理。
