using System.Runtime.InteropServices;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

/// <summary>
/// 一条原始条目。NamePointer 指向枚举器内部缓冲，只在下一次 SampleRaw 之前有效。
/// 采集线程按 PID+CreateTime 复用已缓存的字符串，所以正常情况下不需要 materialize。
/// </summary>
public readonly record struct RawProcessEntry(
    uint Pid,
    uint ParentPid,
    long CreateTime,
    long UserTime,
    long KernelTime,
    long WorkingSetPrivate,
    long WorkingSet,
    uint HandleCount,
    uint ThreadCount,
    nint NamePointer,
    int NameByteLength)
{
    public long CpuTime100ns => UserTime + KernelTime;
}

/// <summary>
/// 单次 NtQuerySystemInformation(SystemProcessInformation) 采样。
///
/// 1. 缓冲区跨调用复用，只在 STATUS_INFO_LENGTH_MISMATCH 时增长；
/// 2. 解析用 unsafe 按偏移直读，不 Marshal.PtrToStructure，不做结构体拷贝；
/// 3. 结果写进复用的 RawProcessEntry[]，本方法不分配任何托管对象；
/// 4. 增长/查询失败路径都不会丢缓冲。
/// </summary>
public sealed unsafe class ProcessEnumerator : IDisposable
{
    private const int InitialCapacity = 1 << 20;   // 1 MiB
    private const int MaxCapacity = 64 << 20;      // 64 MiB
    private const int MaxGrowAttempts = 16;

    // ---- SYSTEM_PROCESS_INFORMATION (x64 / ARM64) ----
    private const int OffNextEntryOffset = 0x00;
    private const int OffNumberOfThreads = 0x04;
    private const int OffWorkingSetPrivate = 0x08;
    private const int OffCreateTime = 0x20;
    private const int OffUserTime = 0x28;
    private const int OffKernelTime = 0x30;
    private const int OffImageNameLength = 0x38;
    private const int OffImageNameBuffer = 0x40;
    private const int OffUniqueProcessId = 0x50;
    private const int OffInheritedFromPid = 0x58;
    private const int OffHandleCount = 0x60;
    private const int OffWorkingSetSize = 0x90;

    // x64 / ARM64 SYSTEM_PROCESS_INFORMATION 固定头到最后一个计数字段。
    // 任何要读取的偏移都必须落在 [p, p + HeaderSize) 内。
    internal const int HeaderSize = 0x100;

    private IntPtr _buffer;
    private int _capacity;
    private RawProcessEntry[] _entries = new RawProcessEntry[1024];
    private bool _disposed;

    public ProcessEnumerator(int initialCapacity = InitialCapacity)
    {
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("只实现了 64 位结构布局（x64 / ARM64）。");

        _capacity = Math.Max(initialCapacity, 4096);
        _buffer = Marshal.AllocHGlobal(_capacity);
    }

    public uint LastNtStatus { get; private set; }
    public int LastReturnLength { get; private set; }
    public int Capacity => _capacity;

    /// <summary>
    /// 采样一次。entries 是内部复用数组，只在下一次调用前有效。
    /// </summary>
    public bool SampleRaw(out RawProcessEntry[] entries, out int count, out uint status)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        status = QueryWithRetry();
        LastNtStatus = status;

        if (status != NativeMethods.STATUS_SUCCESS)
        {
            entries = _entries;
            count = 0;
            return false;
        }

        count = ParseInto(ref _entries);
        entries = _entries;
        return true;
    }

    private uint QueryWithRetry()
    {
        uint status = 0;

        for (int attempt = 0; attempt < MaxGrowAttempts; attempt++)
        {
            status = NativeMethods.NtQuerySystemInformation(
                NativeMethods.SystemProcessInformation,
                _buffer,
                _capacity,
                out int returnLength);

            LastReturnLength = returnLength;

            if (status == NativeMethods.STATUS_SUCCESS)
                return status;

            bool canGrow =
                status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH ||
                status == NativeMethods.STATUS_BUFFER_TOO_SMALL ||
                status == NativeMethods.STATUS_BUFFER_OVERFLOW;

            if (!canGrow)
                return status;

            int target = Math.Max(returnLength, _capacity * 2);
            if (target <= _capacity) target = _capacity * 2;
            if (target > MaxCapacity) return status;

            // 先分配后释放：新分配失败时旧缓冲区仍然有效
            IntPtr grown = Marshal.AllocHGlobal(target);
            IntPtr old = _buffer;
            _buffer = grown;
            _capacity = target;
            Marshal.FreeHGlobal(old);
        }

        return status;
    }

    private int ParseInto(ref RawProcessEntry[] entries) =>
        ParseInto(_buffer, _capacity, ref entries);

    /// <summary>
    /// 全项目唯一的 unsafe 解析入口。任何字段读取前必须确认：
    ///   - 当前 entry 的完整 HeaderSize 在缓冲区内；
    ///   - NextEntryOffset 要么是 0，要么不小于 HeaderSize；
    ///   - ImageName.Buffer 和 ImageName.Length 落在缓冲区内。
    /// internal + 显式 buffer/capacity 便于 selftest 构造畸形缓冲。
    /// </summary>
    internal static unsafe int ParseInto(
        IntPtr buffer,
        int capacity,
        ref RawProcessEntry[] entries)
    {
        if (buffer == IntPtr.Zero || capacity < HeaderSize)
            return 0;

        int count = 0;
        byte* start = (byte*)buffer;
        byte* end = start + capacity;
        byte* p = start;
        int guard = 0;

        while (true)
        {
            if (++guard > 100_000) break;

            // 整个固定头必须可读，再做任何偏移读取。
            if (p < start || end - p < HeaderSize) break;

            if (count == entries.Length)
                Array.Resize(ref entries, entries.Length * 2);

            uint nextOffset = *(uint*)(p + OffNextEntryOffset);
            int nameByteLength = *(ushort*)(p + OffImageNameLength);
            nint namePointer = *(nint*)(p + OffImageNameBuffer);

            if (namePointer != 0 && nameByteLength > 0)
            {
                long offset = namePointer - (nint)start;
                if (offset < 0 || offset > capacity || nameByteLength > capacity - offset)
                {
                    // 脏数据/结构漂移：不要把这个指针交给后续 MaterializeName。
                    namePointer = 0;
                    nameByteLength = 0;
                }
            }

            entries[count++] = new RawProcessEntry(
                Pid: (uint)(*(nint*)(p + OffUniqueProcessId)),
                ParentPid: (uint)(*(nint*)(p + OffInheritedFromPid)),
                CreateTime: *(long*)(p + OffCreateTime),
                UserTime: *(long*)(p + OffUserTime),
                KernelTime: *(long*)(p + OffKernelTime),
                WorkingSetPrivate: *(long*)(p + OffWorkingSetPrivate),
                WorkingSet: *(long*)(p + OffWorkingSetSize),
                HandleCount: *(uint*)(p + OffHandleCount),
                ThreadCount: *(uint*)(p + OffNumberOfThreads),
                NamePointer: namePointer,
                NameByteLength: nameByteLength);

            if (nextOffset == 0) break;
            if (nextOffset < HeaderSize) break;   // 指向自己/固定头内部，防御性中止
            p += nextOffset;
        }

        return count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
        _capacity = 0;
    }
}
