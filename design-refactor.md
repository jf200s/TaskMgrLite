# Refactor / Bugfix：红队审查后

本轮只修 Bug，不新增功能。所有 P0/P1 修复均附自检用例，最后重新跑了启动与 125 秒真实长跑。

## P0-1 采集线程 Dispose 竞态

`ProcessCollector`：

- `_disposed` 改为 `volatile bool`；
- `Join(2000)` 超时后先判断 `_thread.IsAlive`；
- 新增 `SafeDispose(workerExited, dispose)`：worker 未退出时不释放 `_enumerator`、`_stop`、`_refresh`，宁可一次性泄漏，等进程退出回收；
- 超时路径写一条 `[collector] dispose timeout...` 日志。

测试：`collector dispose waits for worker` 模拟 `workerExited=false`，断言 dispose 回调完全不执行；再模拟 true，断言只执行一次。

## P0-2 CommandLineReader 悬空指针

`CommandLineReader.EnsureBuffer` 不再先 `FreeHGlobal` 再 `AllocHGlobal`。新增 `GrowBuffer`：

1. 先 `allocate(size)`；失败时旧 buffer/capacity 原封不动；
2. 分配成功后才替换 `buffer/capacity`；
3. 最后释放旧指针。

测试：`command line buffer grows safely` 注入抛 `OutOfMemoryException` 的 allocator，断言旧指针/容量不变且 release 未调用；成功路径断言 release 发生在 alloc 之后。

## P0-3 快照释放权双归属

新增 `SnapshotSlot` 作为唯一所有权槽位：

- `Publish`：覆盖旧快照并释放旧快照；
- `Take`：把所有权交给 UI，槽位清空；
- `Discard`：UI 明确不会接管时释放当前槽位。

`ProcessCollector` 的 `_latest` 改为 `_slot`，新增 `DiscardLatest()`。
`MainViewModel.OnSnapshotReady` 的 catch 不再 `snapshot.Release()`，改为 `_collector?.DiscardLatest()`，避免槽位留下指向已复用缓冲的悬空快照。

测试：`snapshot slot owns release` 覆盖 Publish 覆盖释放、Take 后 UI 释放、Release 幂等、Discard 只释放一次，以及第二次 Discard 为空操作。

## P1-4 ParseInto 越界读

`ProcessEnumerator`：

- 新增 `HeaderSize = 0x100`，读取任何字段前确认整个固定头在缓冲区内；
- `NextEntryOffset != 0 && < HeaderSize` 时防御性中止；
- `ImageName.Buffer/Length` 必须落在 `buffer + capacity` 内，否则清零，绝不把脏指针交给 `MaterializeName`；
- `ParseInto` 抽成 `internal static`，便于构造畸形缓冲测试。

测试：`parse into validates bounds` 覆盖正常条目、截断头、越界名称指针、非法 next offset。

## P1-5 批量可见行卡顿

`VisibleListSync.Apply` 增加批量阈值：

- `abs(desired.Count - target.Count) > 32` 时构建完整列表，调用 `replaceAll`；
- `BulkObservableCollection.ReplaceAll` 只发一次 Reset，不再逐条 Insert；
- 小变更仍走原增量同步。

测试：`bulk visible sync resets once` 从 5 行扩到 100 行，断言 `replaceAll` 只调用一次、`CollectionChanged Reset` 只触发一次、顺序正确。

## P1-6 假数据模式串号

`MainViewModel.SelectedRow` 在 `!ShouldLoadNative(UseRealData)` 时直接返回，不再请求真实命令/路径/用户；假数据行在加载时填入本地占位的 `ImagePath` / `UserName`。`GetDetailsAsync` 在假数据模式直接返回行内字段。

测试：`fake data never uses native lazy load` 断言假模式返回 false、真实模式返回 true，并验证假行命令/路径/用户保持本地值。

## P1-7 数据诚实度

- `Log` 文件写入改为 `AutoFlush=false`，退出时 `Log.Flush()`；致命异常处理器也立即 Flush。
- `ProcessCollector(logStats:)` 只在 `--stats` 下记录 `[enum]` 和采样分配。
- 分配采样点移到 `CollectOnce` 开始之前，日志输出移到 `ProcessSnapshot` 构造之后；覆盖枚举/解析/字典/名称和快照对象本身。日志字符串自身的固定小分配无法包含在自己的数字里，已在代码注释中说明。

## 验证（Windows 11 / .NET 11.0.100）

- 构建：0 warning / 0 error
- `--selftest`：**43 passed, 0 failed**
- `--diag-round3`：3 passed
- `--diag-round4`：3 passed
- 右键菜单 UIA：4/4 PASS

### 启动（10 次，真实数据）

```
841, 843, 813, 777, 811, 766, 776, 804, 822, 770 ms
中位数 = 807.5ms
```

**结论：未达到 800ms 目标（差 7.5ms）；原锁定口径 <1500ms 仍通过。** 不再把 883.6ms 写成“达标”。

### 125 秒真实进程长跑（440 个进程，未选中详情）

- 专用工作集：min 60.9 / mean 64.8 / max **70.8MB**（<80MB，余量约 9.2MB）
- 工作集 max 165.5MB；私有提交 max 105.6MB
- GC：Gen0=2，**Gen1=1**，Gen2=0
- 句柄：669–699
- 托管堆：6.4–18.3MB
- 分配速率：mean 0.10MB/s，max 1.75MB/s
- `[enum]` 采样 126 次：mean 0.6KB/次，max 71KB/次，min 0；尖峰来自新进程名称/状态增长，不是固定每帧分配

**结论：内存指标通过但余量比之前小；Gen1 不是 0；分配不是绝对 0。** 这些数字按实际采样点记录，不再用“日志前 0KB”代表整轮。

## 已知剩余

1. 125 秒内专用工作集从约 61MB 漂到 70.8MB，仍低于 80MB，但需要后续更长时间观察是否收敛。
2. 800ms 启动目标未达成；本轮不为了指标做未经验证的裁剪。
3. `Log` 缓冲写入在进程被强杀时可能丢失最后几行；正常关闭/致命异常会 Flush。

## 本轮改动文件

- `Core/ProcessCollector.cs`：Dispose 守卫、SnapshotSlot、stats-only 采样与分配口径。
- `Core/CommandLineReader.cs`：GrowBuffer 先分配后释放。
- `Core/SnapshotSlot.cs`：快照所有权。
- `Core/ProcessEnumerator.cs`：HeaderSize/名称指针边界。
- `Core/TreeModel.cs`：VisibleListSync 批量 Reset。
- `ViewModels/BulkObservableCollection.cs`：ReplaceAll。
- `ViewModels/MainViewModel.cs`：DiscardLatest、假数据防护、ReplaceAll 接入。
- `Core/Log.cs` / `App.xaml.cs`：AutoFlush=false + Flush。
- `Core/SelfTest.cs`：新增 6 个回归用例。
