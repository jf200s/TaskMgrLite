using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

/// <summary>
/// 系统总网络吞吐。
///
/// 拓扑只在初始化 / 网卡变更时枚举一次：GetIfTable2 建表、复制 LUID 后立刻
/// FreeMibTable；稳态每个 tick 只对缓存的 LUID 调 GetIfEntry2，复用同一个
/// MIB_IF_ROW2 字段，不产生托管分配。
///
/// 过滤：回环（IF_TYPE_SOFTWARE_LOOPBACK）、隧道（IF_TYPE_TUNNEL）、
/// 过滤驱动（FilterInterface 标志）。VPN 虚拟网卡仍可能与物理网卡重复计数，
/// 这是“系统总计”口径的已知取舍；需要精确到单网卡时 UI 应改为逐适配器展示。
/// </summary>
internal sealed unsafe class NetworkThroughputReader : IDisposable
{
    private struct InterfaceState
    {
        public ulong Luid;
        public ulong LastIn;
        public ulong LastOut;
        public bool Stale;
    }

    private const int OffInOctets = 1208;
    private const int OffOutOctets = 1280;

    private List<InterfaceState> _interfaces = new(16);
    private List<InterfaceState> _scratch = new(16);
    private readonly MetricsNative.IpInterfaceChangeCallback _callback;

    // GetIfEntry2 用指针调用，避免 1352 字节结构体按 ref 封送时产生复制/分配。
    private IntPtr _row;
    private IntPtr _notifyHandle;
    private long _lastTimestamp;
    private bool _hasBaseline;
    private int _refreshRequested = 1;
    private bool _disposed;

    public NetworkThroughputReader()
    {
        _row = Marshal.AllocHGlobal(sizeof(MetricsNative.MibIfRow2));
        _callback = OnInterfaceChanged;

        // 只在网卡拓扑变化时重建表；稳态不轮询整表。
        if (MetricsNative.NotifyIpInterfaceChange(
                0,
                _callback,
                IntPtr.Zero,
                false,
                out IntPtr handle) == 0)
        {
            _notifyHandle = handle;
        }
    }

    /// <summary>
    /// 采样一次。首次调用建立基线并返回 false；之后按两次 Stopwatch 时间差
    /// 计算系统总上下行字节/秒。
    /// </summary>
    public bool TrySample(long timestamp, out double rxBytesPerSec, out double txBytesPerSec)
    {
        rxBytesPerSec = 0;
        txBytesPerSec = 0;

        if (Volatile.Read(ref _refreshRequested) != 0)
            Refresh();

        if (timestamp <= 0)
            return false;

        double seconds = _lastTimestamp == 0
            ? 0
            : (timestamp - _lastTimestamp) / (double)Stopwatch.Frequency;

        bool valid = _hasBaseline && seconds > 0.001;
        ulong inSum = 0;
        ulong outSum = 0;

        Span<InterfaceState> states = CollectionsMarshal.AsSpan(_interfaces);
        for (int i = 0; i < states.Length; i++)
        {
            ref InterfaceState state = ref states[i];
            Marshal.WriteInt64(_row, 0, unchecked((long)state.Luid));

            if (MetricsNative.GetIfEntry2(_row) != 0)
            {
                // 本轮读不到就把该网卡标脏；下一次成功只重置基线，不补算整段差值。
                state.Stale = true;
                continue;
            }

            ulong inOctets = unchecked((ulong)Marshal.ReadInt64(_row, OffInOctets));
            ulong outOctets = unchecked((ulong)Marshal.ReadInt64(_row, OffOutOctets));

            if (_hasBaseline
                && !state.Stale
                && inOctets >= state.LastIn
                && outOctets >= state.LastOut)
            {
                inSum += inOctets - state.LastIn;
                outSum += outOctets - state.LastOut;
            }

            state.LastIn = inOctets;
            state.LastOut = outOctets;
            state.Stale = false;
        }

        if (valid)
        {
            rxBytesPerSec = inSum / seconds;
            txBytesPerSec = outSum / seconds;
        }
        else
        {
            valid = false;
        }

        _lastTimestamp = timestamp;
        _hasBaseline = true;
        return valid;
    }

    private void Refresh()
    {
        Volatile.Write(ref _refreshRequested, 0);

        IntPtr table = IntPtr.Zero;
        uint error = MetricsNative.GetIfTable2(out table);

        if (error != 0)
        {
            if (table != IntPtr.Zero)
                MetricsNative.FreeMibTable(table);

            return;
        }

        if (table == IntPtr.Zero)
            return;

        try
        {
            _scratch.Clear();

            MetricsNative.MibIfTable2* header = (MetricsNative.MibIfTable2*)table;
            MetricsNative.MibIfRow2* rows = &header->Table;

            for (uint i = 0; i < header->NumEntries; i++)
            {
                ref MetricsNative.MibIfRow2 row = ref rows[i];
                if (!ShouldInclude(in row) || row.InterfaceLuid == 0)
                    continue;

                InterfaceState state = default;
                state.Luid = row.InterfaceLuid;

                if (TryFind(state.Luid, out InterfaceState previous))
                {
                    state.LastIn = previous.LastIn;
                    state.LastOut = previous.LastOut;
                    state.Stale = previous.Stale;
                }
                else
                {
                    // 新网卡以当前计数为基线，避免第一次采样出现整段累计值的尖峰。
                    state.LastIn = row.InOctets;
                    state.LastOut = row.OutOctets;
                }

                _scratch.Add(state);
            }

            List<InterfaceState> old = _interfaces;
            _interfaces = _scratch;
            _scratch = old;
            _scratch.Clear();
        }
        finally
        {
            MetricsNative.FreeMibTable(table);
        }
    }

    private bool TryFind(ulong luid, out InterfaceState state)
    {
        for (int i = 0; i < _interfaces.Count; i++)
        {
            if (_interfaces[i].Luid == luid)
            {
                state = _interfaces[i];
                return true;
            }
        }

        state = default;
        return false;
    }

    private static bool ShouldInclude(in MetricsNative.MibIfRow2 row)
    {
        if (row.Type == MetricsNative.IfTypeSoftwareLoopback
            || row.Type == MetricsNative.IfTypeTunnel)
        {
            return false;
        }

        return (row.InterfaceAndOperStatusFlags & MetricsNative.FilterInterfaceFlag) == 0;
    }

    private void OnInterfaceChanged(IntPtr callerContext, IntPtr row, uint notificationType)
        => Volatile.Write(ref _refreshRequested, 1);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_notifyHandle != IntPtr.Zero)
        {
            MetricsNative.CancelMibChangeNotify2(_notifyHandle);
            _notifyHandle = IntPtr.Zero;
        }

        _interfaces.Clear();
        _scratch.Clear();

        if (_row != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_row);
            _row = IntPtr.Zero;
        }
    }
}
