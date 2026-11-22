namespace TaskMgrLite.Core;

/// <summary>
/// 一条进程采样（已解析、已算好 CPU）。值类型，避免每个进程一个堆对象。
/// 字段口径与任务管理器对齐：WorkingSetPrivate = 「内存（专用工作集）」。
/// </summary>
public readonly record struct ProcessSample(
    uint Pid,
    uint ParentPid,
    long CreateTime,
    long UserTime,
    long KernelTime,
    long WorkingSetPrivate,
    long WorkingSet,
    uint HandleCount,
    uint ThreadCount,
    double CpuPercent,
    string Name)
{
    /// <summary>PID 会被复用，行 identity 必须带创建时间。</summary>
    public string Key => $"{Pid}:{CreateTime}";

    public long CpuTime100ns => UserTime + KernelTime;
}

/// <summary>
/// 不可变快照。采集线程构造完整体才发布，UI 侧无需加锁。
///
/// Buffer 来自采集线程的缓冲池；UI 应用完必须调用 Release() 归还。
/// 未 Release 的快照不会被复用，池空时采集线程会新分配——宁可多分配一次，也不冒并发改写的风险。
/// </summary>
public sealed class ProcessSnapshot
{
    private Action<ProcessSample[]>? _returner;

    public ProcessSnapshot(
        ProcessSample[] buffer,
        int count,
        long sequence,
        long timestampUtcTicks,
        Action<ProcessSample[]>? returner)
    {
        Buffer = buffer;
        Count = count;
        Sequence = sequence;
        TimestampUtcTicks = timestampUtcTicks;
        _returner = returner;
    }

    public static ProcessSnapshot Empty { get; } =
        new(Array.Empty<ProcessSample>(), 0, 0, 0, returner: null);

    /// <summary>只读使用：长度可能大于 Count，有效区间是 [0, Count)。</summary>
    public ProcessSample[] Buffer { get; }

    public int Count { get; }
    public long Sequence { get; }
    public long TimestampUtcTicks { get; }

    /// <summary>把缓冲还给池。幂等。</summary>
    public void Release()
    {
        Action<ProcessSample[]>? returner = Interlocked.Exchange(ref _returner, null);
        returner?.Invoke(Buffer);
    }
}
