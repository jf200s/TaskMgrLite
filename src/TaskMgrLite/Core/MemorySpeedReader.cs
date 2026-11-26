using System.Runtime.InteropServices;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

/// <summary>单条有效内存条速度汇总；0 表示没有可用数据。</summary>
internal readonly record struct MemorySpeedInfo(uint MtS, int ModuleCount, bool Valid);

/// <summary>
/// 初始化时读取一次 SMBIOS Type 17（Memory Device），取内存条速度。
///
/// GetSystemFirmwareTable('RSMB') 返回 RawSMBIOSData：
///   offset 0..3  Used20CallingMethod / Major / Minor / DmiRevision
///   offset 4..7  DWORD Length
///   offset 8..   结构表
///
/// 只读 Speed(0x15) / Configured Memory Speed(0x20)，单位 MT/s。
/// 多根内存条取最小值（系统通常按最慢一根运行），并返回有效条数。
/// 只调用一次，缓冲用完立即释放；不在 1Hz 路径上。
/// </summary>
internal static unsafe class MemorySpeedReader
{
    private const int RawHeaderSize = 8;

    public static MemorySpeedInfo Read()
    {
        uint size = MetricsNative.GetSystemFirmwareTable(
            MetricsNative.SmbiosProvider,
            0,
            IntPtr.Zero,
            0);

        if (size < (uint)RawHeaderSize || (long)size > int.MaxValue)
            return default;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint written = MetricsNative.GetSystemFirmwareTable(
                MetricsNative.SmbiosProvider,
                0,
                buffer,
                size);

            if (written < (uint)RawHeaderSize)
                return default;

            byte* raw = (byte*)buffer;
            uint tableLength = *(uint*)(raw + 4);
            if (tableLength == 0)
                return default;

            byte* table = raw + RawHeaderSize;
            long available = Math.Min((long)tableLength, (long)written - RawHeaderSize);
            byte* end = table + (int)available;

            uint minSpeed = 0;
            int moduleCount = 0;
            byte* current = table;

            while (current + 4 <= end)
            {
                byte type = current[0];
                byte length = current[1];

                if (length < 4 || current + length > end)
                    break;

                if (type == 17 && length >= 0x17)
                {
                    ushort moduleSize = ReadUInt16(current + 0x0C);

                    // Size 为 0 表示空插槽；0xFFFF 表示使用 Extended Size，仍是有效条。
                    if (moduleSize != 0)
                    {
                        uint speed = 0;

                        if (length >= 0x22)
                        {
                            ushort configured = ReadUInt16(current + 0x20);
                            if (configured != 0 && configured != 0xFFFF)
                                speed = configured;
                        }

                        if (speed == 0)
                        {
                            ushort rated = ReadUInt16(current + 0x15);
                            if (rated != 0 && rated != 0xFFFF)
                                speed = rated;
                        }

                        if (speed != 0)
                        {
                            if (minSpeed == 0 || speed < minSpeed)
                                minSpeed = speed;

                            moduleCount++;
                        }
                    }
                }

                // 跳过字符串集：连续 NUL 结尾，最后再跟一个 NUL。
                byte* next = current + length;
                while (next + 1 < end && (next[0] != 0 || next[1] != 0))
                    next++;

                if (next + 1 >= end)
                    break;

                current = next + 2;
            }

            return moduleCount > 0
                ? new MemorySpeedInfo(minSpeed, moduleCount, true)
                : default;
        }
        catch (OutOfMemoryException)
        {
            return default;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ushort ReadUInt16(byte* p) => (ushort)(p[0] | (p[1] << 8));
}
