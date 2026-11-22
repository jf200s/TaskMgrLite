using System.Diagnostics;

namespace TaskMgrLite.Core;

/// <summary>
/// 专用采集线程，1Hz。
///
/// 每轮：
///   1. 复用缓冲区枚举（零托管分配，除新进程的进程名）
///   2. 用两次采样差值算 CPU%（占 CPU 总容量，与任务管理器同口径）
///   3. 构造不可变快照，缓冲来自池，原子发布
///
/// UI 侧通过 TakeLatest 取走并 Release 归还。UI 忙时只保留最新一份，不排队。
/// </summary>
public sealed class ProcessCollector : IDisposable
{
    private struct ProcState
    {
        public long CreateTime;
        public long CpuTime100ns;
        public string Name;
        public int LastSeenTick;
    }

    private const int TickMs = 1000;
    private const int PruneEveryTicks = 120;   // 两分钟清一次已退出进程的缓存

    private readonly ProcessEnumerator _enumerator = new();
    private readonly Dictionary<uint, ProcState> _state = new(1024);
    private readonly Stack<ProcessSample[]> _pool = new();
    private readonly List<uint> _pruneKeys = new(512);
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly AutoResetEvent _refresh = new(false);
    private readonly WaitHandle[] _wakeHandles;
    private readonly Thread _thread;
    private readonly int _cores = Environment.ProcessorCount;
    private readonly bool _logStats;

    private readonly SnapshotSlot _slot = new();
    private long _sequence;
    private long _lastTimestamp;
    private bool _hasLastTimestamp;
    private int _tick;
    private volatile bool _disposed;

    public ProcessCollector(bool logStats = false)
    {
        _logStats = logStats;
        _wakeHandles = new WaitHandle[] { _stop.WaitHandle, _refresh };

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "TaskMgrLite.Collector",
            Priority = ThreadPriority.BelowNormal
        };
    }

    /// <summary>在采集线程上触发，只允许做「唤醒 UI」这种轻量动作。</summary>
    public event Action<ProcessSnapshot>? SnapshotReady;

    public void Start() => _thread.Start();

    /// <summary>UI 右键“刷新”调用：唤醒采集线程立即采一次，不排队、不叠加。</summary>
    public void RequestRefresh()
    {
        if (!_disposed) _refresh.Set();
    }

    public ProcessSnapshot? TakeLatest() => _slot.Take();

    /// <summary>UI 无法接管快照时由调用方丢弃；槽位负责唯一一次 Release。</summary>
    public void DiscardLatest() => _slot.Discard();

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        long nextDue = 0;

        bool manualRefresh = false;

        while (!_stop.IsSet)
        {
            // 空白处右键刷新：有信号就立刻采一次，采完重新对齐 1Hz 节拍。
            if (_refresh.WaitOne(0))
            {
                manualRefresh = true;
                nextDue = 0;
            }

            long now = clock.ElapsedMilliseconds;

            if (now < nextDue)
            {
                int wait = (int)Math.Min(nextDue - now, 500);
                if (wait > 0)
                {
                    int signaled = WaitHandle.WaitAny(_wakeHandles, wait);
                    if (signaled == 0) break;        // stop
                    if (signaled == 1)               // refresh
                    {
                        manualRefresh = true;
                        nextDue = 0;
                    }
                }
                continue;
            }

            ProcessSnapshot snapshot;
            try
            {
                snapshot = CollectOnce();
            }
            catch (Exception ex)
            {
                Log.WriteLine("[collector] FAILED " + ex);
                nextDue = clock.ElapsedMilliseconds + TickMs;
                manualRefresh = false;
                continue;
            }

            // 覆盖式发布：旧的还没被 UI 取走就由槽位归还缓冲
            _slot.Publish(snapshot);

            try { SnapshotReady?.Invoke(snapshot); }
            catch { /* UI 正在退出 */ }

            if (manualRefresh)
            {
                nextDue = clock.ElapsedMilliseconds + TickMs;
                manualRefresh = false;
            }
            else
            {
                nextDue += TickMs;
                if (now - nextDue > 2000) nextDue = now + TickMs;   // 长时间卡住后不补跑
            }
        }
    }

    private ProcessSnapshot CollectOnce()
    {
        // 采样点必须在枚举/解析/快照对象构造之前。不开启 --stats 时完全不测。
        long allocBefore = _logStats ? GC.GetAllocatedBytesForCurrentThread() : 0;

        long timestamp = Stopwatch.GetTimestamp();
        double wallSeconds = _hasLastTimestamp
            ? (timestamp - _lastTimestamp) / (double)Stopwatch.Frequency
            : 0;
        _lastTimestamp = timestamp;
        _hasLastTimestamp = true;

        if (!_enumerator.SampleRaw(out RawProcessEntry[] raw, out int count, out uint status))
        {
            if (_logStats)
                Log.WriteLine($"[enum] ntstatus=0x{status:X8} 采样失败");

            return ProcessSnapshot.Empty;
        }

        int tick = ++_tick;
        ProcessSample[] buffer = Rent(count);
        int written = 0;

        for (int i = 0; i < count; i++)
        {
            RawProcessEntry e = raw[i];

            bool known = _state.TryGetValue(e.Pid, out ProcState state)
                         && state.CreateTime == e.CreateTime;

            string name = known ? state.Name : MaterializeName(in e);

            double cpu = -1;
            if (known && wallSeconds > 0.001)
            {
                long delta = e.CpuTime100ns - state.CpuTime100ns;
                if (delta < 0) delta = 0;
                cpu = delta / 1e7 / (wallSeconds * _cores) * 100.0;
            }

            _state[e.Pid] = new ProcState
            {
                CreateTime = e.CreateTime,
                CpuTime100ns = e.CpuTime100ns,
                Name = name,
                LastSeenTick = tick
            };

            buffer[written++] = new ProcessSample(
                e.Pid,
                e.ParentPid,
                e.CreateTime,
                e.UserTime,
                e.KernelTime,
                e.WorkingSetPrivate,
                e.WorkingSet,
                e.HandleCount,
                e.ThreadCount,
                cpu,
                name);
        }

        if (tick % PruneEveryTicks == 0) Prune(tick);

        var snapshot = new ProcessSnapshot(
            buffer, written, ++_sequence, DateTime.UtcNow.Ticks, ReturnBuffer);

        if (_logStats)
        {
            // 覆盖枚举、解析、字典/名称分配和 ProcessSnapshot 本身。
            // 这一行日志字符串自身的分配无法包含在自己的数字里，属于固定小噪声。
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Log.WriteLine(
                $"[enum] ntstatus=0x{status:X8} entries={count} buffer={_enumerator.Capacity / 1024}KB " +
                $"alloc={allocated / 1024}KB");
        }

        return snapshot;
    }

    private static unsafe string MaterializeName(in RawProcessEntry e)
    {
        if (e.NameByteLength > 1 && e.NamePointer != 0)
            return new string((char*)e.NamePointer, 0, e.NameByteLength / 2);
        return string.Empty;
    }

    private void Prune(int tick)
    {
        _pruneKeys.Clear();

        foreach (KeyValuePair<uint, ProcState> kv in _state)
        {
            if (kv.Value.LastSeenTick < tick - 1)
                _pruneKeys.Add(kv.Key);
        }

        for (int i = 0; i < _pruneKeys.Count; i++)
            _state.Remove(_pruneKeys[i]);
    }

    private ProcessSample[] Rent(int min)
    {
        min = Math.Max(min, 256);

        lock (_pool)
        {
            while (_pool.Count > 0)
            {
                ProcessSample[] buffer = _pool.Pop();
                if (buffer.Length >= min) return buffer;
            }
        }

        return new ProcessSample[min + 256];
    }

    private void ReturnBuffer(ProcessSample[] buffer)
    {
        lock (_pool)
        {
            if (_pool.Count < 8) _pool.Push(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stop.Set();
        if (_thread.IsAlive) _thread.Join(2000);

        // Join 超时说明采集线程仍可能在使用 _enumerator 的非托管缓冲和
        // WaitHandle。此时释放会直接 UAF/AV；宁可一次性泄漏，等进程退出回收。
        bool workerExited = !_thread.IsAlive;
        bool freed = SafeDispose(workerExited, () =>
        {
            _stop.Dispose();
            _refresh.Dispose();
            _enumerator.Dispose();
        });

        if (!freed)
        {
            Log.WriteLine("[collector] dispose timeout; leaving one-shot native/event resources to process exit");
        }
    }

    /// <summary>
    /// 只有确认 worker 已退出才执行非托管/WaitHandle 释放。
    /// 抽成 internal 便于自检模拟 Join 超时。
    /// </summary>
    internal static bool SafeDispose(bool workerExited, Action dispose)
    {
        if (!workerExited) return false;
        dispose();
        return true;
    }
}
