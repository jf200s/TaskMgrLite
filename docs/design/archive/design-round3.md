# Round 3：结束进程与命令行懒加载

本轮先交付两个核心模块：结束进程/结束进程树、命令行懒加载。搜索框的 150ms `DispatcherTimer` 防抖留到下一模块；当前搜索已经走 `ICollectionView.Filter`，且 XAML 绑定有 `Delay=120`，不会每个按键都刷新。

## 1. 结束进程

### 1.1 树杀计划：复用同一套父关系判定

`TreeFlattener.TryCollectSubtreePostOrder` 和 UI 展平共用 `BuildIndex`：

- 同一份 `pid → index`、childCount、children 区间、roots；
- 同一套父关系规则：PID 0 当根、父不存在当根、父创建时间晚于子当根、重复 PID 取最旧；
- 从选中节点做 DFS 前序，再把结果反转，得到“后代先于祖先”的顺序。

反转前序保证每个后代都在祖先之前；兄弟顺序相反，杀进程树不关心兄弟顺序。折叠状态不参与计划——UI 上折叠的子进程也必须被杀。

如果选中节点已经不在快照里，或同 PID 的创建时间对不上，计划返回 `false`，UI 显示“进程已退出”，不会误杀 PID 复用后的新进程。

### 1.2 单次结束的三重校验

`ProcessTerminator.Kill` 对每个 `ProcKey` 执行：

1. `OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE)`；
2. `GetProcessTimes` 的创建时间必须等于快照里的 `CreateTime`，否则 `ProcessReused`；
3. `WaitForSingleObject(handle, 0) == WAIT_OBJECT_0` 说明已经退出，返回 `AlreadyExited`。

所有句柄都在 `finally` 里 `CloseHandle`。

前置拒绝：

- PID 0 / 4：`SystemProcess`，不尝试打开；
- 当前进程自己：`SelfProcess`；
- 假数据模式：`NotSupported`，避免拿假 PID 去杀真实进程。

### 1.3 错误分类与报告

`KillStatus`：`Success / AccessDenied / AlreadyExited / ProcessReused / SystemProcess / SelfProcess / NotSupported / Failed`。

Win32 错误映射：

- 5 → `AccessDenied`
- 87 / 1168 / 6 → `AlreadyExited`
- 其他 → `Failed`，保留原始 Win32 错误码

树杀逐个执行，某一项失败不会中断后面的项。`KillReport` 汇总成功/失败数，并在弹窗里最多列出 8 条失败原因；超过 8 条显示“另有 N 个失败”。

有任何 `AccessDenied` 时，弹窗会追问是否以管理员身份重启。`ElevationHelper` 用 `runas` 重新启动当前 exe，并保留命令行参数；用户取消 UAC 时返回 false，不退出当前进程。

### 1.4 线程模型

- 树杀计划在 UI 线程生成，基于当前不可变快照；
- 复制成 `ProcKey[]` 后丢给 `Task.Run`，避免结束大量进程时卡住 UI；
- 结束期间采集线程继续产出快照，UI 继续刷新；
- 报告回到 UI 线程弹窗，状态栏显示成功/失败数。

## 2. 命令行懒加载

### 2.1 单次读取

`CommandLineReader.Read`：

1. `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE)`；
2. 校验创建时间，防止 PID 复用；
3. `WaitForSingleObject` 排除已退出进程；
4. 先用空缓冲区调 `NtQueryInformationProcess(class 60)` 拿长度；
5. 用复用的非托管缓冲区再查一次；
6. 解析 `PROCESS_COMMAND_LINE_INFORMATION` 里的 `UNICODE_STRING`。

x64 / ARM64 布局：

```
+0  USHORT Length          // 字节数
+2  USHORT MaximumLength
+8  PWSTR  Buffer          // 指向返回缓冲区内部
```

解析时同时校验 `Buffer` 和 `Length` 落在本次实际返回长度内，避免读缓冲区尾部未初始化内存。只有命令字符串本身会 `new string`，缓冲区跨调用复用。

### 2.2 服务层

`CommandLineService`：

- 单后台线程串行读取，`ThreadPriority.BelowNormal`；
- 有界队列 256，UI 滚动再快也不会堆积；
- `_pending` 去重，同一 key 同时只有一个请求；
- LRU 512，命中时移到表头，超容量淘汰表尾；
- 失败结果也缓存（拒绝访问/已退出），避免反复重试；
- 结果通过 `Dispatcher.BeginInvoke` 回 UI；
- 进程退出时 `Forget(key)`，worker 尚未返回的结果会被 `isAlive` 丢弃；
- 服务在窗口关闭时 `Dispose`，worker 是后台线程，不会阻塞退出。

### 2.3 只对可视/选中行请求

`MainWindow` 挂四个触发源：

- `Loaded`：首屏；
- `ScrollChanged`：滚动；
- `SelectionChanged`：选中行（即使滚出视口也加载）；
- `ItemContainerGenerator.StatusChanged`：过滤/排序/切树后容器重建。

同一轮布局里的多次事件用 `_commandLineScanQueued` 合并成一次 `Dispatcher.BeginInvoke`。扫描只遍历 `ItemContainerGenerator.ContainerFromIndex` 已经生成的容器（虚拟化下通常 20–40 个），不会碰全量行。

## 3. 搜索防抖

`SearchText` 的 setter 只重置一个 150ms `DispatcherTimer`，不直接碰集合视图。计时器触发后才：

1. 把 `_searchText` 复制到 `_appliedSearchText`；
2. 非空时 `SetFlatMode()`（过滤会把树的父子行拆散）；
3. `View.Refresh()`。

`FilterRow` 只读 `_appliedSearchText`，匹配逻辑抽到纯函数 `ProcessFilter.Matches(name, pid, query)`：空查询匹配全部，名称大小写不敏感，PID 子串匹配。selftest 直接覆盖这个纯函数。

XAML 里原来的绑定级 `Delay=120` 已移除，避免和 150ms 计时器叠加成 270ms。

## 4. UI

- ListView 右键菜单：“结束进程”“结束进程树”；
- `PreviewMouseRightButtonDown` 先把光标下的 `ListViewItem` 选中，再弹菜单；
- 结果用窗口内非阻塞 Toast：摘要一行，失败时可点“详情”，权限不足时出现“提权重启”，8 秒自动隐藏；
- 详情才打开 MessageBox，不再每次结束进程都弹模态框；
- 命令行列沿用 Round 0 的最右列，`ProcessRowViewModel.CommandLine` 由服务回填。

## 5. 验证

### 5.1 单元测试

`--selftest` 从 23 个用例扩到 **32 个**，新增：

- 树杀计划是后序，根最后；子树计划正确；
- 根不存在、PID 复用、PID 0 时拒绝生成计划；
- 成环时计划包含每个节点一次，根最后；
- PID 0/4/自身被结束守卫拒绝；
- Win32 错误分类；
- KillReport 汇总成功/失败/拒绝访问；
- LRU 淘汰最久未使用项；
- 手工构造 `UNICODE_STRING` 缓冲区，验证解析、空串、越界指针；
- 搜索按进程名大小写不敏感、PID 子串匹配、空查询匹配全部。

Windows 11 本机 .NET 11 实测：**32 passed, 0 failed**。

### 5.2 真实内核路径诊断

新增 `--diag-round3`（不创建窗口，只碰受控子进程）：

1. 读自己的命令行，必须包含 `--diag-round3`；
2. 直接启动单进程 `ping.exe`，读它的命令行，验证错误创建时间返回 `ProcessReused` 且不杀进程，再用正确 key 真正结束；
3. 启动 `cmd → cmd → ping` 的受控进程树，用快照构建后序计划并整树结束，验证父/子都退出。

单杀用直接 `ping.exe` 而不是 `cmd /c ping`，避免只杀 cmd 后留下孙进程 ping；树杀等 `PING` 真正进入快照后再执行。实测：**3 passed, 0 failed**，整个诊断约 1.6 秒。`check.ps1` 已把 `--diag-round3` 接进日常检查。

### 5.3 真实 UI 验证

用 UI Automation 读取 500 行真实进程的 ListView：

- 系统进程（Idle、System、smss、csrss、services 等）命令行显示“（拒绝访问）”，符合预期；
- 普通用户进程（SearchHost、msedgewebview2 等）显示真实命令行，说明“仅可视行触发 + 懒加载 + 回填”链路生效；
- 命令行很长时仍在列内 TextTrimming 显示，ToolTip 保留全文；
- 搜索框输入 `svchost`，首行从 Idle 变为 `svchost.exe`；清空后恢复 Idle，证明 150ms 防抖 + `Filter` + 平铺切换链路生效（`artifacts\search-ui.txt`）。

### 5.4 资源

Round 3 的 71 秒真实进程长跑（489–495 个进程，窗口打开，命令行懒加载生效）：

- 专用工作集：min 48.6 / mean 51.6 / max **55.9MB**（<80MB）
- 工作集 max 144.3MB；私有提交 max 121.7MB
- GC：Gen0=1，Gen1=0，Gen2=0
- 句柄：658–668，无增长趋势
- 分配速率：mean 0.06MB/s，max 0.14MB/s
- 可见行 = 全量进程数
- 多次启动实测 728–823ms，中位约 **800ms**（8 次一组为 795.7–822.7ms、中位 809ms；后续热缓存下 728–751ms）

## 6. 已知取舍

1. 提权重启暂不保留搜索词/折叠状态，只保留命令行参数；后续可用临时状态文件补齐。
2. 命令行失败结果是永久负缓存，只有 LRU 淘汰或重启后才会重试；如果需要“以管理员身份读取”按钮，可以加手动失效。
3. 树杀只按点击时的快照执行；点击之后新出生的后代不在计划里，需要在下一轮手动处理。
4. Toast 8 秒自动隐藏；失败详情要用户点“详情”才展开，不再主动打断。
5. 搜索是 150ms 防抖 + `View.Refresh()`；每次刷新仍会遍历当前行，500 行量级可接受，万级进程需要换成增量索引。

## 7. 本轮改动文件

- `Core/TreeModel.cs`：抽出 `BuildIndex`，新增 `TryCollectSubtreePostOrder`。
- `Core/ProcessTerminator.cs`：单杀、批量杀、状态、报告、错误分类。
- `Core/CommandLineReader.cs`：class 60 查询与 UNICODE_STRING 解析。
- `Core/CommandLineService.cs`：后台 worker、有界队列、LRU、回填。
- `Core/LruCache.cs`：固定容量 LRU。
- `Core/ProcessFilter.cs`：纯名称/PID 过滤函数。
- `Core/ElevationHelper.cs`：runas 提权重启。
- `Core/Round3Diagnostics.cs`：`--diag-round3` 真实内核路径检查。
- `ViewModels/MainViewModel.cs`：结束计划、后台执行、命令行请求/回填、150ms 搜索防抖、Dispose。
- `MainWindow.xaml` / `MainWindow.xaml.cs`：右键菜单、非阻塞 Toast、可视行触发。
- `Core/SelfTest.cs`：新增 9 个 Round 3 用例。
- `scripts/check.ps1`：接入 `--diag-round3`，selftest/diag 改用 `Start-Process -Wait -PassThru` 读退出码。
