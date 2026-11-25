# 网络端口查询（按需）设计与核心代码

> 已废止：该设计只服务旧详情面板的“单进程手动查询”。0.1.02 之后端口已改为
> 左侧导航的独立全系统视图，`PortQueryState`、`QueryPortsCommand` 和 `Read(pid)`
> 均已删除。当前实现见 [`../design-network-view.md`](../design-network-view.md)。

## 1. 边界与状态流

只允许用户点击“查询端口”时读取网络表；不在 `ApplySnapshot`、`RebuildFromCurrentNodes` 或 `SelectedRow` 的自动路径里调用 `GetExtendedTcpTable` / `GetExtendedUdpTable`。

```
选中行变化
  → PortState.Reset(newKey)
  → Version++，取消 CTS，清空 Entries / Status / Cache

点击“查询端口”
  → Version = PortState.Begin()（IsBusy=true，清空旧数据）
  → Task.Run(PortReader.Read(pid))
  → 回到 UI：PortState.Apply(key, version, result)
  → version/key 不匹配 → 直接丢弃（不写 UI、不写缓存）
```

关键点：`Reset` 和 `Apply` 共用同一个 `Version`。切换选中行时 `Reset` 让所有在途结果失效，因此不会把进程 A 的端口显示到进程 B。

## 2. `Core/PortReader.cs`

### 2.1 数据模型

```csharp
namespace TaskMgrLite.Core;

public enum PortQueryStatus
{
    Ok,
    NoData,
    Partial,
    AccessDenied,
    Error
}

public readonly record struct PortEntry(
    string Protocol,       // TCP4 / TCP6 / UDP4 / UDP6
    string LocalEndpoint,  // 0.0.0.0:443 或 [::]:443
    string RemoteEndpoint, // “—” for UDP
    string State);         // LISTEN / ESTABLISHED / ... / “—”

public sealed record PortQueryResult(
    PortEntry[] Entries,
    PortQueryStatus Status,
    string Message)
{
    public string DisplayText => Status switch
    {
        PortQueryStatus.Ok => $"已加载 {Entries.Length} 个端口",
        PortQueryStatus.NoData => "未发现端口",
        PortQueryStatus.Partial => $"部分网络表读取失败，已显示 {Entries.Length} 个端口",
        PortQueryStatus.AccessDenied => "无权限读取",
        _ => "读取失败"
    };

    public static PortQueryResult Error(string message) =>
        new(Array.Empty<PortEntry>(), PortQueryStatus.Error, message);
}
```

### 2.2 P/Invoke 与常量

```csharp
using System.Net;
using System.Runtime.InteropServices;

namespace TaskMgrLite.Core;

public static class PortReader
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;

    private const uint NoError = 0;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorInsufficientBuffer = 122;

    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;

    // MIB_TCPROW_OWNER_PID / MIB_TCP6ROW_OWNER_PID
    private const int Tcp4RowSize = 24;
    private const int Tcp6RowSize = 56;
    // MIB_UDPROW_OWNER_PID / MIB_UDP6ROW_OWNER_PID
    private const int Udp4RowSize = 12;
    private const int Udp6RowSize = 28;

    private const int MaxTableBytes = 64 * 1024 * 1024;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);
```

### 2.3 两段式读取与安全遍历

```csharp
    public static PortQueryResult Read(uint pid)
    {
        var entries = new List<PortEntry>(8);
        bool denied = false;
        bool error = false;

        ReadOne(tcp: true, AfInet, pid, entries, ref denied, ref error);
        ReadOne(tcp: true, AfInet6, pid, entries, ref denied, ref error);
        ReadOne(tcp: false, AfInet, pid, entries, ref denied, ref error);
        ReadOne(tcp: false, AfInet6, pid, entries, ref denied, ref error);

        entries.Sort(static (a, b) =>
        {
            int byProtocol = string.CompareOrdinal(a.Protocol, b.Protocol);
            return byProtocol != 0
                ? byProtocol
                : string.CompareOrdinal(a.LocalEndpoint, b.LocalEndpoint);
        });

        PortEntry[] result = entries.ToArray();
        PortQueryStatus status = result.Length > 0
            ? (denied || error ? PortQueryStatus.Partial : PortQueryStatus.Ok)
            : denied
                ? PortQueryStatus.AccessDenied
                : error
                    ? PortQueryStatus.Error
                    : PortQueryStatus.NoData;

        return new PortQueryResult(result, status, status.ToString());
    }

    private static void ReadOne(
        bool tcp,
        int family,
        uint pid,
        List<PortEntry> output,
        ref bool denied,
        ref bool error)
    {
        try
        {
            TableResult table = ReadTable(tcp, family, pid);
            if (table.Entries.Length > 0)
                output.AddRange(table.Entries);

            if (table.Status == PortQueryStatus.AccessDenied)
                denied = true;
            else if (table.Status == PortQueryStatus.Error)
                error = true;
        }
        catch
        {
            // 网络表属于不可信非托管数据；单张表失败不影响其它表。
            error = true;
        }
    }

    private static TableResult ReadTable(bool tcp, int family, uint pid)
    {
        int size = 0;
        uint code = Call(tcp, IntPtr.Zero, ref size, family);

        if (code != NoError && code != ErrorInsufficientBuffer)
            return FromError(code);

        if (size <= 0)
            return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.NoData);

        if (size > MaxTableBytes)
            return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.Error);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int actual = size;
                code = Call(tcp, buffer, ref actual, family);

                if (code == ErrorInsufficientBuffer)
                {
                    // 表在两次调用之间增长：释放后按新大小重试。
                    size = actual;
                    if (size <= 0 || size > MaxTableBytes)
                        return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.Error);
                    continue;
                }

                if (code != NoError)
                    return FromError(code);

                return Parse(buffer, actual, tcp, family, pid);
            }
            finally
            {
                // 所有路径都必须释放，包括解析抛异常。
                Marshal.FreeHGlobal(buffer);
            }
        }

        return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.Error);
    }

    private static uint Call(bool tcp, IntPtr table, ref int size, int family)
    {
        return tcp
            ? GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidAll, 0)
            : GetExtendedUdpTable(table, ref size, false, family, UdpTableOwnerPid, 0);
    }

    private static TableResult FromError(uint code)
    {
        PortQueryStatus status = code == ErrorAccessDenied
            ? PortQueryStatus.AccessDenied
            : PortQueryStatus.Error;
        return new TableResult(Array.Empty<PortEntry>(), status);
    }

    private static TableResult Parse(IntPtr buffer, int size, bool tcp, int family, uint pid)
    {
        if (size < 4)
            return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.Error);

        uint count = (uint)Marshal.ReadInt32(buffer, 0);
        bool ipv6 = family == AfInet6;
        int rowSize = tcp
            ? (ipv6 ? Tcp6RowSize : Tcp4RowSize)
            : (ipv6 ? Udp6RowSize : Udp4RowSize);

        int maxRows = (size - 4) / rowSize;
        if (count > (uint)maxRows)
            return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.Error);

        var entries = new List<PortEntry>(8);

        for (uint i = 0; i < count; i++)
        {
            int offset = 4 + (int)i * rowSize;
            if (offset < 4 || offset + rowSize > size)
                return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.Error);

            uint ownerPid = tcp
                ? (uint)Marshal.ReadInt32(buffer, offset + (ipv6 ? 52 : 20))
                : (uint)Marshal.ReadInt32(buffer, offset + (ipv6 ? 24 : 8));

            if (ownerPid != pid)
                continue;

            entries.Add(tcp
                ? ReadTcpRow(buffer, offset, ipv6)
                : ReadUdpRow(buffer, offset, ipv6));
        }

        return new TableResult(
            entries.ToArray(),
            entries.Count > 0 ? PortQueryStatus.Ok : PortQueryStatus.NoData);
    }
```

### 2.4 行解析、端口字节序与 TCP 状态

```csharp
    private static PortEntry ReadTcpRow(IntPtr buffer, int offset, bool ipv6)
    {
        if (!ipv6)
        {
            uint state = (uint)Marshal.ReadInt32(buffer, offset);
            string local = FormatV4(buffer, offset + 4);
            ushort localPort = ReadPort(buffer, offset + 8);
            string remote = FormatV4(buffer, offset + 12);
            ushort remotePort = ReadPort(buffer, offset + 16);

            return new PortEntry(
                "TCP4",
                FormatEndpoint(local, localPort, ipv6: false),
                FormatEndpoint(remote, remotePort, ipv6: false),
                TcpState(state));
        }

        uint localScope = (uint)Marshal.ReadInt32(buffer, offset + 16);
        string local6 = FormatV6(buffer, offset, localScope);
        ushort localPort6 = ReadPort(buffer, offset + 20);
        uint remoteScope = (uint)Marshal.ReadInt32(buffer, offset + 40);
        string remote6 = FormatV6(buffer, offset + 24, remoteScope);
        ushort remotePort6 = ReadPort(buffer, offset + 44);
        uint state6 = (uint)Marshal.ReadInt32(buffer, offset + 48);

        return new PortEntry(
            "TCP6",
            FormatEndpoint(local6, localPort6, ipv6: true),
            FormatEndpoint(remote6, remotePort6, ipv6: true),
            TcpState(state6));
    }

    private static PortEntry ReadUdpRow(IntPtr buffer, int offset, bool ipv6)
    {
        if (!ipv6)
        {
            string local = FormatV4(buffer, offset);
            ushort localPort = ReadPort(buffer, offset + 4);
            return new PortEntry(
                "UDP4",
                FormatEndpoint(local, localPort, ipv6: false),
                "—",
                "—");
        }

        uint scope = (uint)Marshal.ReadInt32(buffer, offset + 16);
        string local6 = FormatV6(buffer, offset, scope);
        ushort localPort6 = ReadPort(buffer, offset + 20);
        return new PortEntry(
            "UDP6",
            FormatEndpoint(local6, localPort6, ipv6: true),
            "—",
            "—");
    }

    private static ushort ReadPort(IntPtr buffer, int offset)
    {
        // dwLocalPort / dwRemotePort 的低 16 位是网络字节序。
        uint raw = (uint)Marshal.ReadInt32(buffer, offset);
        return (ushort)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
    }

    private static string FormatV4(IntPtr buffer, int offset)
    {
        byte[] bytes = new byte[4];
        Marshal.Copy(IntPtr.Add(buffer, offset), bytes, 0, 4);
        return new IPAddress(bytes).ToString();
    }

    private static string FormatV6(IntPtr buffer, int offset, uint scopeId)
    {
        byte[] bytes = new byte[16];
        Marshal.Copy(IntPtr.Add(buffer, offset), bytes, 0, 16);
        return new IPAddress(bytes, scopeId).ToString();
    }

    private static string FormatEndpoint(string address, ushort port, bool ipv6) =>
        ipv6 ? $"[{address}]:{port}" : $"{address}:{port}";

    private static string TcpState(uint state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTEN",
        3 => "SYN_SENT",
        4 => "SYN_RCVD",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT1",
        7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => state.ToString()
    };

    private readonly record struct TableResult(PortEntry[] Entries, PortQueryStatus Status);
}
```

## 3. `ViewModels/PortQueryState.cs`

纯状态机，不碰 WPF 窗口；`ObservableCollection` 只承载当前选中行的结果。

```csharp
using System.Collections.ObjectModel;
using TaskMgrLite.Core;

namespace TaskMgrLite.ViewModels;

public sealed class PortQueryState : ObservableObject
{
    private string _statusText = "未加载";
    private bool _isBusy;

    public ObservableCollection<PortEntry> Entries { get; } = new();

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(QueryButtonText));
        }
    }

    public string QueryButtonText => IsBusy ? "查询中..." : "查询端口";

    public ProcKey? CurrentKey { get; private set; }
    public PortQueryResult? CachedResult { get; private set; }
    public int Version { get; private set; }

    /// <summary>切换选中行 / 清空详情时调用；所有在途结果立即失效。</summary>
    public void Reset(ProcKey? key)
    {
        Version++;
        CurrentKey = key;
        IsBusy = false;
        StatusText = "未加载";
        CachedResult = null;
        Entries.Clear();
    }

    public int Begin()
    {
        Version++;
        IsBusy = true;
        StatusText = "查询中…";
        CachedResult = null;
        Entries.Clear();
        return Version;
    }

    /// <summary>只有当前 selection 的当前版本才能落结果；旧结果直接丢弃。</summary>
    public bool Apply(ProcKey key, int version, PortQueryResult result)
    {
        if (version != Version || CurrentKey != key)
            return false;

        CachedResult = result;
        Entries.Clear();
        foreach (PortEntry entry in result.Entries)
            Entries.Add(entry);

        StatusText = result.DisplayText;
        IsBusy = false;
        return true;
    }
}
```

## 4. `MainViewModel` 接线

```csharp
using System.Windows.Input;

// 字段
private readonly PortQueryState _portState = new();
private CancellationTokenSource? _portCts;

public PortQueryState PortState => _portState;
public RelayCommand QueryPortsCommand { get; }

public bool CanQueryPorts =>
    !_disposed && UseRealData && _selectedRow is not null && !_portState.IsBusy;
```

构造函数里：

```csharp
QueryPortsCommand = new RelayCommand(
    () => _ = QueryPortsAsync(),
    () => CanQueryPorts);

_portState.PropertyChanged += (_, _) =>
    QueryPortsCommand.RaiseCanExecuteChanged();
```

`SelectedRow` setter 的关键一行：

```csharp
set
{
    if (!Set(ref _selectedRow, value)) return;

    // 切换行：取消查询、清空状态和缓存，并使在途结果失效。
    ResetPortState(value?.KeyStruct);

    OnPropertyChanged(nameof(HasSelection));
    // ...原有详情/命令行懒加载逻辑...
}
```

查询与重置：

```csharp
private void ResetPortState(ProcKey? key)
{
    _portCts?.Cancel();
    _portCts = null;

    _portState.Reset(key);
    QueryPortsCommand.RaiseCanExecuteChanged();
}

private async Task QueryPortsAsync()
{
    if (!CanQueryPorts || _selectedRow is not ProcessRowViewModel row)
        return;

    ProcKey key = row.KeyStruct;

    _portCts?.Cancel();
    var cts = new CancellationTokenSource();
    _portCts = cts;

    int version = _portState.Begin();

    try
    {
        PortQueryResult result = await Task.Run(
            () => PortReader.Read(key.Pid), cts.Token);

        // 用户在查询期间切了行：Apply 会因为 Version/CurrentKey 不匹配而丢弃。
        _portState.Apply(key, version, result);
    }
    catch (OperationCanceledException)
    {
        // 用户切行或发起新一轮查询，状态已由 Reset/Begin 处理。
    }
    catch (Exception ex)
    {
        Log.WriteLine("[ports] " + ex);
        _portState.Apply(key, version, PortQueryResult.Error(ex.Message));
    }
    finally
    {
        if (ReferenceEquals(_portCts, cts))
            _portCts = null;
        cts.Dispose();
    }
}
```

`Dispose` 里追加：

```csharp
_portCts?.Cancel();
_portCts = null;
_portState.Reset(null);
```

`RelayCommand` 只需极简实现：

```csharp
using System.Windows.Input;

namespace TaskMgrLite.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
```

## 5. 详情面板 XAML

在详情面板的 `Grid` 上增加第二行；网络区域跨两列。

```xml
<Grid>
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />
        <RowDefinition Height="Auto" />
    </Grid.RowDefinitions>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="280" />
        <ColumnDefinition Width="*" />
    </Grid.ColumnDefinitions>

    <!-- 原有左侧信息：Grid.Row=0, Grid.Column=0 -->
    <!-- 原有右侧路径/命令行：Grid.Row=0, Grid.Column=1 -->

    <Border Grid.Row="1" Grid.ColumnSpan="2"
            Margin="0,8,0,0" Padding="0,8,0,0"
            BorderBrush="{StaticResource BorderBrush}"
            BorderThickness="0,1,0,0">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>

            <StackPanel Orientation="Horizontal">
                <TextBlock Text="网络端口"
                           Foreground="{StaticResource TextMuted}"
                           VerticalAlignment="Center" />

                <Button Command="{Binding QueryPortsCommand}"
                        Margin="10,0,0,0">
                    <Button.Style>
                        <Style TargetType="Button"
                               BasedOn="{StaticResource ToolbarButton}">
                            <Setter Property="Content" Value="查询端口" />
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding PortState.IsBusy}" Value="True">
                                    <Setter Property="Content" Value="查询中..." />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Button.Style>
                </Button>

                <TextBlock Text="{Binding PortState.StatusText}"
                           Foreground="{StaticResource TextMuted}"
                           Margin="10,0,0,0"
                           VerticalAlignment="Center" />
            </StackPanel>

            <ListView Grid.Row="1"
                      ItemsSource="{Binding PortState.Entries}"
                      MaxHeight="120"
                      Margin="0,6,0,0"
                      BorderThickness="0"
                      Background="Transparent"
                      Foreground="{StaticResource TextPrimary}"
                      ScrollViewer.HorizontalScrollBarVisibility="Disabled">
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
                    </GridView>
                </ListView.View>
            </ListView>
        </Grid>
    </Border>
</Grid>
```

按钮的 `CanExecute` 由 `CanQueryPorts` 控制：选中行存在、真实数据、且不在查询中。查询中 `IsBusy=true` → `QueryPortsCommand` 立即禁用，同时 `DataTrigger` 把文案切成“查询中...”。

## 6. 单元测试

```csharp
private static void PortStateClearsOnSelectionChange()
{
    var state = new PortQueryState();
    ProcKey a = new(1, 11);
    ProcKey b = new(2, 22);

    state.Reset(a);
    int version = state.Begin();
    True(state.IsBusy, "query is busy");

    var result = new PortQueryResult(
        new[]
        {
            new PortEntry("TCP4", "0.0.0.0:443", "—", "LISTEN")
        },
        PortQueryStatus.Ok,
        "ok");

    True(state.Apply(a, version, result), "current result applied");
    Equal(1, state.Entries.Count, "one port");
    True(state.CachedResult is not null, "cache populated");
    EqualText("已加载 1 个端口", state.StatusText, "loaded status");

    // 切换选中行：状态、列表、缓存全部清空，在途结果失效。
    state.Reset(b);
    Equal(0, state.Entries.Count, "entries cleared");
    EqualText("未加载", state.StatusText, "status reset");
    True(state.CachedResult is null, "cache cleared");
    True(!state.IsBusy, "not busy");

    True(!state.Apply(a, version, result), "stale result rejected");
    Equal(0, state.Entries.Count, "stale result does not repopulate");
}
```

在 `SelfTest.Run()` 里注册：

```csharp
Check("port state clears on selection change", PortStateClearsOnSelectionChange);
```

## 7. 分配与性能

- `PortReader.Read` **只**从 `QueryPortsAsync` 调用，绝不出现在 `ApplySnapshot` / `RebuildFromCurrentNodes` / 1Hz 路径。
- 每次查询最多四次两段式调用；每次 `AllocHGlobal` 都在 `finally` 中 `FreeHGlobal`。
- 只把 `OwningPid == pid` 的行加入 `List<PortEntry>`，不为全表建托管对象。
- `PortQueryState.Entries` 只在按钮查询/切换选中行时 `Clear/Add`，不参与每秒刷新。
- `SelectedRow` 切换时的 `Reset` 只清空一个通常为空的 `ObservableCollection`，不会产生持续分配。
- 网络表读取占用的是后台线程；UI 只接收最终结果，不阻塞 1Hz 采集。

## 8. 实施记录（2026-11-22）

| 提交 | 内容 |
|---|---|
| `b49f3ab` | PortReader / PortModels：两段式 P/Invoke、边界校验、TCP/UDP、IPv4/IPv6 |
| `dd7446c` | PortQueryState、RelayCommand、MainViewModel 查询命令与切行清理 |
| `5ab5f5b` | 详情面板网络端口区域 |

验证：

- `--selftest`：69 passed, 0 failed（新增 `port state clears on selection change`）。
- UIA：QQLive 查询到 9 / 80 个端口，TCP4/TCP6/UDP4 行均能展示；切到另一个进程后状态回到“未加载”、列表清空。
- 查询期间 `IsBusy` 禁用按钮；`AccessDenied` 映射为“无权限读取”，不会弹全局异常。
- 网络读取未进入 1Hz 采集路径，不影响 0.1MB/s 分配基线。
