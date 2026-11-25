namespace TaskMgrLite.Core;

/// <summary>
/// 端口轮询的一帧不可变快照。
///
/// 生产者在独立后台线程构造，数组只读；消费者（NetworkViewModel）在 UI 线程
/// 按 Sequence 丢弃过期帧。与进程快照不同，端口快照不借用池化缓冲，
/// 因为端口视图关闭后整帧可以被 GC 直接回收。
/// </summary>
public sealed class PortSnapshot
{
    public PortSnapshot(
        PortEntry[] entries,
        PortQueryStatus status,
        long sequence,
        long timestampUtcTicks)
    {
        Entries = entries;
        Status = status;
        Sequence = sequence;
        TimestampUtcTicks = timestampUtcTicks;
    }

    public static PortSnapshot Empty { get; } =
        new(Array.Empty<PortEntry>(), PortQueryStatus.NoData, 0, 0);

    /// <summary>只读使用。生产后不再修改。</summary>
    public PortEntry[] Entries { get; }

    public int Count => Entries.Length;
    public PortQueryStatus Status { get; }
    public long Sequence { get; }
    public long TimestampUtcTicks { get; }
}
