# Round 5：极简体验补全（实现方案）

本轮只做四件事，全部复用现有状态机，不新增架构层：

- 折叠时，选中态迁移到被折叠的父节点。
- 提权重启前保存搜索词、折叠集合和视图模式，新进程恢复。
- 命令行失败缓存加 30 秒 TTL，提权后由新进程天然清空。
- 树形模式支持 Left 折叠 / Right 展开。

明确不做：5000+ 进程的 `VisibleListSync` 重构、兄弟节点稳定排序、UIA `AutomationPeer`、详情服务的负缓存改造、Left 跳到父节点、Home/End/type-ahead。

## 状态流转总览

```
折叠（点击展开器 / Left）
  → MainViewModel.SetExpanded(row, expand:false)
  → 先判断当前选中是否在这棵子树里
  → 改 _collapsed
  → RebuildFromCurrentNodes()
  → 若选中在子树里，SelectedRow = 被折叠的节点

提权重启
  → vm.CaptureUiState()
  → UiStateStore.Save() 到 %TEMP%\TaskMgrLite\elevate-ui-<pid>.json
  → runas + --restore-ui="<path>"
  → 新进程 TryLoad() → 删除文件 → MainViewModel 构造时注入
  → _searchText / _collapsed / _viewMode 直接初始化

命令行读取
  → Request(key)
  → CommandLineCache.TryGet(key, now)
      命中且未过期 → 立即回填
      命中但已过期 → Remove + 入队
      未命中       → 入队
  → worker 读取 → Set(key, result, now)

键盘
  → ProcessList.PreviewKeyDown
  → Left / Right → vm.SetExpanded(row, false / true)
  → 与点击展开器走同一条路径
```

---

## 1. 折叠时选中态迁移

### 目标

折叠当前选中行的祖先时，选中落到被折叠的节点，详情面板不清空。选中在子树外时不动。

### 改动点

`ViewModels/MainViewModel.cs`

- 新增 `bool SetExpanded(ProcessRowViewModel row, bool expand)`，返回是否真的改变了折叠状态；键盘处理靠这个返回值决定要不要 `Handled`。
- `ToggleExpanded(row)` 只负责算出目标状态，然后委托给 `SetExpanded`。
- `CollapseState.Toggle` 拆出一个不翻转的 `Set(collapsed, key, expand, parentsWithChildren)`，保留原校验。

`Core/TreeModel.cs`

- 新增纯函数 `TreeSelection.ShouldMoveSelection(List<FlatNode> flat, ProcKey collapsed, ProcKey? selected)`。
- 利用展平结果的前序性质：祖先之后、深度仍大于祖先的连续行就是它的子树。

### 状态流

1. `SetExpanded` 先做现有校验：树模式、key 仍在 `_rowsByKey`、key 在 `_parentsWithChildren` 里。
2. 在修改 `_collapsed` 之前，用当前 `_desired` 计算 `migrate`：
   - 选中为 null、选中就是被折叠节点、选中是兄弟或子树外 → `false`。
   - 选中是被折叠节点的后代 → `true`。
3. `CollapseState.Set` 返回 `false` 说明状态没变（例如已经折叠），直接返回，不重建。
4. `RebuildFromCurrentNodes()`。
5. 如果 `migrate`，在重建完成后设置 `SelectedRow = row`。先重建再设置，避免 ListView 删除被选中项时把绑定清成 null。

### 边界

- 折叠叶子 / 上一帧的旧行：`_parentsWithChildren` 校验会拒绝，不产生状态。
- 选中就是被折叠节点：保持选中，不迁移。
- 展开（Right）永远不迁移选中。
- 重建过程中可能出现一次同步的 `SelectedRow = null`，但同一 dispatcher 回合内会设回父节点，正常渲染不会看到中间态。若实测有闪烁，再在 `MainWindow` 加一个重建抑制标志，本轮先不加。

### 单测

- `TreeSelection.ShouldMoveSelection` 覆盖：后代、兄弟、祖先自己、null、key 不在列表。
- `CollapseState.Set` 覆盖：折叠、展开、重复设置、叶子拒绝。

---

## 2. 提权重启保留搜索、折叠和视图模式

### 目标

用户点“提权重启”后，新进程恢复搜索词、折叠集合、树/平铺模式。不恢复选中行、滚动位置、窗口位置，避免扩大范围。

### 新增文件

`Core/UiState.cs`

```csharp
public readonly record struct UiState(
    string SearchText,
    ProcessViewMode ViewMode,
    ProcKey[] Collapsed,
    DateTime SavedAtUtc)
{
    // 搜索非空时强制 Flat，与现有“搜索一律平铺”的规则一致
    public UiState Normalize() { ... }
}
```

`Core/UiStateStore.cs`

- `Save(UiState state, string? rootDir = null)`：先写 `.tmp`，再原子 `Move` 到最终文件。
- `TryLoad(string path, string? rootDir = null)`：校验路径、大小、版本、时间戳，失败返回 null。
- `TryDelete(string path)`。
- 只接受 `%TEMP%\TaskMgrLite\` 下的路径；不在这个目录下的路径不读、不删。
- 路径合法但内容无效（坏 JSON、超大小、版本不符、过期）时删除文件；路径非法时保持原文件不动。

### 文件格式

```json
{
  "version": 1,
  "savedAtUtc": "2026-11-22T16:40:00Z",
  "searchText": "svchost",
  "viewMode": "Tree",
  "collapsed": [
    { "pid": 1234, "createTime": 133000000000000000 }
  ]
}
```

限制：文件不超过 64KB，搜索词不超过 256 字符，折叠项不超过 4096 条，超过 5 分钟视为过期。

### 状态流

1. `MainWindow.OnToastElevateClick` 调 `ElevationHelper.TryRestartElevated(vm.CaptureUiState())`。
2. `CaptureUiState` 只取三样：`_searchText`、`_collapsed.ToArray()`、`_viewMode`；时间戳用 `DateTime.UtcNow`。
3. `ElevationHelper` 保存文件，路径为 `%TEMP%\TaskMgrLite\elevate-ui-<当前 PID>.json`，并把 `--restore-ui="<path>"` 追加到参数。
4. `BuildArguments` 抽成 `internal static`，方便自检；它必须先过滤掉命令行里已有的 `--restore-ui=`，再追加新的，避免二次提权时引用已删除的旧文件。
5. UAC 取消或启动失败：删除刚写的文件；成功启动则让旧进程退出。
6. `App.OnStartup` 解析 `--restore-ui=`，`TryLoad` 成功后立即删除文件，再把 `UiState?` 传给 `MainViewModel`。

### 恢复时序

`MainViewModel` 构造函数里直接初始化字段，不走属性 setter：

- `_searchText = state.SearchText`，`_appliedSearchText = state.SearchText`，避免 150ms 防抖和一次空过滤。
- 搜索词非空时强制 `_viewMode = Flat`，与现有“搜索一律平铺”的规则一致。
- 把 `state.Collapsed` 填进 `_collapsed`。

首份快照到达后，现有顺序正好能安全恢复：

1. `CollapseState.Prune(_collapsed, _isDead)` 先丢掉已退出 / PID 复用的 key。
2. `Flatten` 使用恢复后的 `_collapsed`，同时填好 `_parentsWithChildren`。
3. `CollapseState.Prune(_collapsed, _hasNoChildren)` 再丢掉已经变叶子的 key。

不要调整这个顺序：如果在 `Flatten` 之前做叶子剪枝，`_parentsWithChildren` 还是空的，会把所有恢复项误删。

### 边界

- 没有 `--restore-ui`：行为与现在完全一致。
- 文件损坏、版本不符、过期、路径越界：静默忽略，正常启动。
- 多实例：文件名带旧进程 PID，且路径显式传参，不互相覆盖。
- 新进程的命令行 / 详情缓存本来就是空的，负缓存天然清空，不需要额外信号。

### 单测

- `UiStateStore` 往返：搜索词、模式、折叠 key 完全一致。
- 过期、版本不符、坏 JSON、超过 64KB、路径不在 temp root：返回 null。
- 越界路径不删除原文件。
- `BuildArguments` 保留 `--real` 等已有参数、正确引用带空格的路径、过滤旧的 `--restore-ui`。
- `UiState.Normalize`：搜索词非空时模式变 Flat，空搜索保持原模式。

---

## 3. 命令行失败缓存 30 秒 TTL

### 目标

`AccessDenied` / `Error` 不再永久负缓存。30 秒后下一次触发会重试；成功结果继续永久缓存。提权重启后新进程缓存为空。

### 新增文件

`Core/CommandLineCache.cs`

```csharp
public sealed class CommandLineCache
{
    public const int NegativeTtlMs = 30_000;

    public bool TryGet(ProcKey key, long nowMs, out CommandLineResult result);
    public void Set(ProcKey key, CommandLineResult result, long nowMs);
    public void Remove(ProcKey key);
    public int Count { get; }
}
```

内部仍是 `LruCache<ProcKey, Entry>`，`Entry` 保存 `Result` 和写入时的 `nowMs`。

### TTL 策略

| 状态 | 是否过期 | 理由 |
|---|---|---|
| `Ok` | 否 | 进程身份不变，命令行不会变 |
| `AccessDenied` | 30s | 提权或权限变化后应能重试 |
| `Error` | 30s | 瞬时失败不应永久钉死 |
| `Exited` | 否 | 进程已退出，由 `Forget` 清理 |
| `ProcessReused` | 否 | 旧 key 已失效，由 `Forget` 清理 |
| `Unsupported` | 否 | 系统能力问题，重试无意义 |

### 状态流

1. `CommandLineService.Request(key)` 用 `Environment.TickCount64` 调 `TryGet`。
2. 命中且未过期：同步回填，和现在一样。
3. 命中但过期：从 LRU 移除，继续走入队逻辑；`_pending` 已经能去重。
4. worker 读取完成后 `Set(key, result, now)`，TTL 从结果写入时开始算。
5. 没有定时器、没有后台清扫；过期只在 `Request` 时惰性判断。稳态零额外分配、零额外线程。

### 边界

- 不做 UI 上的“重试”按钮；用户下一次滚动或选中就会触发惰性重试。
- 不做 `ProcessDetailsService` 的负缓存；那是另一项，避免本轮扩大范围。
- 30 秒是上限，不是轮询：一个持续可见的受保护进程最多每 30 秒重试一次。
- 提权重启走新进程，缓存为空，不需要显式 `Clear`。

### 单测

- `Ok` 在 10 分钟后仍命中。
- `AccessDenied` 在 29,999ms 命中，30,000ms 未命中且从缓存移除。
- `Error` 同上。
- `Exited` / `ProcessReused` / `Unsupported` 在 30 秒后仍命中。
- 重新 `Set` 会刷新时间戳。
- LRU 容量仍是 512，淘汰顺序不变。

---

## 4. 树形模式左右键

### 目标

只做 Left 折叠、Right 展开。不做父节点跳转、Home/End、type-ahead，不写 UIA `AutomationPeer`。

### 状态流

`MainWindow` 给 `ProcessList` 挂 `PreviewKeyDown`：

1. 只处理 `Key.Left` / `Key.Right`。
2. 只在树形模式、当前有选中行时处理。
3. Left → `vm.SetExpanded(row, expand:false)`；Right → `vm.SetExpanded(row, expand:true)`。
4. `e.Handled = vm.SetExpanded(...)`：只有状态真的变化才拦截，避免影响平铺模式的默认横向滚动，也避免叶子节点吃掉按键。

`SetExpanded` 复用第 1 项的实现：状态没变就 no-op，叶子 no-op，选中迁移只发生在折叠时。

### 单测

键盘本身是 6 行 UI 接线，不做自动化；逻辑由 `CollapseState.Set` 覆盖：

- Right 在已展开时 no-op。
- Left 在已折叠时 no-op。
- 叶子 / 不在 `_parentsWithChildren` 的 key 两个方向都 no-op。
- 折叠后 `_collapsed` 包含 key，展开后不包含。

---

## 改动文件

| 文件 | 改动 |
|---|---|
| `Core/TreeModel.cs` | 新增 `TreeSelection.ShouldMoveSelection`；`CollapseState.Set`，`Toggle` 委托给它 |
| `ViewModels/MainViewModel.cs` | `SetExpanded`、选中迁移、`CaptureUiState`、构造函数恢复状态 |
| `Core/UiState.cs`（新） | `UiState` + `Normalize` |
| `Core/UiStateStore.cs`（新） | JSON 保存 / 校验 / 删除 / 路径边界 |
| `Core/ElevationHelper.cs` | 接收 `UiState`，保存状态，过滤并追加 `--restore-ui`，失败清理 |
| `App.xaml.cs` | 解析 `--restore-ui=`，加载后传给 `MainViewModel` |
| `MainWindow.xaml.cs` | 提权按钮传状态；`ProcessList.PreviewKeyDown` 处理 Left/Right |
| `Core/CommandLineCache.cs`（新） | TTL + LRU 包装 |
| `Core/CommandLineService.cs` | 换用 `CommandLineCache`，`Request` 惰性过期 |
| `Core/SelfTest.cs` | 新增 16 个用例，注册进 `Run()` |

不新增 XAML，不改主题，不改 `ProcessDetailsService`，不改 `VisibleListSync`。

## 单元测试清单

全部加进 `Core/SelfTest.cs`，保持“不碰 WPF”的原则。计划新增 15–16 个用例；实际新增 17 个，43 → 60。

| # | 用例 | 断言 |
|---|---|---|
| 1 | selection moves to collapsed ancestor | 选中孙节点，折叠父节点 → `true` |
| 2 | selection stays for sibling | 选中兄弟分支 → `false` |
| 3 | selection stays on collapsed node | 选中被折叠节点自身 → `false` |
| 4 | selection stays outside subtree | 选中根或其它分支 → `false` |
| 5 | collapse set supports explicit expand | `Set(expand:true)` 移除，重复调用返回 `false` |
| 6 | leaf expand/collapse rejected | key 不在 `parentsWithChildren` → `false` |
| 7 | ui state round-trips | 搜索词、模式、collapsed 数组一致 |
| 8 | ui state rejects stale | `SavedAtUtc` 超过 5 分钟 → null |
| 9 | ui state rejects bad version | version != 1 → null |
| 10 | ui state rejects malformed / oversized | 坏 JSON、>64KB → null，文件被删除 |
| 11 | ui state rejects path outside temp root | 返回 null，原文件仍在 |
| 12 | elevation args preserve flags and path | `--real` 保留、带空格路径正确引用、旧 `--restore-ui` 被过滤 |
| 13 | restore forces flat with search | 搜索非空 → Flat；空搜索保持 Tree |
| 14 | ok cache never expires | 10 分钟后仍命中 |
| 15 | negative cache expires at 30s | 29,999ms 命中，30,000ms 未命中且 Count 减一 |
| 16 | terminal cache statuses do not expire | Exited / ProcessReused / Unsupported 30 秒后仍命中 |

### 测试代码形状

```csharp
// 1. 选中迁移
var flat = Flat((1, 0), (2, 1), (3, 2), (4, 1), (5, 0));
True(TreeSelection.ShouldMoveSelection(flat, K(1), K(3)), "grandchild");
True(!TreeSelection.ShouldMoveSelection(flat, K(1), K(4)), "sibling");
True(!TreeSelection.ShouldMoveSelection(flat, K(1), K(1)), "self");

// 15. 负缓存 TTL
var cache = new CommandLineCache();
cache.Set(K(1), Denied(), nowMs: 0);
True(cache.TryGet(K(1), 29_999, out _), "before ttl");
True(!cache.TryGet(K(1), 30_000, out _), "at ttl");
Equal(0, cache.Count, "expired entry removed");

// 7. 状态往返
var state = new UiState("svchost", ProcessViewMode.Tree, new[] { K(1), K(2) }, DateTime.UtcNow);
string path = UiStateStore.Save(state, tempRoot);
UiState? loaded = UiStateStore.TryLoad(path, tempRoot);
EqualText("svchost", loaded!.Value.SearchText, "search");
Equal(2, loaded.Value.Collapsed.Length, "collapsed");
```

### UI / 手工验证

自动化测试覆盖不到 UAC 和键盘焦点，以下四项手工跑一次：

1. 选中子进程 → 折叠父进程 → 选中落到父进程，详情面板仍显示父进程信息。
2. 搜索 `svchost`、折叠两个分支、切到树形 → 点“提权重启” → UAC 通过后搜索词、折叠状态、模式恢复。
3. 对受保护进程选中详情，等 30 秒后重新选中/滚动 → 命令行重新尝试读取，不出现永久“拒绝访问”。
4. 树形下按 Left / Right 折叠展开；平铺模式下左右键不影响列表。

## 验证口径

- 构建：0 warning / 0 error。
- `--selftest`：现有 43 个用例不回归，新增用例全绿。
- 启动与 125 秒长跑：不应出现可测量的变化；热路径只多一次 `TickCount64` 和状态判断，选中迁移只在用户操作时发生。
- 不跑 `release.ps1`；本轮只提交源码和设计文档。

## 风险与应对

| 风险 | 应对 |
|---|---|
| 重建时选中短暂变 null，详情面板闪一下 | 同一 dispatcher 回合内设回父节点；实测有闪烁再加重建抑制标志 |
| 恢复的折叠 key 已退出 | 首份快照的 `_isDead` 剪枝会丢掉；不要调整 Prune/Flatten 顺序 |
| 二次提权保留旧 `--restore-ui` | `BuildArguments` 过滤旧参数后再追加 |
| 30 秒 TTL 造成重试风暴 | 只在 `Request` 时惰性过期，`_pending` 去重，队列容量不变 |
| 临时状态文件损坏 | 版本、大小、时间戳三重校验，失败静默忽略并删除 |
| 左右键与 ListView 默认行为冲突 | 只在树形、有子节点、目标状态确实变化时 `Handled` |

## 实施结果（2026-11-22）

已按模块提交：

| 提交 | 内容 |
|---|---|
| `d916c09` | Round 5-1：折叠时选中态迁移到被折叠节点 |
| `89cfd38` | Round 5-2：提权重启恢复搜索、折叠和视图模式 |
| `a6431dc` | Round 5-3：命令行失败缓存 30 秒 TTL |
| `556dd50` | Round 5-4：树形模式左右键折叠/展开 |
| `22a6cdd` | Round 5-1 修正：`ToggleExpanded` 目标状态取反 |

### 验证

- Release 构建：0 warning / 0 error。
- `--selftest`：**60 passed, 0 failed**（原 43 + 新增 17）。
- `--diag-round3` / `--diag-round4`：退出码 0。
- 启动冒烟：`--probe-exit` 首帧 842.5ms（单次样本，与基线口径一致）。
- UI Automation 实测：
  - 选中子进程后折叠父进程，选中迁移到父行（456 → 456）。
  - Left / Right 切换展开图标（U+25BE ↔ U+25B8）。
  - `--restore-ui` 启动后 `SearchBox=svchost`，状态文件被删除。
- UAC 弹窗本身仍无法在自检中自动化；提权后的恢复逻辑通过状态文件与参数拼接用例覆盖，UAC 交互留手工验证。
- Round 5 当时未运行 `release.ps1`；最终 0.2.0 发布包见下节。

## 后续补丁：收尾三个洞（2026-11-22）

| 提交 | 内容 |
|---|---|
| `a910225` | 排序状态抽成 `SortState` 纯状态机，新增 3 个自检用例 |
| `b0a320c` | `UiState` 保存/恢复 `SortKey` / `SortDirection`，兼容旧 JSON |
| `1390981` | 批量替换后恢复仍可见的选中行；新增树形/平铺选中 UIA 用例 |
| `6a749ca` | 列头排序箭头 ▲/▼；树形挂起时隐藏 |

验证：

- `--selftest`：**65 passed, 0 failed**。
- UIA：PID 升序在树形/平铺往返、搜索强制平铺、清空搜索后保留；`--restore-ui` 首屏按保存的排序显示；排序箭头可见；折叠大量分支后选中子行，切平铺选中不丢。
- 排序不是实时排序：这是 `ListCollectionView` 的既定行为，README 已注明；箭头只表示当前排序条件。

### 0.2.0 发布产物

- 包：`dist\TaskMgrLite-win-x64-0.2.0.zip`，59.9MB（260 files / 137.8MB unpacked）。
- SHA256：`26D002013906D31075DC20F0974CEEFAD388E2D2ADFD3895189A2339D85CE9F3`。
- exe 文件版本：`0.2.0.0`；`VERSION.txt`：`TaskMgrLite 0.2.0`。
- `release.ps1 -Version 0.2.0 -Ui` 验证：selftest 65/0、diag-round3/4 通过、树形/平铺选中 UIA 通过、右键菜单 UIA 4/4 通过。
- 最终包因 README 文案更新用 `release.ps1 -Version 0.2.0` 重建一次（selftest + diag 仍全绿）；代码与 UI 验证通过的构建一致。
- 包内无 PDB / XML / log；README、LICENSE、docs、.NET `LICENSE.txt`、`ThirdPartyNotices.txt` 齐全。
