# Changelog

## [0.1.05] - 2026-11-27

### 变更

- GPU 采集整体迁移到 DXCore（`dxcore.dll`）：删除 `GpuMemoryReader` 与 `DxgiNative`，不再使用 `IDXGIAdapter3.QueryVideoMemoryInfo`。
- 新增全系统 GPU 引擎占用率：`AdapterEngineRunningTimeMicroseconds` 逐引擎差分，整卡取各引擎最大值（与任务管理器一致，NVENC 忙时会高于 3D）。
- 显存改为全系统口径：总量取 `DedicatedAdapterMemory`，已用取 `AdapterMemoryUsageBytes` 的 Resident（与 PDH `Dedicated Usage` 逐字节一致）。
- 选卡按专用显存最大的硬件适配器，软件适配器（WARP）排除；初始化失败自动降级为 N/A。
- GPU 卡片新增 60 秒折线图：动态量程（近 20 秒峰值 ×1.2，封顶 100%）并显示当前量程；文字改为“全系统专用显存”。

### 验证

- Release 构建 0 warning / 0 error；`check-file-size` 81 个文件 0 失败。
- `--diag-perf`：5 passed，新增 DXCore 冒烟与显存校验；稳态 0 B（含逐引擎与显存查询）。
- 逐引擎占用率与 PDH `GPU Engine` 编号、数值一一对应；显存与 PDH `GPU Adapter Memory` 一致。
- 高负载实测（RTX 5060）：占用率 30–100% 正常跟随，折线动态量程可读。

## [0.1.04] - 2026-11-26

### 新增

- 独立“性能”视图：CPU 总体、物理内存、系统磁盘吞吐、系统网络吞吐、GPU 显存与预算。
- 原生采集层：`GetSystemTimes`、`GlobalMemoryStatusEx`、`GetPerformanceInfo`、`NtQuerySystemInformation`、`GetIfTable2/GetIfEntry2`、SMBIOS Type 17、DXGI 显存。
- 60 秒环形历史 + 原生 `Polyline` 折线图，点集只创建一次；固定坐标系 + Viewbox 防止平坦曲线被拉伸归一化。
- `--diag-perf`：原生结构体布局、首/次帧契约、稳态零分配、SMBIOS/DXGI 降级检查，并接入 CI 和发布脚本。

### 变更

- 顶部导航新增“性能”，与“进程”“网络端口”互斥；性能页生命周期由 `MainWindow` code-behind 驱动，切走立即停止 1Hz 采集线程。
- 性能页文本与折线 1Hz 刷新；`ScrollViewer` 保证小窗口可滚动到 GPU 卡片。
- GPU 占用率固定 N/A；DXGI 显存 usage 明确标注为本进程口径，全系统占用不引入 PDH。
- CI 增加 `TaskMgrLite-Plus` 分支 push 触发和 Perf contract 步骤。

### 验证

- Debug / Release 构建：0 warning / 0 error。
- `--selftest`：72 passed, 0 failed。
- `--diag-perf`：4 passed, 0 failed（原生布局 / 首次帧 / 稳态零分配 / 可选硬件 smoke）。
- 真机 1Hz 性能页：采集器稳态 0 B/次；页面激活 `--stats` 约 0.20–0.24MB/s，切走后 0.08–0.09MB/s。
- 性能页线程生命周期：25 → 26 → 25，日志 `loop exit` + `deactivate joined=True`。
- `check-file-size`：81 个文件 0 失败；GitHub Actions push 与 PR 均通过。

## [0.1.03] - 2026-11-26

### 新增

- 独立“网络端口”视图：左侧导航切换，全系统 TCP/UDP IPv4/IPv6 列表，2Hz 后台轮询。
- 端口列表支持按协议、地址、状态、PID、进程名搜索，列头排序，完整虚拟化（Recycling）。
- 端口帧按 `协议 + 本地端点 + 远程端点 + PID` 做 UI Diff；无变化时零 `CollectionChanged`。
- 进程名通过当前进程快照做内存映射，不新增 `OpenProcess` / P/Invoke。
- 文件规模标准与 `check-file-size.ps1`：硬上限 + 存量棘轮，已接入本地检查和 CI。
- 正式 UIA 回归脚本 `scripts/test-network-view.ps1`。

### 变更

- 删除进程详情底部的旧端口查询面板、`PortQueryState`、`QueryPortsCommand` 和 `PortReader.Read(pid)`。
- `PortReader.ReadAll()` 成为唯一端口读取入口，稳定排序后直接供 UI Diff。
- 切回“进程”立即停止端口轮询并释放端口行；进程列表集合保持驻留，不重建。
- 网络端口视图抽离为 `Views/NetworkView.xaml`，MainWindow 只保留导航和容器切换。
- 进程/端口列头共用 `SortHeaderTemplate`，消除重复排序箭头 XAML。
- `SelfTest` 拆为 `SelfTest.cs/Core/Tree/Snapshot/Ports` partial 文件。
- 设计文档归档到 `docs/design/`，新增 `docs/CODE_STANDARDS.md`。

### 验证

- Release 构建：0 warning / 0 error。
- `--selftest`：72 passed, 0 failed。
- `--diag-round3` / `--diag-round4`：exit 0。
- UIA：端口视图 342 端口仅生成 27 个行容器；PID 升降序、进程名搜索（42/339）、切回进程后集合非空。
- 进程列头 PID 升降序 UIA 通过。
- 交流供电热启动各 10 次：当前中位 844.5ms（剔除两次 3s 级扫描后典型 800.5ms），同机 0.1.02 基线 787.5ms。
- `[enum]`：当前 1.6KB 均值 / 61KB max，基线 2.3KB / 61KB；无 XAML 重构导致的分配升高。
- 网络视图：CPU 0.36–0.38s/10s，专用工作集 65.2–65.8MB，Gen2=0。

## [0.1.02] - 2026-11-22

### 新增

- 按需网络端口查询：选中进程后手动查询 TCP/UDP IPv4/IPv6，显示本地/远程地址与 TCP 状态。
- 查询状态机使用 Version + CurrentKey 防串号；切换选中行立即清空端口、状态和缓存。
- `AccessDenied` 显示“无权限读取”，部分表失败显示已加载数量，不弹全局异常。

### 变更

- 数值列首次点击默认降序，文本列保持升序。
- 命令行禁用排序并 Toast 提示；列头弱化，不可排序。
- 树形下保留排序箭头，降低透明度表示挂起；箭头改为 Accent 色。
- 模式按钮改为动作文案（切到树形 / 切到平铺）。

### 验证

- `--selftest`：69 passed, 0 failed。
- UIA：TCP4/TCP6/UDP4 端口、查询中禁用、切换行清空、排序/模式交互均通过。

## [0.1.01] - 2026-11-22

### 新增

- 树形模式 Left/Right 折叠/展开。
- 树形/平铺往返保留列排序，列头显示 ▲/▼。
- 提权重启恢复搜索词、折叠集合、视图模式和列排序。
- 命令行 `AccessDenied` / `Error` 30 秒惰性重试。
- 批量替换后保持仍可见的选中行。
- 应用图标覆盖 exe、窗口和发布包。

### 修复

- 折叠时选中态迁移到被折叠节点，详情面板不清空。
- `ToggleExpanded` 目标状态取反。
- 右键菜单 UIA 等待真实主窗口，避免隐藏窗口时序失败。

### 验证

- `--selftest`：65 passed, 0 failed。
- `--diag-round3` / `--diag-round4`：通过。
- UIA：排序往返、提权恢复排序、排序箭头、折叠后切平铺选中、右键菜单 4/4。
- 0.1.01 性能重测：启动 10 次中位 813ms；125 秒长跑专用工作集 max 69.1MB。

### 发布

- 包：`TaskMgrLite-win-x64-0.1.01.zip`（59.9MB，260 files / 137.8MB unpacked）。
- SHA256：`7B9AAFB97BC9449BB7F839E48021D484A0CAD718F9D783648C59A5356DB15258`。
