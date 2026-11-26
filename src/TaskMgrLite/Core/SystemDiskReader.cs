using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

/// <summary>
/// 全系统磁盘读写吞吐，来自 NtQuerySystemInformation(SystemPerformanceInformation)。
///
/// 读取的是 I/O 管理器累计的 IoReadTransferCount / IoWriteTransferCount，
/// 两次采样差值除以墙钟时间。它是“系统总 I/O”口径，包含所有设备，
/// 不是单块物理磁盘的磁头吞吐；默认权限下这是最轻的可用来源。
///
/// 缓冲在构造时分配一次，之后跨 tick 复用；首次采样只建立基线。
/// </summary>
internal sealed class SystemDiskReader : IDisposable
{
    private const int BufferSize = 4096;
    private const int ReadOffset = 0x08;
    private const int WriteOffset = 0x10;

    private IntPtr _buffer;
    private bool _hasBaseline;
    private long _lastTimestamp;
    private ulong _prevRead;
    private ulong _prevWrite;
    private bool _disposed;

    public SystemDiskReader()
    {
        _buffer = Marshal.AllocHGlobal(BufferSize);
    }

    /// <summary>
    /// 采样一次。首次调用返回 false；之后的速率按“上次成功采样”到现在的
    /// 实际墙钟秒数归一化，单次 NtQuery 失败不会把下一次速率放大。
    /// </summary>
    public bool TrySample(long timestamp, out double readBytesPerSec, out double writeBytesPerSec)
    {
        readBytesPerSec = 0;
        writeBytesPerSec = 0;

        if (_disposed || _buffer == IntPtr.Zero)
            return false;

        uint status = MetricsNative.NtQuerySystemInformation(
            MetricsNative.SystemPerformanceInformation,
            _buffer,
            BufferSize,
            out _);

        if (status != MetricsNative.StatusSuccess)
            return false;

        long readRaw = Marshal.ReadInt64(_buffer, ReadOffset);
        long writeRaw = Marshal.ReadInt64(_buffer, WriteOffset);

        ulong read = readRaw > 0 ? (ulong)readRaw : 0;
        ulong write = writeRaw > 0 ? (ulong)writeRaw : 0;

        double seconds = _lastTimestamp == 0
            ? 0
            : (timestamp - _lastTimestamp) / (double)Stopwatch.Frequency;

        bool valid = _hasBaseline
                     && seconds > 0.001
                     && read >= _prevRead
                     && write >= _prevWrite;

        if (valid)
        {
            readBytesPerSec = (read - _prevRead) / seconds;
            writeBytesPerSec = (write - _prevWrite) / seconds;
        }
        else
        {
            valid = false;
        }

        _prevRead = read;
        _prevWrite = write;
        _lastTimestamp = timestamp;
        _hasBaseline = true;
        return valid;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}
