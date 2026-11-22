# TaskMgrLite

一个为“更快到达”而做的 Windows 任务管理器替代品。不是信息更多，而是启动更快、操作路径更短、底层解析更干净。

![TaskMgrLite](docs/screenshot.png)

## 硬核特性

- **原生解析，不走 WMI / PerformanceCounter**：一次 `NtQuerySystemInformation(SystemProcessInformation)` 拿全量进程，unsafe 偏移直读，零分配解析。
- **极速启动**：自包含 ReadyToRun 发布，不压缩单文件；本机 10 次实测中位 **807.5ms**。
- **极低分配**：1Hz 采集稳态 `[enum]` 分配多数为 0，125 秒长跑统计均值 **0.10MB/s**；新进程名称/状态增长时单次峰值约 71KB。
- **300+ 进程可用**：树形结构在采集侧建 parent→children 索引，UI 侧 O(n) 展平为可见行，交给 ListView 虚拟化。
- **不可变快照 + 原子交换**：采集线程只生产不可变数组，UI 无锁读取；UI 忙时覆盖式丢弃旧快照，不排队。
- **命令行懒加载**：只在行进入可视区或选中时走 `OpenProcess + NtQueryInformationProcess(60)`，LRU 512，失败负缓存。
- **详情面板**：选中行才读取全路径、用户名、命令行；启动时间/PID 等直接来自快照，不重新枚举。
- **完整右键菜单**：打开文件所在位置、复制 PID、复制路径、结束进程、结束进程树；空白处右键刷新。
- **树杀按快照 DFS 后序**：后代先于祖先，逐个报告成功/失败；PID 复用用创建时间校验。
- **Windows 11 外观**：DWM 圆角 + Mica（90% 暗色罩层），失败自动回退纯暗色；交替行背景提升长列表可读性。
- **无依赖自检**：`--selftest` 覆盖树、折叠、快照所有权、越界解析、缓冲区增长、批量 Reset、假数据隔离等 **43 个用例**。

## 系统要求

- Windows 10 / 11，x64。
- 不需要安装 .NET：发布包是自包含的。
- 普通权限即可启动；读取/结束受保护进程时会提示提权重启。
- Windows 10 没有 Mica，会自动使用纯暗色窗口。

## 快速开始

从 Release 包解压后直接运行：

```powershell
TaskMgrLite.exe --real
```

**注意：当前构建不带参数时会进入假数据压测模式（400 行）。日常使用必须带 `--real`。**

建议创建一个快捷方式，目标填：

```text
D:\path\to\TaskMgrLite.exe --real
```

## 命令行参数

| 参数 | 作用 |
|---|---|
| `--real` | 使用真实进程数据；不带则使用假数据压测模式 |
| `--stats` | 每 2 秒输出专用工作集/工作集/私有提交/GC/句柄/分配速率，通常与 `--real` 连用 |
| `--selftest` | 不创建窗口，运行纯逻辑自检并返回退出码 |
| `--diag-round3` | 真实内核路径诊断：读命令行、PID 复用保护、单杀/受控进程树杀 |
| `--diag-round4` | 真实路径/用户名读取、Explorer 参数校验、手动刷新唤醒 |
| `--probe-exit` | 启动 3 秒后自动退出，用于启动测量 |

示例：

```powershell
.\TaskMgrLite.exe --stats --real
.\TaskMgrLite.exe --selftest
.\TaskMgrLite.exe --diag-round3
.\TaskMgrLite.exe --diag-round4
```

## 使用说明

- **搜索**：顶部搜索框按进程名或 PID 过滤，150ms 防抖；搜索时自动切平铺。
- **树形 / 平铺**：右上角按钮切换。树形下点 ▾ / ▸ 展开或折叠。
- **排序**：点列头排序即切平铺；点“树形”恢复树序并清空排序。
- **详情面板**：选中任意行，底部显示名称、PID、父 PID、启动时间、用户名、完整路径和命令行。
- **右键行**：打开文件所在位置、复制 PID、复制路径、结束进程、结束进程树。
- **右键空白处 / 表头**：只有“刷新”，立即唤醒采集线程采一次，不改变 1Hz 节拍。
- **结束进程结果**：右下角非阻塞 Toast；失败可点“详情”，拒绝访问时出现“提权重启”。Toast 8 秒自动隐藏。
- **权限不足**：Toast 里的“提权重启”会用 `runas` 重启；也可以直接右键 exe 选择“以管理员身份运行”。

## 性能基线（本机实测）

Windows 11 / .NET 11.0.100：

- 启动 10 次：841 / 843 / 813 / 777 / 811 / 766 / 776 / 804 / 822 / 770ms，中位 **807.5ms**。
  - 未达到 800ms 的内部目标；原锁定口径 <1500ms 通过。
- 125 秒真实长跑（440 个进程，未选中详情）：
  - 专用工作集：min 60.9 / mean 64.8 / max **70.8MB**（<80MB）
  - 工作集 max 165.5MB；私有提交 max 105.6MB
  - GC：Gen0=2，Gen1=1，Gen2=0
  - 句柄：669–699
  - 托管堆：6.4–18.3MB
  - 统计分配：mean 0.10MB/s，max 1.75MB/s
  - `[enum]` 126 次：mean 0.6KB/次，max 71KB/次，min 0

这些数字是实际采样结果，不把“日志前的 0KB”当作整轮零分配。Gen2=0、125 秒内未破 80MB，目前按无泄漏处理。

## 架构一句话

```
NtQuerySystemInformation(class 5) → 零分配 unsafe 解析
        → 不可变快照 + 原子交换
        → 稳定 ProcKey（PID + 创建时间）
        → O(n) 树展平 + ListView 虚拟化
        → 懒加载命令行 / 路径 / 用户名
```

关键取舍：

- 不用 WPF TreeView：虚拟化不可用，改用扁平可见行。
- 排序即平铺：避免“按 CPU 排序后父子不相邻”的界面歧义。
- 命令行/详情只在需要时读，LRU 缓存；采集热路径不碰 OpenProcess。
- 树杀按点击时的快照执行，之后新出生的后代不在本轮计划内。

## 构建、测试、发布

```powershell
# 日常构建 + 自包含 R2R 发布到 publish\
.\scripts\build.ps1

# 自检 + diag + 启动测量
.\scripts\check.ps1 -StartupRuns 10

# 加上右键菜单 UI 自动化
.\scripts\check.ps1 -Ui -StartupRuns 3

# 干净发布包：dist\TaskMgrLite-win-x64\ + zip + SHA256
.\scripts\release.ps1
```

`release.ps1` 会清空 staging、重新 publish、跑 selftest、删除 PDB/XML/logs，复制 README 和 docs，再生成 zip 与 `.sha256`。

## 已知限制

- 发布包解压后约 137MB（自包含 WPF 运行时），zip 约 62MB；没有安装器，不是单文件，目的是保住冷启动。
- 二进制未签名，首次运行可能触发 Windows SmartScreen。
- Mica/圆角只在 Windows 11 生效；Windows 10 自动回退纯暗色。
- 受保护进程（PPL/系统进程）即使提权也可能无法读取路径或结束。
- 800ms 启动目标未达成，当前接受 807.5ms；不为了 7.5ms 引入复杂懒加载。
- 树杀只覆盖点击时快照；点击后新出生的后代需要下一轮手动处理。
