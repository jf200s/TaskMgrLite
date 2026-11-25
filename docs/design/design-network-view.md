# 独立网络端口视图设计与实施记录

目标：把端口从“底部详情面板手动查询单进程”改成左侧导航的独立全系统视图，
2Hz 刷新、支持搜索/排序、与 1Hz 进程采集完全隔离。

当前状态（2026-11-25）：
- 已完成 A–D：`PortReader.ReadAll()`、`PortSnapshot`、`NetworkViewModel`、
  `NetworkPortListSync`、左侧导航、视图切换、端口列表 XAML、旧端口面板清理。
- `PortQueryState`、`QueryPortsCommand`、`PortReader.Read(pid)` 已删除。
- 实测：Release 0 warning / 0 error；`--selftest` 72 passed / 0 failed；
  `--diag-round3/4` exit 0；UIA 多次采样 352–450 端口时均只有 27 个行容器，
  PID 升降序、进程名搜索（40/352）、切回进程后进程行数保持（504/504）。
- 截图：`artifacts/c-ui-process.png`、`artifacts/c-ui-network.png`。

---

## 1. 阶段划分

按模块提交，每个阶段构建 0 warning、`--selftest` 全绿后再提交。

| 阶段 | 内容 | 状态 |
|---|---|---|
| A | `PortModels` + `PortReader.ReadAll()` | 已完成，0/0 |
| B | `NetworkViewModel` + `NetworkPortListSync` + selftest | 已完成，72/0 |
| C | 左侧导航、视图切换、端口列表 XAML | 已完成，UIA 验证 |
| D | 删除底部端口面板、`PortQueryState`、`QueryPortsCommand`、`PortReader.Read(pid)` | 已完成 |

`Read(pid)` 只在 A/B 过渡期存在，C/D 已删除；最终只有 `ReadAll()` 一个入口。

---

## 2. PortReader 改造

### 2.1 公开 API

```csharp
public static PortQueryResult ReadAll();   // 全系统，端口视图使用
public static PortQueryResult Read(uint pid); // 临时兼容旧详情面板
```

两者都走：

```
ReadCore(filterPid)
  → TCP4 / TCP6 / UDP4 / UDP6 四次 ReadOne
  → 两段式 GetExtendedTcpTable / GetExtendedUdpTable
  → Parse：只按可选 PID 过滤，其余全部产出
  → 稳定排序：Protocol → LocalEndpoint → RemoteEndpoint → PID
```

稳定排序是为了让 Diff 看到的最小变化尽量小；顺序抖动会直接变成 Move。

### 2.2 数据模型

`PortEntry` 增加：

- `uint Pid`：四张表里的 owner PID；
- `ushort LocalPort` / `ushort RemotePort`：解析后的数值端口，便于搜索和排序；
- `PortKey Key`：`(Protocol, LocalEndpoint, RemoteEndpoint, Pid)`。

`State` 不在 Key 里。`LISTEN → ESTABLISHED` 只更新行属性，不触发集合变化。

`PortSnapshot` 是端口轮询的一帧不可变快照：`PortEntry[] + Status + Sequence + Timestamp`。
和进程快照不同，它不借池化缓冲，视图关闭后整帧直接交给 GC。

---

## 3. 独立刷新架构

```
NetworkViewModel.Activate()
  └─ PollLoopAsync(session, cts)                     [后台 async 循环]
       while (!ct.IsCancellationRequested)
         ├─ Task.Run(PortReader.ReadAll)             [线程池，串行]
         ├─ new PortSnapshot(...)                    [不可变帧]
         ├─ Dispatcher.InvokeAsync(ApplySnapshot)    [只投递一帧到 UI]
         └─ Task.Delay(剩余到 500ms, ct)
```

关键约束：

1. **不与进程采集共用任何东西**：不使用 `ProcessCollector` 的线程、快照槽位、
   `DispatcherTimer` 或 `ProcessSnapshot`。端口读取只发生在端口视图激活期间。
2. **单飞、不堆积**：循环体 `await` 完整一轮才进入下一轮；读取超过 500ms 时
   下一轮顺延，而不是排队补跑。
3. **不可变帧**：`PortReader.ReadAll()` 在后台线程完成，构造 `PortSnapshot`
   后不再修改 `Entries`。UI 线程只读。
4. **过期帧丢弃**：`session` 在 Deactivate 时递增；排队中的 UI 回调发现
   `session != _session` 或 `IsActive == false` 就不应用。`Sequence` 再兜底丢弃乱序帧。
5. **关闭即清理**：`Deactivate()` 取消 CTS、递增 session、清空 `Rows`、`_index`、
   `_desiredKeys` 和搜索状态；正在进行的原生读取会跑完，但结果不会回到 UI。

500ms 是默认值；如果后续实测全表解析在某台机器上超过预算，只需调整
`PollIntervalMs`，循环结构不变。

---

## 4. UI Diff

`NetworkPortListSync.Apply`：

1. 用 `PortKey` 集合删除已经消失的行；
2. 用 `Dictionary<PortKey, Row>` 建现有行索引；
3. 按期望顺序就位：
   - 同一位置同 Key：只 `Update(State, ProcessName)`，零集合事件；
   - Key 在后面：`ObservableCollection.Move`，一行动作；
   - 不存在：`Insert`；
4. 删除尾部残留。

事件保证：

- 两帧完全相同：不调用任何 `Insert/Remove/Move`，`CollectionChanged == 0`；
- 只有状态变化：零 `CollectionChanged`，只有 `PropertyChanged`；
- 增删：只对差异行发事件，不 `Reset`；
- 换位：`Move`，不 `Remove + Insert`，不 `Reset`。

自检覆盖：

- `network port diff is silent when unchanged`
- `network port diff updates state without collection event`
- `network port diff inserts and removes`
- `network port diff moves reordered rows`

已知边界：如果系统返回两条 Key 完全相同的行（例如 SO_REUSEADDR 下的重复 UDP
绑定），当前实现按同一行处理。需要区分时，Key 要加 occurrence 序号，这是下一轮
的事。

---

## 5. 进程名反查

端口表只有 PID，没有进程名。`NetworkViewModel` 构造时接收：

```csharp
Func<IReadOnlyDictionary<uint, string>> processNamesProvider
```

约束：该委托只在 UI 线程的 `ApplySnapshot` 里调用，返回“当前进程快照的
PID → Name”只读映射；查不到时行显示“未知”。

计划在 `MainViewModel` 中这样接：

```csharp
private Dictionary<uint, string>? _nameMap;
private long _nameMapSequence = -1;
private long _lastSnapshotSequence;

// ApplySnapshot 末尾：_lastSnapshotSequence = snapshot.Sequence;

private IReadOnlyDictionary<uint, string> GetProcessNameMap()
{
    if (_nameMap is not null && _nameMapSequence == _lastSnapshotSequence)
        return _nameMap;

    var map = new Dictionary<uint, string>(_rowsByKey.Count);
    foreach (KeyValuePair<ProcKey, ProcessRowViewModel> kv in _rowsByKey)
        map[kv.Key.Pid] = kv.Value.Name;

    _nameMap = map;
    _nameMapSequence = _lastSnapshotSequence;
    return map;
}
```

这样只有端口视图激活时才按进程快照序号重建一次映射，不改 1Hz 采集路径。
端口表本身只有 PID，PID 复用期间名字可能短暂对应到新进程；这是原生表的信息
上限，不在这一轮伪造创建时间。

---

## 6. 视图切换（阶段 C）

`MainViewModel` 增加：

```csharp
public enum MainSection { Processes, Network }

private MainSection _section = MainSection.Processes;
public MainSection Section => _section;
public bool IsProcessView => _section == MainSection.Processes;
public bool IsNetworkView => _section == MainSection.Network;

public NetworkViewModel Network { get; } // 构造时注入 Dispatcher 和 GetProcessNameMap

public void ShowProcesses()
{
    if (_section == MainSection.Processes) return;
    _section = MainSection.Processes;
    Network.Deactivate();
    NotifySectionChanged();
}

public void ShowNetwork()
{
    if (_section == MainSection.Network) return;
    _section = MainSection.Network;
    NotifySectionChanged();

    if (UseRealData)
        Network.Activate();
    else
        Network.Deactivate();   // 假数据模式不读真实端口，状态栏显示“假数据模式不支持端口”
}
```

`Dispose` 里调用 `Network.Dispose()`。

### XAML 骨架

把现有根 `DockPanel` 放进右侧，左侧加导航。进程视图保留原来的顶栏/底栏/详情面板；
网络视图有自己的顶栏和列表。

```xml
<Grid>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="172" />
        <ColumnDefinition Width="*" />
    </Grid.ColumnDefinitions>

    <!-- 左侧导航 -->
    <Border Grid.Column="0"
            Background="{StaticResource Surface}"
            BorderBrush="{StaticResource BorderBrush}"
            BorderThickness="0,0,1,0">
        <StackPanel Margin="8,16,8,8">
            <Button Content="进程"
                    Style="{StaticResource NavButton}"
                    Command="{Binding ShowProcessesCommand}" />
            <Button Content="网络端口"
                    Style="{StaticResource NavButton}"
                    Command="{Binding ShowNetworkCommand}" />
        </StackPanel>
    </Border>

    <!-- 进程视图：原来的 DockPanel 整体移到这里 -->
    <DockPanel Grid.Column="1"
               Visibility="{Binding IsProcessView, Converter={StaticResource BoolToVis}}">
        <!-- 原来的顶栏、底栏、详情面板、ProcessList -->
    </DockPanel>

    <!-- 网络端口视图 -->
    <DockPanel Grid.Column="1"
               Visibility="{Binding IsNetworkView, Converter={StaticResource BoolToVis}}">
        <Border DockPanel.Dock="Top" Padding="14,10"
                Background="{StaticResource Surface}"
                BorderBrush="{StaticResource BorderBrush}"
                BorderThickness="0,0,0,1">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="280" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <TextBox Grid.Column="0"
                         Text="{Binding Network.SearchText, UpdateSourceTrigger=PropertyChanged}"
                         ToolTip="按进程名、PID 或端口过滤" />
                <TextBlock Grid.Column="1"
                           Text="{Binding Network.CountText}"
                           Foreground="{StaticResource TextMuted}"
                           VerticalAlignment="Center"
                           Margin="16,0,0,0" />
                <TextBlock Grid.Column="2"
                           Text="{Binding Network.StatusText}"
                           Foreground="{StaticResource TextMuted}"
                           VerticalAlignment="Center" />
            </Grid>
        </Border>

        <ListView ItemsSource="{Binding Network.View}"
                  SelectionMode="Single"
                  AlternationCount="2">
            <ListView.View>
                <GridView>
                    <GridViewColumn Header="协议" Width="70"
                                    DisplayMemberBinding="{Binding Protocol}" />
                    <GridViewColumn Header="本地地址" Width="230"
                                    DisplayMemberBinding="{Binding LocalEndpoint}" />
                    <GridViewColumn Header="远程地址" Width="230"
                                    DisplayMemberBinding="{Binding RemoteEndpoint}" />
                    <GridViewColumn Header="状态" Width="120"
                                    DisplayMemberBinding="{Binding State}" />
                    <GridViewColumn Header="PID" Width="80"
                                    DisplayMemberBinding="{Binding PidDisplay}" />
                    <GridViewColumn Header="进程名" Width="220"
                                    DisplayMemberBinding="{Binding ProcessName}" />
                </GridView>
            </ListView.View>
        </ListView>
    </DockPanel>
</Grid>
```

列头排序用阶段 C 的路由事件，复用现有 `GridViewColumnHeader.Click` 模式；
端口列的排序属性是 `Protocol / LocalEndpoint / RemoteEndpoint / State / Pid / ProcessName`。
搜索走 `Network.SearchText` 的 150ms 防抖，过滤在 `ICollectionView` 上做，不重建集合。
`NavButton` 需要新增一个最小样式（选中态可用 `Tag` + DataTrigger，或两个 ToggleButton
绑定 `IsProcessView/IsNetworkView`），不引入新的控件库。

---

## 7. 风险与待实测

- **2Hz 全表分配未实测**。每帧会为端点和地址创建字符串；这是当前实现的主要成本。
  阶段 B 构建通过后需要跑 `--stats` 或独立长跑，记录分配速率和 UI 帧时间，不能凭估算。
- **读取变慢时刷新率自动下降**。这是单飞设计的预期行为；需要确认不会拖慢 UI。
- **端口表只有 PID**。PID 复用窗口内，进程名可能对应到新进程；不伪造创建时间。
- **重复 Key**。SO_REUSEADDR 的重复 UDP 绑定会被合并；需要时再加 occurrence。
- **旧 `Read(pid)` 会枚举全表再过滤**。只在阶段 D 之前存在，旧面板删除时一并移除。
- **权限**：四张表分别失败会汇总为 `Partial`；全部无结果且拒绝访问显示“无权限读取”。
  UDP 远程/状态继续显示“—”。
