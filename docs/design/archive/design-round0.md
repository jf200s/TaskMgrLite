# 任务管理器替代工具 — 设计与取舍（Round 0）

基线已锁定：.NET 11 + WPF，自包含 ReadyToRun 发布（不用单文件压缩）；内存口径为**专用工作集**；权限模型为**普通权限运行 + 一键提权重启**。

> 运行时补充：.NET 11 按既定节奏属于 STS（18 个月支持），.NET 10 才是 LTS。若这个工具要长期分发，建议保留一条 net10 构建路径。R2R 的 ISA 基线若确实是 x86-64-v3，老 CPU 上不会崩，但会拒用 R2R 镜像退回 JIT，冷启动收益消失——分发前要按目标机实测。

---

## (a) 对「提高体验」的理解和取舍

任务管理器的真实痛点不是功能少，是四个瞬间：

1. 打开那一刻——空白窗口，表格逐行长出来，或者干脆转圈
2. 找进程那一刻——树折叠着，点开三层才找到
3. 看数字那一刻——1s 刷新但行在跳，CPU 列来回闪，选中行消失
4. 杀进程那一刻——不确定杀的是不是对的，杀了父进程子进程还在

所以产品命题是「更快到达」，不是「信息更多」。砍掉一切首屏不需要的东西，预算全押在列表手感和数字可信度上。

三个体验点的可执行定义：

- **冷启动 < 1.5s**：从进程创建（`Process.StartTime`，内核记录）到**数据就绪后的第一个 `CompositionTarget.Rendering`**，不是「窗口句柄出现」，也不是「空壳窗口的第一帧」——实测空壳帧比 `Window.Loaded` 还早 140ms，用它会把指标量虚。取 20 次中位数，冷/热分开测。
- **自身内存 < 80MB**：空闲 60s 后的专用工作集（`WorkingSetPrivateSize`），同时报工作集和 CPU 作为旁证。
- **树默认展开 + 按父进程折叠**：300+ 进程全展开时首屏 < 100ms 可见，折叠/展开 < 16ms（一帧内），折叠父进程时子进程整棵隐藏。

明确的取舍，按重要性排：

**树用扁平化列表实现，不用 TreeView。** WPF 的 TreeView 虚拟化基本不可用，默认全展开等于一次性实例化所有容器。做法是采集侧算好 parent→children 索引，UI 侧维护 `expanded` 集合，每次快照后 O(n) 展平成带 depth 缩进的可见行，交给 ListView 虚拟化。折叠只改变可见行集合，不碰控件树。代价：拖拽、键盘树导航要自己写，这轮不做。

**排序和树二选一。** 点任意列排序即切平铺模式，点「树」切回树序。否则「按 CPU 排序后父子不相邻」是个无法解释的界面。硬取舍，不提供「两者都要」。

**刷新是 diff，不是重建。** 每秒 Clear + Add 会丢选中、滚动位置、ToolTip。行用 `PID + CreateTime` 做稳定 key（防 PID 复用），按 key 增删改，属性变了才触发通知。排序再加滞后：值跨过阈值或用户点表头才重排。

**命令行懒加载。** 全量读 300 个进程的命令行＝每秒 300 次 OpenProcess + 内核查询，是采集线程唯一会超预算的操作，非管理员下大半还会失败。只在行进入可视区或选中时读，LRU 缓存 512 条，失败显示「—」并把原因放进 ToolTip。代价：搜索不匹配命令行。

**内存列口径对齐任务管理器。** 用 `WorkingSetPrivateSize`（专用工作集），不是 `WorkingSetSize`。既决定正确性验证能否通过，也让采集侧零额外成本。

**暗色主题手写最小样式集。** 不引第三方 UI 库（体积、启动、依赖都吃预算），只覆盖 Window、ListView/GridView、ScrollBar、TextBox、Button、ContextMenu、ToolTip；标题栏用 `DwmSetWindowAttribute` 走系统暗色。数字列右对齐 + tabular figures + 固定列宽，避免每秒跳 1px。

**这轮不做：** 详情面板、实时曲线、列宽记忆、命令行搜索、拖拽排序、托盘常驻。

---

## (b) 数据源选型

核心判断：**除命令行外，所有列都能被一次 `NtQuerySystemInformation` 调用拿全。** 这是低开销的根本。

| 列 | 来源 | 频率 | 备注 |
|---|---|---|---|
| 名称 / PID / 父PID | `SYSTEM_PROCESS_INFORMATION.ImageName` / `UniqueProcessId` / `InheritedFromUniqueProcessId` | 1Hz，单次调用 | ImageName 是 UNICODE_STRING，指向共享缓冲区，必须当场复制成 string |
| CPU% | `UserTime` + `KernelTime` 两次采样差 | 1Hz | 100ns 单位，除以（Δwall × 逻辑核数）得整机占比 |
| 内存 | `WorkingSetPrivateSize` | 1Hz，同一结构 | 与任务管理器「内存」列同口径 |
| 线程 / 句柄数 | `NumberOfThreads` / `HandleCount` | 1Hz | 顺手就有 |
| 创建时间 | `CreateTime` | 1Hz | 同时作为行 key，防 PID 复用 |
| 命令行 | `NtQueryInformationProcess(ProcessCommandLineInformation=60)` | 懒加载 | 需 `PROCESS_QUERY_LIMITED_INFORMATION` |
| 结束进程 | `OpenProcess(PROCESS_TERMINATE)` + `TerminateProcess` | 用户触发 | 树杀按快照 DFS 后序 |

理由：

**用 class 5（SystemProcessInformation），不用 57。** 57 多返回每线程信息，我们不展示线程，白付解析成本。缓冲区首轮 1MB，`STATUS_INFO_LENGTH_MISMATCH` 时翻倍重试（上限 16 次 / 64MB），之后复用同一个 buffer，不每秒新分配。

**用 unsafe 按偏移直读，不用 `Marshal.PtrToStructure`。** 后者对 300 个 entry 做结构体拷贝，每秒白分配上百 KB 并推高 GC。结构体按 x64/x86/ARM64 分别定义，用 `sizeof` 断言 + 单元测试锁住字段偏移；版本差异靠偏移断言测试兜底，不靠记忆。

**CPU 用差值，首帧不出数。** 第一次采样没有基准，显示「—」而不是 0% 或 100%。归一化按「占 CPU 总容量」，与任务管理器 2020 后的默认口径一致，这一点写死在验证脚本里。

**命令行用 class 60，不读 PEB。** 读 PEB 要处理 32/64 位跨位、指针有效性、受保护进程，还得自己管字符串生命周期；class 60 由内核拷贝，跨位数天然可用。代价是对系统进程需要更高权限——正好落在既定权限模型里。

**不用 WMI、不用 PDH 做进程列表。** WMI 单次几十到几百毫秒且要起 COM；PDH 每进程一个 counter，初始化慢、句柄多，会直接搞挂「30 分钟句柄不增长」。

---

## (c) 线程 / 刷新架构草图

```
┌─ 采集线程（专用 Thread，BelowNormal，非线程池 Timer）────────┐
│  while (!stop)                                              │
│    t0 = Stopwatch                                    1Hz    │
│    NtQuerySystemInformation(5, reuseBuf)                    │
│    unsafe parse → ProcessSample[]（复用数组，零分配）        │
│    CPU delta = (K+U) - prev[PID+CreateTime]                 │
│    Snapshot snap = { rows[], childIndex, seq, samplingTime }│
│    Interlocked.Exchange(ref _latest, snap)   // 覆盖式       │
│    signal UI（仅当 _uiBusy == 0 时 BeginInvoke）             │
│    sleep(1000 - elapsed)   // 补偿采集耗时，节拍不漂移       │
└─────────────────────────────────────────────────────────────┘
                              │ 不可变快照，无锁
┌─ UI 线程（STA）─────────────▼───────────────────────────────┐
│  var s = Interlocked.Exchange(ref _latest, null)            │
│  keyed diff → 就地增删改 RowVM（只对变化属性发 INPC）        │
│  tree mode → flatten(expandedSet) → 可见行 List             │
│  ListView 虚拟化只实例化视口内的行容器                       │
└─────────────────────────────────────────────────────────────┘
                              │
┌─ 命令行 worker（按需，单线程 + 有界队列 + LRU 512）─────────┐
│  OpenProcess → NtQueryInformationProcess(60) → cache        │
└─────────────────────────────────────────────────────────────┘
```

关键决策：

**采集用专用 Thread，不用 `System.Threading.Timer`。** Timer 跑在线程池上，可能被饿死，也可能因上一轮未结束而叠加回调；1s 节拍一旦漂移，「每秒刷新」变成「每 1.3 秒刷新」，CPU% 也不准。专用线程 + Stopwatch 补偿，周期误差控制在几毫秒。

**快照不可变 + 原子交换，UI 零锁。** 采集线程构造完整 Snapshot 才发布，UI 读到的永远一致。UI 忙时只保留最新一份（覆盖式交换），不排队堆积——这是「刷新不阻塞 UI」与「内存不增长」同时成立的前提。

**不跨线程碰 ObservableCollection。** 所有集合变更都在 UI 线程，采集线程只产出数组。

**首次刷新不等 1s。** 窗口 Loaded 后立刻采样并渲染一次，第二次才进入 1Hz 循环。直接决定冷启动时首帧有没有数据。

**展开状态独立于快照。** `expanded` 是 `HashSet<key>`，快照只影响「可见行是什么」。进程消失时顺手清理，避免集合无限增长。

**UI 更新合并。** BeginInvoke 用 Background 优先级 + `_uiBusy` 标志；上一帧没处理完就不投递新快照（反正 `_latest` 已被覆盖）。

---

## (d) 最可能翻车的三个点

**1. 80MB 专用工作集在 WPF 上可能就达不到，这是目标定义问题，不是优化问题。**

空的 .NET 10 自包含 R2R WPF 窗口，专用工作集经验值 50–70MB；加载 400 行、每行一个带 INPC 的 VM，再算 WPF 的渲染目标和字体缓存，很容易顶到 90–110MB。原版任务管理器是原生程序，20–30MB，追不平。专用工作集在任务管理器里还会受共享 DLL 归因影响，数字会跳。

所以 round 0 先做 spike：空壳 + 400 行假数据 + 真实采集，量出基线，再决定 80MB 是「行 VM 池化 + 非可见行不保留详细数据 + 进程名 intern」能守住，还是要改成「专用工作集 <100MB、工作集 <140MB」。**先测再承诺。** 防御手段已经想好，不用临时找。

**2. 树 × 排序 × 1s 刷新，各自都简单，叠在一起全是状态 bug。**

会怎么翻：每秒重建 ItemsSource → 选中丢、滚动跳；按 CPU 排序 → 树散了，折叠的父子对不上；PID 复用 → 新进程顶着旧行的选中态；点表头的同一帧快照到达 → 排序键和行集合不同步，出现「点了没反应」或「排完又跳回去」；折叠了一个刚启动的父进程，子进程晚 1s 出现，状态对不上。

三条硬规矩：所有行必须有稳定 key；展开集合与快照完全解耦；排序只在用户动作或数值跨阈值时应用，且带 seq 校验（快照序号回退就丢弃）。这一块会写针对状态机的单元测试，不只靠手点——手点永远点不到那个竞态。

**3. 权限与安全软件：普通权限下功能大面积退化，而这套 API 组合很像 hack tool。**

普通权限下，系统进程（PID 4、各类服务）读不到命令行、杀不掉；PPL 进程（杀软、Defender）提权也杀不掉；部分 EDR 会拦截甚至隔离二进制。这不是理论风险，是这类工具最常见的死法。

防御：每个失败都给具体原因（拒绝访问 / 受保护进程 / 进程已退出），不搞统一「操作失败」；结束进程树做成逐个报告——杀到第 4 个失败时告诉用户前 3 个成功、第 4 个为什么失败；提权重启保留筛选和展开状态。验证阶段要在一台装了第三方杀软的机器上跑一次，不能只在干净环境自测。

**第四名（差一点进前三）：句柄和 GC。** 30 分钟不增长的真正敌人不是 OpenProcess 漏关（SafeHandle 兜底），而是 WPF 的字体/字形缓存、ContextMenu 与 ToolTip 的重复创建、以及解析路径里偷偷分配的大对象。采集路径要求零分配；验证脚本同时记录 gen0/gen2 次数和 allocated bytes，光看句柄会漏。

---

## 验证方案（预告，代码轮落地）

- **冷启动**：程序内从 Main 打点到第一个 `CompositionTarget.Rendering`，以 `Process.StartTime` 为基准（内核记录，精度高），写日志；外层 PowerShell 跑 20 次取中位数，冷/热分开。不用轮询 `MainWindowHandle`——它比首帧早。
- **自身资源**：`--stats` 每 10s 打一行 CSV（专用工作集 / 工作集 / 私有提交 / CPU / 句柄 / GC），30 分钟直接出图。
- **正确性**：`--dump-once` 导出 JSON 快照，PowerShell 用 `Get-Process` + `Get-Counter` 同刻对照，随机抽 10 个 PID 打印三方数字与偏差；任务管理器 UI 只做人工抽检截图。
- **稳定性**：CSV 回归斜率 + 峰谷差双阈值，句柄、工作集、gen2 三条曲线都要平。

---

## 待确认

1. 80MB 是硬指标还是「尽量」？建议等 round 0 spike 出数再锁死，否则后续优化都在为一个可能不可达的数字服务。
2. 「排序即切平铺」这个取舍能否接受？不能接受的话，需要定义排序时树的语义。
3. 冷启动口径用「进程创建 → 首帧」而非「双击 → 窗口可见」，确认。
