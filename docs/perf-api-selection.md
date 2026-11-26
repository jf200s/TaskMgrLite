# TaskMgrLite 性能监控 API 选型报告

> 适用分支：`my-project-2`　技术栈：C# / .NET 11 / WPF　系统基线：Windows 10 1709+ / Windows 11 x64
>
> 硬约束：不引入 PDH、不引入 WMI、不引入第三方图表库；CPU 以 `GetSystemTimes` 的内核真值为准；1 Hz 采样。
>
> 日期：2026-11-26

## 0. 结论

| 指标 | 选定方案 | 需要管理员 | 稳态托管分配 | 单次调用开销（量级） |
|---|---|---:|---:|---:|
| 内存总量 / 可用 / 已用 | `GlobalMemoryStatusEx` | 否 | 0 | < 1 µs |
| 已提交 / 提交上限 | `GetPerformanceInfo` | 否 | 0 | 1–3 µs |
| 系统缓存 | `GetPerformanceInfo.SystemCache`；要任务管理器口径再用 `NtQuerySystemInformation(SystemMemoryListInformation)` | 否 | 0 | 1–5 µs |
| 内存条速度 | `GetSystemFirmwareTable('RSMB')` 解析 SMBIOS Type 17 | 否 | 0（复用缓冲） | 1–5 ms，仅启动时一次 |
| CPU 总体使用率 | `GetSystemTimes` 两次差值 | 否 | 0 | < 1 µs |
| CPU 每核使用率 | `NtQuerySystemInformation(SystemProcessorPerformanceInformation = 8)` | 否 | 0（复用缓冲） | 2–10 µs（≤ 64 逻辑核） |
| 磁盘每盘吞吐 | `IOCTL_DISK_PERFORMANCE` | **是**（打开 `\\.\PhysicalDriveN`） | 0 | 10–50 µs / 盘 |
| 磁盘系统总吞吐 | `NtQuerySystemInformation(SystemPerformanceInformation = 2)` 的 I/O 计数 | 否 | 0 | 2–5 µs，偏移需按构建校验 |
| 网络上下行 | `GetIfTable2` 建表 + `GetIfEntry2` 轮询 | 否 | 后者 0；前者由 API 分配非托管表 | 50–200 µs / 5–20 µs |
| GPU 本进程显存 / 预算 | DXGI（`IDXGIAdapter3.QueryVideoMemoryInfo`） | 否 | 初始化后约 0 | 10–100 µs |
| GPU 占用率 | DXGI **做不到**；只能 `D3DKMTQueryStatistics`（不受支持）或 v1 不做 | 通常否，跨进程待实测 | 可做到 0 | 50–500 µs+，结构体随构建变化 |
| WMI | 禁用 | 否 | 大 | 首次 100 ms–数秒 |
| PDH | 禁用 | 否 | 可控 | 初始化 ms 级；GPU 计数器阵列更贵 |

一句话版本：

- **内存**：`GlobalMemoryStatusEx` 只够“总量 / 可用 / 已用”。**提交量必须换 `GetPerformanceInfo`**，因为它文档明确说明 `MEMORYSTATUSEX` 的 pagefile 字段是“系统与当前进程取较小值”，不是系统提交量。内存条频率不需要 WMI，SMBIOS 一次解析即可。
- **CPU**：`GetSystemTimes` 就是这套架构的最优解；多核用 `NtQuerySystemInformation` class 8，一次调用拿全部逻辑核，缓冲复用后零分配。
- **磁盘**：这是唯一和“无 PDH、无管理员”直接冲突的指标。**没有管理员权限就没有受支持的“每盘吞吐”方案**；要么 v1 只显示系统总吞吐，要么把每盘功能放进提权模式走 `IOCTL_DISK_PERFORMANCE`。
- **网络**：`GetIfTable2` 满足需求，稳态轮询用 `GetIfEntry2`，不需要 PDH。
- **GPU**：**DXGI 没有“占用率”这个能力**，只有适配器信息、显存和预算。无 PDH / 无 WMI 时，占用率只能走不受支持的 `D3DKMTQueryStatistics`；建议 v1 只做显存，占用率明确显示 N/A，v2 再评估。

---

## 1. 口径与前提

### 1.1 “纯内核真值”意味着什么

CPU 使用率按 `busy = (kernel - idle) + user`、`total = kernel + user` 计算，即“CPU 在非空闲状态下度过的时间比例”。它不包含任务管理器的 `% Processor Utility` 那种频率 / Turbo 折算，因此在大小核混合 CPU、长时间睿频或降频的机器上，数字可能和任务管理器不同。这是刻意选择，不是缺陷；UI 上应标注“系统时间占比”，避免用户拿它和任务管理器逐位对比。

### 1.2 “零分配”的边界

本文的“零分配”指**稳态采样路径上的托管堆分配为 0**，不包含：

- 首次 P/Invoke 的 stub 解析、JIT、类型初始化；
- 首次 DXGI / SMBIOS 的惰性初始化；
- 采样线程之外的 UI 字符串格式化、`PointCollection` 之外的图表对象；
- API 自身在非托管堆上的分配（如 `GetIfTable2` 的表）。

验收方式：`GC.GetAllocatedBytesForCurrentThread()` 在预热后连续采样 60 次，差值应为 0；同时用 `Stopwatch` 记录单次耗时。

### 1.3 采样节拍

1 Hz 与现有进程采集一致。60 秒环形缓冲区在启动时一次性分配，采样线程只写结构体，UI 只读快照；不要在每个 tick `new` 数组、`List`、`PointCollection` 或格式化字符串。

---

## 2. 内存

### 2.1 `GlobalMemoryStatusEx` 够不够

**够“总量 / 可用 / 已用”，不够“已提交 / 缓存 / 内存条速度”。**

它一次调用返回：

- `ullTotalPhys`：物理内存总量；
- `ullAvailPhys`：当前可立即复用的物理内存，文档定义为 **standby + free + zero 三个列表之和**；
- `dwMemoryLoad`：0–100 的粗略占用百分比；
- `ullTotalPageFile` / `ullAvailPageFile`：**系统与当前进程取较小值**的提交上限 / 当前进程还能提交的量。

关键坑：`ullTotalPageFile` / `ullAvailPageFile` 不是系统提交量和系统提交上限。微软文档明确写了：要拿系统级提交信息，调用 `GetPerformanceInfo`，用 `CommitLimit - CommitTotal` 计算系统可用提交。用 `MEMORYSTATUSEX` 相减会在有 Job 限制、不同页面文件配置或进程提交上限较低的机器上偏小。

`ullAvailPhys` 与任务管理器“可用”基本同源（standby + free + zero），但任务管理器还会把 modified 等列表算进自己的口径，所以不保证逐字节一致；作为“可用”展示足够。

### 2.2 已提交：`GetPerformanceInfo`

`GetPerformanceInfo`（psapi，XP 起，无需管理员）返回 `PERFORMANCE_INFORMATION`：

- `CommitTotal`：系统当前已提交页数；
- `CommitLimit`：系统当前提交上限页数；
- `CommitPeak`：重启以来提交峰值；
- `PhysicalTotal` / `PhysicalAvailable`：物理内存总页数 / 可用页数；
- `SystemCache`：**standby + system working set** 的页数；
- `KernelTotal` / `KernelPaged` / `KernelNonpaged`：内核池；
- `PageSize`：页大小；
- `HandleCount` / `ProcessCount` / `ThreadCount`。

换算：`CommitTotal * PageSize` 为已提交字节，`CommitLimit * PageSize` 为提交上限。结构体在 x64 上含 `CommitPeak`，`sizeof` 为 **104 字节**（不是 96）；x86 为 56 字节。调用方必须把 `cb` 设成实际大小。

开销极低，blittable 结构体 + `out` 参数即可零托管分配；不需要 PDH，也不需要 WMI。

### 2.3 缓存：先分清两种“缓存”

- **任务管理器“已缓存”** ≈ standby + modified；
- **`PERFORMANCE_INFORMATION.SystemCache`** = standby + system working set，文档定义如此。

两者不是同一个东西。v1 如果只是展示“系统缓存”，直接用 `SystemCache * PageSize`，零额外调用；如果必须和任务管理器“已缓存”对齐，再解析：

`NtQuerySystemInformation(SystemMemoryListInformation = 0x50)`，缓冲区 168 字节，按 x64 布局：

| 偏移 | 字段 | 含义 |
|---:|---|---|
| 0x00 | `ZeroPageCount` | 零页 |
| 0x08 | `FreePageCount` | 空闲页 |
| 0x10 | `ModifiedPageCount` | 已修改页 |
| 0x18 | `ModifiedNoWritePageCount` | 已修改不写回页 |
| 0x20 | `BadPageCount` | 坏页 |
| 0x28 | `PageCountByPriority[8]` | 8 个优先级的 standby 页 |
| 0x68 | `RepurposedPagesByPriority[8]` | 8 个优先级的复用页 |

`已缓存 ≈ (Modified + ModifiedNoWrite + Σ PageCountByPriority) * PageSize`。这是未文档化结构，需按目标构建校验；失败就退回 `SystemCache`，不要阻塞 UI。

### 2.4 内存条速度：不要 WMI

WMI 的 `Win32_PhysicalMemory.Speed / ConfiguredClockSpeed` 本身也是从 SMBIOS 读的，换 WMI 不会更准，只会带来：

- `System.Management` 依赖与首次查询的 WMI 服务开销（首次 100 ms 到数秒，服务冷启动更久）；
- 查询期间的字符串 / 对象分配；
- 计数器或 WMI 仓库损坏时的额外故障面。

直接用 `GetSystemFirmwareTable('RSMB', 0, ...)` 取原始 SMBIOS：

1. 先调用一次取所需大小，分配一次非托管缓冲；之后跨启动复用。
2. `RSMB` 返回的是 `RawSMBIOSData`：前 8 字节是 Used20CallingMethod / 版本 / DmiRevision / Length，结构表从偏移 8 开始；按 `Length` 截断后遍历 Type 17（Memory Device）。
3. 读取：
   - `Speed`：偏移 **0x15**，WORD，单位 MT/s；
   - `Configured Memory Speed`：偏移 **0x20**，WORD，SMBIOS 2.7+ 才有；
   - `Device Locator` / `Bank Locator`：0x10 / 0x11 的字符串索引；
   - `Manufacturer` / `Part Number`：0x17 / 0x1A 的字符串索引。
4. 优先用 Configured；为 0 或 0xFFFF 时退回 Speed；两者都无效显示“—”，不要猜。

注意单位：SMBIOS 给的是 **MT/s**（等效数据传输率），任务管理器标成 “MHz” 其实也是这个数；DDR 的真实时钟是它的一半。UI 建议写 “3200 MT/s”，避免技术用户挑错。

虚拟机、部分 OEM 笔记本、焊接 LPDDR 的机器可能返回 0、0xFFFF 或错误值；这是 SMBIOS 数据质量，不是解析错误。WMI 同样无能为力。

### 2.5 内存结论

- 基础调用：`GlobalMemoryStatusEx`；
- 提交：`GetPerformanceInfo`（必需，不要用 pagefile 字段硬减）；
- 缓存：v1 用 `SystemCache`，需要任务管理器口径再上 class 0x50；
- 频率：SMBIOS，一次性；
- WMI：不需要，且违反项目约束。

---

## 3. CPU

### 3.1 总体使用率：`GetSystemTimes`

`GetSystemTimes(out idle, out kernel, out user)` 返回自开机以来的累计时间（100 ns 单位）：

- `kernel` **包含** `idle`；
- `busy = (kernel - idle) + user = kernel + user - idle`；
- `total = kernel + user`；
- `usage% = busy / total * 100`。

实现要点：

- 第一次调用只建立基线，不产出使用率；第二次开始才有差值。UI 第一秒显示“—”，不要显示 0%。
- 两次采样之间 `total == 0`（同一 tick 内重复调用）时跳过，沿用上一次有效值。
- 计数器正常单调；仍要防 `idle/kernel/user` 任一倒退（休眠恢复、异常注入），倒退时重建基线。
- 系统时钟 tick 默认约 15.6 ms，1 Hz 采样下量化误差小于 2%，可接受；不要用 100 ms 采样去追瞬时峰值。
- 返回的是所有处理器的累计和；与任务管理器总体曲线形状一致，但任务管理器做频率折算，绝对值可能不同。

**不需要 PDH。** PDH 的唯一优势是 `% Processor Utility` 的“任务管理器口径”和现成的每核实例；为了这个引入毫秒级初始化、计数器注册表损坏风险和额外的 counter 路径，不符合项目目标。

### 3.2 多核：`NtQuerySystemInformation` 是合适的

`SystemProcessorPerformanceInformation = 8` 一次返回每个逻辑处理器的 `SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION`，x64 下每项 **48 字节**：

| 偏移 | 字段 | 说明 |
|---:|---|---|
| 0x00 | `IdleTime` | 空闲累计 |
| 0x08 | `KernelTime` | 内核累计，含 Idle |
| 0x10 | `UserTime` | 用户累计 |
| 0x18 | `DpcTime` | DPC，已含在 KernelTime 内，不要重复加 |
| 0x20 | `InterruptTime` | 中断，已含在 KernelTime 内，不要重复加 |
| 0x28 | `InterruptCount` | 中断计数 |

每核同样按 `1 - idleDelta / (kernelDelta + userDelta)` 计算。缓冲按 `逻辑处理器数 * 48` 预留并跨调用复用，返回 `STATUS_INFO_LENGTH_MISMATCH` 时再增长；解析用 unsafe 偏移直读，零托管分配。调用开销约 2–10 µs（≤ 64 核），1 Hz 完全无压力。

注意：

- 超过 64 个逻辑处理器时，单次 class 8 只覆盖当前处理器组；需要 `NtQuerySystemInformationEx` + `GROUP_AFFINITY` 逐组查询，或 `GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)` 配合组切换。消费级机器可先不做，但代码里要留能力判断。
- `Environment.ProcessorCount` 受进程亲和性 / Job 限制影响，可能小于系统逻辑核数；要显示“系统全部核心”时用 `GetActiveProcessorCount`。
- 首次每核采样同样只建立基线。

### 3.3 CPU 结论

- 总体：`GetSystemTimes`；
- 每核：`NtQuerySystemInformation` class 8；
- PDH：不需要，且与约束冲突；
- 代价：没有 Turbo / 频率折算，和任务管理器绝对值可能有差异，UI 口径写清楚即可。

---

## 4. 磁盘

这是本报告里唯一需要正面处理“约束冲突”的部分。

### 4.1 `IOCTL_DISK_PERFORMANCE` 的真实门槛

`IOCTL_DISK_PERFORMANCE` 返回 `DISK_PERFORMANCE`，包含：

- `BytesRead` / `BytesWritten`（累计字节）；
- `ReadCount` / `WriteCount`（累计操作数）；
- `ReadTime` / `WriteTime` / `IdleTime`（100 ns）；
- `QueryTime`、`StorageDeviceNumber`、`StorageManagerName`。

但它的使用前提是：

1. 用 `CreateFile` 打开 `\\.\PhysicalDriveN`。在客户端 Windows 上，打开物理磁盘需要管理员权限；用 `dwDesiredAccess = 0` 有时能拿到句柄，但后续 `DeviceIoControl` 仍可能因权限 / ACL 失败，不能作为默认路径。
2. 这个 IOCTL 会**启用**计数器（引用计数式），对应 `IOCTL_DISK_PERFORMANCE_OFF` 关闭。正确做法是进程生命周期内保持句柄，退出时调 OFF；不要每 tick 开关。
3. 服务器 SKU 上磁盘性能计数器可能默认关闭，需要管理员执行 `diskperf -Y`；客户端通常默认开启，但不能假设。
4. 它仍然是同步设备 IOCTL，磁盘忙时可能被拖慢；1 Hz 下影响小，但不要放到 UI 线程。

因此：**它是“提权模式”的合理方案，不是默认模式的方案。** TaskMgrLite 已经有提权重启入口，可以复用：未提权时隐藏每盘卡片或只显示系统总量；提权后打开每盘句柄。

### 4.2 无管理员、无 PDH 的替代：系统级 I/O 计数

`NtQuerySystemInformation(SystemPerformanceInformation = 2)` 的结构体里有一组累计 I/O 计数（x64 布局，按 `ntexapi.h`）：

| 偏移 | 字段 | 含义 |
|---:|---|---|
| 0x00 | `IdleProcessTime` | 空闲进程累计 |
| 0x08 | `IoReadTransferCount` | 累计读字节 |
| 0x10 | `IoWriteTransferCount` | 累计写字节 |
| 0x18 | `IoOtherTransferCount` | 其它 I/O 字节 |
| 0x20 | `IoReadOperationCount` | 累计读操作数（ULONG） |
| 0x24 | `IoWriteOperationCount` | 累计写操作数（ULONG） |
| 0x28 | `IoOtherOperationCount` | 其它操作数（ULONG） |
| 0x2C | `AvailablePages` | 可用页 |
| 0x30 | `CommittedPages` | 已提交页 |
| 0x34 | `CommitLimit` | 提交上限页 |

两次采样差值除以墙钟时间，就是**全系统**读 / 写字节每秒。无需管理员，缓冲复用后零分配，开销约 2–5 µs。

限制与风险：

- 只有全系统总量，**没有每盘拆分**；
- 统计的是 I/O 管理器层面的传输，包含文件系统、部分网络重定向等，不等于“物理磁盘磁头吞吐”；
- 结构体未文档化，早期偏移多年稳定，但仍应把缓冲区大小和偏移做成集中常量，并在目标 Windows 构建上校验；解析越界要失败退出，不能读脏数据。

### 4.3 其它路径为什么不行

- **PDH 物理磁盘计数器**：能满足每盘、无需管理员，但被本分支约束排除。若哪天要“默认模式每盘吞吐”，这是唯一受支持的路径，届时应重新评估“无 PDH”这条约束，而不是继续堆未文档化 IOCTL。
- **ETW `Microsoft-Windows-Kernel-Disk`**：实时内核会话通常需要管理员，且会话管理、缓冲和解析成本都高于收益。
- **逐进程 `GetProcessIoCounters` 求和**：要打开大量进程句柄，受保护进程拿不到，开销和失败面都大，不适合做系统总量。

### 4.4 磁盘结论

- v1 默认模式：显示**全系统读写吞吐**（class 2 差值），明确标注“系统总计”，不做每盘；
- 每盘：提权模式 + `IOCTL_DISK_PERFORMANCE`，保持句柄、退出时 OFF；
- 不要在默认模式假装能拿到每盘数据；
- 如果产品上“默认模式每盘吞吐”是硬需求，那么 no-PDH 约束必须让步——这是唯一一处无法两全的地方。

---

## 5. 网络

### 5.1 `GetIfTable2` 能满足

`GetIfTable2`（netioapi，Vista+，无需管理员）返回 `MIB_IF_TABLE2`，每项 `MIB_IF_ROW2` 包含：

- `InterfaceLuid` / `InterfaceIndex`：稳定标识；
- `Alias` / `Description`：显示名；
- `Type` / `MediaType` / `AccessType`：接口类型；
- `OperStatus` / `AdminStatus` / `MediaConnectState`：状态；
- `TransmitLinkSpeed` / `ReceiveLinkSpeed`：链路速率，可用于算占用百分比；
- `InOctets` / `OutOctets`：64 位累计字节，直接做差值就是上下行；
- `InUcastPkts` / `OutUcastPkts`、`InDiscards` / `OutDiscards`、`InErrors` / `OutErrors`：可选的包 / 错误指标。

64 位计数器在 1 Hz、万兆链路上也不会回绕；不需要额外处理。

### 5.2 稳态零分配的做法

`GetIfTable2` 会把整张表分配在非托管堆上，调用方用 **`FreeMibTable`** 释放（不是 `NetApiBufferFree`）；托管侧如果只接 `IntPtr`，托管分配为 0，但每次都有一个非托管分配 + 拷贝，约 50–200 µs。文档还提醒表头与 `MIB_IF_ROW2` 数组之间、数组项之间可能存在对齐 padding，解析时不要假设“表头后立刻紧挨第一项”，要按对齐后的行大小推进。

更好的稳态路径：

1. 启动 / 热插拔时调用一次 `GetIfTable2`，缓存需要的 `InterfaceLuid`（以及显示名、类型）；
2. 每个 tick 对每个缓存的 LUID 调用 `GetIfEntry2(ref MIB_IF_ROW2)`；
3. `MIB_IF_ROW2` 约 1.3 KB，用 `unsafe fixed` 数组定义，实例作为采集器字段复用；`GetIfEntry2` 只填结构体，不分配；
4. 单接口约 5–20 µs，10 个接口也在 200 µs 内。

热插拔 / VPN 拨号用 `NotifyIpInterfaceChange` 注册回调，或每 5–10 秒重建一次表；不要每 tick 重建。

### 5.3 过滤与双计

- 排除 `IF_TYPE_SOFTWARE_LOOPBACK`（24）和 `IF_TYPE_TUNNEL`（131）；
- 排除 `FilterInterface` 标志的过滤驱动接口，否则同一流量会被算两次；
- VPN 场景下物理网卡和虚拟网卡都会计数：**逐适配器展示**最诚实；如果要做“总计”，只汇总 `HardwareInterface` 的物理适配器，并在 UI 说明不含 VPN 虚拟接口；
- 不要用 `InOctets + OutOctets` 直接当“带宽占用”，算百分比要除以链路速率，且全双工上下行应分开。

### 5.4 网络结论

`GetIfTable2` + `GetIfEntry2` 足够，无需 PDH、无需管理员；成本低，64 位计数稳定；主要工作是过滤规则和 UI 口径，不是 API 选型。

---

## 6. GPU

### 6.1 先说结论：DXGI 没有“占用率”

DXGI 能给的是：

- `IDXGIFactory1::EnumAdapters1` → `DXGI_ADAPTER_DESC1`：适配器名、LUID、专用显存、共享内存；
- `IDXGIAdapter3::QueryVideoMemoryInfo`：按 `DXGI_MEMORY_SEGMENT_GROUP_LOCAL / NON_LOCAL` 返回 `Budget`、`CurrentUsage`、`AvailableForReservation`；
- 适配器 LUID 可用于把其它数据源映射回具体 GPU。

关键口径：`QueryVideoMemoryInfo` 的 `Budget / CurrentUsage` 是**当前进程**的预算和用量，不是全系统显存占用。实测一个没有 D3D 资源的进程 `CurrentUsage` 恒为 0，而 PDH `\GPU Adapter Memory(*)\Dedicated Usage` 显示系统占用约 2.8 GB。因此 DXGI 只能做“本进程显存”，做不了任务管理器那种“全系统 2.8 / 8.0 GB”。

它**不提供**引擎占用率、每进程 GPU 时间、3D / Copy / VideoDecode 引擎利用率。DXGI 是显示 / 显存 / 交换链 API，不是 GPU 调度统计 API。因此“直接调 DXGI 拿 GPU 占用”这个方案在 API 层面不成立。

### 6.2 无 PDH / 无 WMI 时还剩什么

任务管理器的 GPU 占用来自 WDDM 调度器暴露的性能计数器（`\GPU Engine(*)\Utilization Percentage`，Windows 10 1709+ / WDDM 2.0+），这些计数器底层对应 `D3DKMTQueryStatistics`。绕过 PDH 直接用 `D3DKMTQueryStatistics` 是可行的，但代价明确：

1. 微软文档对该结构的原话是 **“reserved for system use. Do not use.”**，属于不受支持的接口；
2. 需要 `D3DKMTOpenAdapterFromLuid` 打开适配器，先查 node 数量 / 引擎类型，再按进程 / node 查 `RunningTime`，两次差值除以墙钟时间得到占用；
3. 结构体布局随 WDDM / Windows 构建变化，必须按 build 适配；用错长度会直接读脏或失败；
4. 驱动差异大：部分 NVIDIA GeForce 平台对 `D3DKMT_QUERYSTATISTICS_NODE` 返回 `STATUS_INVALID_PARAMETER`，Process Hacker / GPUView 也会失败；
5. 读取其它进程的统计在部分系统上可能需要提升权限；至少要处理 `STATUS_ACCESS_DENIED` 并降级；
6. 每 tick 查询多个进程 / node，开销可能到几百 µs 甚至毫秒级，且随进程数增长。

ETW 的 `Microsoft-Windows-DxgKrnl` 也能拿到类似数据，但实时内核会话通常要管理员，复杂度和开销不低于 D3DKMT。

### 6.3 建议

- **v1**：DXGI 只做“适配器名 + 专用显存总量 + 本进程预算 / 使用量”；GPU 占用率显示 N/A，不要用 0% 假装有数据。显存指标本身对用户有价值，且完全受支持、无需管理员；但必须标明是**本进程**口径，不能冒充全系统显存占用。
- **v2（可选 spike）**：单独分支验证 `D3DKMTQueryStatistics`，在 Intel / NVIDIA / AMD、Win10 1709 / Win11 上各跑一遍，统计失败率和开销；只有失败率可接受、且能优雅降级时再合入。不要让它阻塞 v1。
- 如果“GPU 占用率”是硬需求，而 DXGI 又拿不到，那么唯一受支持的路径就是 PDH 的 GPU Engine 计数器；这时需要在“无 PDH”与“GPU 占用”之间做产品取舍，而不是继续找 DXGI 的隐藏能力。
- 不要用 WMI 的 `Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine`：首次查询慢、依赖 WMI 服务、分配大，和项目目标冲突。

### 6.4 多引擎的聚合口径

即使将来用 D3DKMT，也要先定义口径：同一进程在 3D + Copy + VideoDecode 上可以同时跑，**按引擎求和会超过 100%**；取最大值则代表“最忙引擎”。任务管理器的进程列和性能页也不是简单同一个公式。纯内核真值路线建议：每引擎单独展示占用，总计取“最忙引擎”，并在 UI 标注，避免用户拿一个混合数字猜。

---

## 7. 权限、分配、开销总表

### 7.1 不需要管理员

- `GlobalMemoryStatusEx`
- `GetPerformanceInfo`
- `GetSystemTimes`
- `NtQuerySystemInformation`（class 2 / 5 / 8 / 0x50 在常规桌面环境均可用）
- `GetSystemFirmwareTable('RSMB')`
- `GetIfTable2` / `GetIfEntry2`
- DXGI（`CreateDXGIFactory1`、`EnumAdapters1`、`QueryVideoMemoryInfo`）
- `D3DKMTQueryStatistics`：通常不需要，但跨进程 / 特定驱动可能失败，按“待实测”处理

### 7.2 需要管理员

- `IOCTL_DISK_PERFORMANCE`：打开 `\\.\PhysicalDriveN` 需要提升权限；服务器 SKU 还可能需要 `diskperf -Y`
- ETW 内核会话（`NT Kernel Logger` / `DxgKrnl` 实时会话）：通常需要管理员
- `diskperf` 计数器启用 / 关闭

### 7.3 零托管分配

| API | 是否零分配 | 条件 |
|---|---:|---|
| `GlobalMemoryStatusEx` | 是 | blittable 结构体 + `ref` |
| `GetPerformanceInfo` | 是 | blittable 结构体 + `out`，`cb` 正确 |
| `GetSystemTimes` | 是 | 三个 `FILETIME` + `out` |
| `NtQuerySystemInformation` class 8 / 2 / 0x50 | 是 | 复用非托管缓冲，unsafe 偏移解析，不 `PtrToStructure` |
| `GetSystemFirmwareTable` | 是（稳态） | 一次性分配 / 复用缓冲，不每次 new |
| `GetIfTable2` | 托管侧是，非托管侧否 | API 分配表，`FreeMibTable` 释放 |
| `GetIfEntry2` | 是 | 复用 `MIB_IF_ROW2` 字段，unsafe fixed 数组 |
| `IOCTL_DISK_PERFORMANCE` | 是 | `DISK_PERFORMANCE` 栈 / 字段复用 |
| DXGI | 初始化后约是 | 缓存 factory / adapter；不要每 tick 新建 COM 对象 |
| `D3DKMTQueryStatistics` | 可做到 | 预分配查询结构体，按 build 校验长度 |

### 7.4 调用开销排序（从贵到便宜）

1. **WMI**：首次 100 ms–数秒，服务内存与分配都大；禁用。
2. **PDH 初始化**：`PdhOpenQuery` / `PdhAddCounter` 毫秒级；GPU 计数器阵列每 tick 可达 0.5–3 ms；禁用。
3. **`D3DKMTQueryStatistics`**：几十到几百 µs，随进程 / node 数增长；驱动失败还会带来异常路径。
4. **`GetIfTable2`**：50–200 µs + 非托管分配；只用于建表 / 热插拔。
5. **`IOCTL_DISK_PERFORMANCE`**：10–50 µs / 盘，同步设备调用，磁盘忙时可能抖动。
6. **`NtQuerySystemInformation`**：2–10 µs（class 2 / 8 / 0x50）。
7. **`GetSystemTimes` / `GlobalMemoryStatusEx` / `GetPerformanceInfo`**：亚微秒到几微秒。

以上都是量级，必须在目标机器上实测；不要拿这些数字当 SLA。

---

## 8. 采样与图表实现建议

- **一个采集线程**，`BelowNormal`，1 Hz；内存里一次 `Sample()` 顺序调用 `GetSystemTimes` → `GlobalMemoryStatusEx` → `GetPerformanceInfo`，避免锁和跨线程分配。
- **快照结构体**：只包含数值和时间戳；UI 通过原子交换 / 不可变快照读取。60 秒环形缓冲在启动时 `new MetricsSnapshot[60]`，之后只覆盖索引，不扩容。
- **CPU 首帧**：第一次采样返回 `CpuValid = false`，UI 显示“—”；不要用 0% 误导。
- **WPF 折线**：`Polyline.Points` 只创建一次（60 个 `Point`），之后按索引改 Y；不要每个 tick 新建 `PointCollection` / `Polyline` / `Brush`。`Pen` / `Brush` 在初始化时 `Freeze()`；抗锯齿可以保留，1 Hz 下不是瓶颈。
- **坐标缩放**：在采集线程或 UI 的一次性批处理里算 min/max，不要在 `OnRender` 里 LINQ。
- **字符串**：采样线程不拼字符串；UI 用复用的 `StringBuilder` 或 `Span<char>` 格式化，避免每秒几十个小字符串。
- **启动成本**：SMBIOS、DXGI、磁盘句柄都惰性初始化，别加进 813 ms 启动路径；第一次进入性能页再初始化。
- **失败降级**：任何未文档化结构解析失败都返回“上一份有效值 + 无效标志”，不要让采样线程抛异常或把 UI 清零。

---

## 9. 风险清单

| 风险 | 影响 | 处理 |
|---|---|---|
| `SystemPerformanceInformation` / `SystemMemoryListInformation` 偏移随构建变化 | 读错 / 越界 | 偏移集中定义；先校验缓冲区大小；失败降级 |
| `GetSystemTimes` 在 > 64 逻辑处理器时只覆盖当前组 | 每核数据不全 | 检测组数；需要时用 `NtQuerySystemInformationEx` 逐组 |
| `GetSystemTimes` 无 Turbo / 频率折算 | 与任务管理器绝对值不同 | UI 标注口径；不承诺逐位一致 |
| SMBIOS 返回 0 / 0xFFFF / 错误频率 | 频率显示错误 | 校验 `Length` 和值域；无效显示“—” |
| `MEMORYSTATUSEX` pagefile 字段是进程 / 系统较小值 | 提交量偏小 | 用 `GetPerformanceInfo` |
| `IOCTL_DISK_PERFORMANCE` 需要管理员 / 服务器默认关闭 | 默认模式不可用 | 提权模式启用；默认只显示系统总量 |
| 虚拟 / VPN 网卡重复计数 | 网络总计虚高 | 逐适配器展示；总计只算硬件接口 |
| `D3DKMTQueryStatistics` 不受支持、驱动差异 | GPU 占用失败 / 崩溃 | v2 spike；能力探测；失败 N/A |
| 首次 CPU 采样无效 | 首秒显示异常 | `CpuValid` 标志；UI 显示“—” |

---

## 10. 参考

- [MEMORYSTATUSEX structure (sysinfoapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-memorystatusex)
- [PERFORMANCE_INFORMATION structure (psapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-performance_information)
- [GetPerformanceInfo function (psapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getperformanceinfo)
- [IOCTL_DISK_PERFORMANCE IOCTL (winioctl.h)](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-ioctl_disk_performance)
- [D3DKMT_QUERYSTATISTICS structure (d3dkmthk.h)](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/ns-d3dkmthk-d3dkmt_querystatistics)
- [GetSystemTimes function (processthreadsapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)
- [MIB_IF_ROW2 structure (netioapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/ns-netioapi-mib_if_row2)
- [GetIfEntry2 function (netioapi.h)](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getifentry2)

> 注：本报告按“C# / .NET 11 + P/Invoke、Win10 1709+、1 Hz、无 PDH / 无 WMI”编写；若后续放宽其中任一约束（尤其是默认模式每盘吞吐或 GPU 占用），需要重新评估对应章节，而不是局部打补丁。
