# Round 4：右键菜单与详情面板

本轮交付：完整右键菜单、底部详情面板、交替行背景、DWM 圆角与 Mica（失败自动回退纯暗色）。

## 1. 数据来源与状态流

详情面板不重新枚举进程，也不触发全量快照重建：

```
ListView.SelectedItem
        │ TwoWay
        ▼
MainViewModel.SelectedRow
        │
        ├─ 快照已有字段：Name / PID / ParentPid / StartTime
        ├─ CommandLineService.Request(key)   // 复用 Round 3
        └─ ProcessDetailsService.Request(key) // 路径 + 用户名
```

`StartTime` 来自 `ProcessSample.CreateTime`（FILETIME 100ns），在 `CreateRow` 时转成本地时间；不是选中时再查。选中只补两个懒加载字段：全路径和用户名。

不可变快照仍然是唯一结构来源。详情读取只接收 `ProcKey`，不持有快照，也不回头改行集合。

## 2. 路径与用户名读取

`ProcessDetailsReader` 是无状态静态读取器，可由详情 worker 或用户动作的 `Task.Run` 并发调用：

1. `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE)`；
2. `GetProcessTimes` 创建时间必须匹配 `ProcKey.CreateTime`，防 PID 复用；
3. `WaitForSingleObject(handle, 0)` 排除已退出；
4. `QueryFullProcessImageNameW` 读全路径（缓冲区不足时 1KB→2KB→…→32KB）；
5. `OpenProcessToken(TOKEN_QUERY)` → `GetTokenInformation(TokenUser)` → `LookupAccountSidW`，拼成 `DOMAIN\USER`。

状态：`Ok / AccessDenied / Exited / ProcessReused / Error`。路径和用户名各自带状态，允许“路径可读、用户名拒绝访问”的部分成功。

`ProcessDetailsService` 与 `CommandLineService` 同构：单后台线程、有界队列 64、pending 去重、LRU 256、失败负缓存、进程退出 `Forget`。只在选中行时触发，不进入 1Hz 热路径。

## 3. 右键菜单

行菜单：

- 打开文件所在位置；
- 复制 PID；
- 复制路径；
- 结束进程；
- 结束进程树。

空白/表头菜单只有“刷新”。

右键时先命中 `ListViewItem` 并选中；命中不到则切空白菜单。选中变化时也把菜单切到行菜单，兼容键盘上下文菜单。

路径处理：

- `ExplorerLauncher.BuildSelectArguments` 负责 `/select,"path"` 的引号；
- 打开前检查 `File.Exists`，文件不存在给出明确提示；
- `Process.Start(explorer.exe)` 的 `Win32Exception` / 其他异常统一转 Toast；
- 路径读取拒绝访问时，Toast 带“提权重启”按钮；
- 复制路径前同样先确保路径；剪贴板异常（被其他程序占用）也走 Toast，不崩溃。

刷新：

- `ProcessCollector` 增加 `AutoResetEvent _refresh`，采集线程用 `WaitHandle.WaitAny` 同时等 stop/refresh；
- 刷新信号立即采一次，然后把下一次节拍对齐到 `now + 1s`；
- 不排队、不叠加，多个刷新信号最多合并成一次。

## 4. 详情面板 XAML

底部 `Border`，`Visibility` 绑定 `HasSelection`，选中行时展开。左侧名称/PID/父 PID/启动时间/用户，右侧路径/命令行。路径单行省略 + ToolTip；命令行自动换行，最大高度 46。详情字段直接绑定 `SelectedRow.*`。

`SelectedRow` 的 setter 是唯一详情触发点：

```csharp
set
{
    if (!Set(ref _selectedRow, value)) return;
    OnPropertyChanged(nameof(HasSelection));
    if (value is null) return;

    ProcKey key = value.KeyStruct;
    if (!_rowsByKey.ContainsKey(key)) return;

    _commandLines.Request(key);
    _details.Request(key);

    if (string.IsNullOrEmpty(value.CommandLine)) value.CommandLine = "读取中…";
    if (string.IsNullOrEmpty(value.ImagePath)) value.ImagePath = "读取中…";
    if (string.IsNullOrEmpty(value.UserName)) value.UserName = "读取中…";
}
```

快照删除选中行时，`SelectedRow = null`，详情面板自动收起。

## 5. 交替行背景

WPF `ListView` 没有 `AlternatingRowBackground` 属性（这是 DataGrid 的）。用：

```xml
<ListView AlternationCount="2" ... />
```

配合全局 `ListViewItem` 样式触发器：

```xml
<Style.Triggers>
  <Trigger Property="ItemsControl.AlternationIndex" Value="1">
    <Setter Property="Background" Value="{StaticResource RowAlt}" />
  </Trigger>
</Style.Triggers>
```

模板的 `Border` 使用 `TemplateBinding Background`，所以 hover/selected 触发器仍然优先。

## 6. DWM 圆角与 Mica

`OnSourceInitialized` 里先做暗色标题栏，再尝试：

```csharp
int corner = DWMWCP_ROUND;   // 2
DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

int backdrop = DWMSBT_MAINWINDOW; // 2
int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
```

只有 `hr == 0`（系统接受 Mica）才：

1. `DwmExtendFrameIntoClientArea(hwnd, margins: -1)`；
2. 把 `HwndSource.CompositionTarget.BackgroundColor` 设为透明；
3. 窗口背景改成 90% 暗色（`#E6 14 14 17`），保留材质感并保证浅色系统主题下文字可读。

老系统/禁用透明效果时 `hr != 0`，完全不执行透明化，保持 XAML 的纯暗色背景。`[dwm] corner-hr=0 backdrop-hr=0` 会写进日志，方便现场判断是否生效。

## 7. 验证

### 7.1 单元测试

`--selftest` 从 32 个用例扩到 **37 个**，新增：

- 详情状态与显示文本（拒绝访问/已退出/Ok）；
- Win32 错误 → 详情状态分类；
- FILETIME → 本地时间格式化；
- Explorer `/select,"path"` 参数与空路径/不存在路径拒绝；
- 行启动时间显示。

Windows 11 本机 .NET 11：**37 passed, 0 failed**。

### 7.2 真实内核路径诊断

`--diag-round4`（不创建窗口）：

1. 读自己的全路径 + 用户名；
2. 空路径/不存在路径不会启动 Explorer；
3. 启动采集线程，`RequestRefresh` 后等待下一份快照。

实测：**3 passed, 0 failed**，手动刷新唤醒延迟 **12ms**。

### 7.3 UI 验证

UI Automation 选中 `TaskMgrLite.exe` 行后，详情面板实际显示：

- 名称：TaskMgrLite.exe
- PID 55312 / 父 PID 8644
- 启动时间：2026-11-22 10:54:37
- 用户：DESKTOP-48LFTDG\Hello F
- 路径：`D:\ALL\工具\任务管理器\project-1\publish\TaskMgrLite.exe`
- 命令行：`"...\TaskMgrLite.exe" --real`

产物：`artifacts\details-ui.txt`。

DWM 圆角 + Mica：日志 `[dwm] corner-hr=0 backdrop-hr=0`，系统接受两种属性；截图 `artifacts\round4-window.png`、`artifacts\round4-details.png` 已做视觉检查，窗口和详情面板没有黑屏/透明穿透。

### 7.4 右键菜单 UI 自动化

新增 `scripts/test-context-menu.ps1`：UI Automation + 鼠标 P/Invoke，真实右键点击并调用菜单项。

覆盖：

1. 搜索自身 PID，右键行 → “复制 PID”，校验剪贴板；
2. 再次右键 → “复制路径”，校验剪贴板等于进程全路径；
3. 启动受控 `ping.exe`，搜索它的 PID，右键 → “结束进程”，校验进程退出；
4. 搜索不存在的 PID 制造空白区域，右键 → 只出现“刷新”并调用一次。

细节：

- 关闭的 ContextMenu 仍可能留在 UIA 树里，脚本按“离点击点最近的菜单项 + 同一左边缘的垂直堆栈”定位当前弹出的菜单；
- 每次右键前用 `WScript.Shell.AppActivate` + `SetForegroundWindow` 把窗口拉回前台，避免测试 runner 或其他桌面窗口抢焦点导致“空点击”；
- 测试会移动鼠标并覆盖剪贴板，`finally` 里恢复鼠标位置和原剪贴板文本；
- 清理受控 ping 和 TaskMgrLite 进程。

实测：**连续 3 次 4/4 PASS**。`check.ps1 -Ui` 已接入，日常检查可以跑：

```powershell
.\scripts\check.ps1 -NoBuild -Ui -StartupRuns 0
```

### 7.5 资源

64 秒真实进程长跑（489 个进程，详情面板选中 TaskMgrLite 自身，UI Automation 同时附加）：

- 专用工作集：min 60.5 / mean 60.7 / max **62.2MB**（<80MB）
- 工作集 max 159.5MB；私有提交 max 111.4MB
- GC：Gen0=1，Gen1=0，Gen2=0
- 句柄：731–736，无增长趋势
- 分配速率：mean 0.03MB/s，max 0.24MB/s
- 可见行 = 全量进程数

Mica + 圆角开启后，47 秒长跑（同样选中详情、UIA 附加）：

- 专用工作集：min 61.8 / mean 62.2 / max **63.6MB**（仍 <80MB）
- 工作集 max 159.4MB；私有提交 max 97.8MB
- Gen0=1，Gen1=0，Gen2=0；句柄 730–735；分配 mean 0.03 / max 0.26 MB/s

启动：3 次 926 / 872 / 855ms；Mica 后单次 probe 1008ms，最终 `check.ps1` 918ms。

## 8. 已知取舍

1. Mica 使用 90% 暗色罩层，材质感比较克制；如果现场觉得太弱，可以调低 alpha，但要重新做文字对比度验收。
2. 右键菜单 UI 测试会移动鼠标并覆盖剪贴板；`finally` 已恢复，但测试运行期间不要操作桌面。
3. 受保护进程的路径/用户名会显示“拒绝访问”；Toast 提供提权重启，但提权后仍可能无法读取 PPL。
4. 详情面板显示选中时的路径/用户/启动时间；进程运行期间这些字段不变，符合身份语义。
5. UI Automation 附加会抬高 WPF 的辅助功能内存；63.6MB 是“Mica + 选中详情 + UIA 已附加”的上限值，仍低于 80MB。

## 9. 本轮改动文件

- `Core/ProcessDetailsReader.cs`：路径/用户名读取与状态。
- `Core/ProcessDetailsService.cs`：详情 worker、缓存、去重。
- `Core/ProcessTime.cs`：FILETIME 格式化。
- `Core/ExplorerLauncher.cs`：`/select` 参数与路径校验。
- `Core/ProcessCollector.cs`：`RequestRefresh` 与唤醒事件。
- `Core/Round4Diagnostics.cs`：`--diag-round4`。
- `Interop/NativeMethods.cs`：QueryFullProcessImageName、token、LookupAccountSid、DWM 圆角/Mica。
- `ViewModels/ProcessRowViewModel.cs`：StartTime / ImagePath / UserName。
- `ViewModels/MainViewModel.cs`：SelectedRow、详情服务、GetDetailsAsync、RequestRefresh。
- `MainWindow.xaml` / `MainWindow.xaml.cs`：行/空白菜单、详情面板、交替行、复制/打开/刷新、DWM 圆角/Mica。
- `Core/SelfTest.cs`：新增 5 个 Round 4 用例。
- `scripts/test-context-menu.ps1`：右键菜单 UIA 自动化测试。
- `scripts/check.ps1`：接入 `--diag-round4` 与 `-Ui`。
