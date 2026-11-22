using System.Runtime.InteropServices;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

public enum CommandLineStatus
{
    Ok,
    AccessDenied,
    Exited,
    ProcessReused,
    Unsupported,
    Error
}

public readonly record struct CommandLineResult(string? Text, CommandLineStatus Status)
{
    public bool IsOk => Status == CommandLineStatus.Ok;

    public string DisplayText => Status switch
    {
        CommandLineStatus.Ok => Text ?? string.Empty,
        CommandLineStatus.AccessDenied => "（拒绝访问）",
        CommandLineStatus.Exited => "（进程已退出）",
        CommandLineStatus.ProcessReused => "（PID 已复用）",
        CommandLineStatus.Unsupported => "（系统不支持）",
        _ => "（读取失败）"
    };
}

/// <summary>
/// 命令行懒加载的单次读取器。
///
/// 只做一件事：OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE)
/// → 校验创建时间 → NtQueryInformationProcess(class 60) → 解析 UNICODE_STRING。
///
/// 缓冲区跨调用复用；只有命令字符串本身会 new string。
/// 这个类不是线程安全的，由 CommandLineService 的单线程 worker 持有。
/// </summary>
public sealed unsafe class CommandLineReader : IDisposable
{
    private const int UnicodeStringSize = 16;   // ushort Length/MaxLength + 4 字节填充 + PWSTR

    private IntPtr _buffer;
    private int _capacity;
    private bool _disposed;

    public CommandLineResult Read(ProcKey key)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (key.Pid == 0)
            return new CommandLineResult(null, CommandLineStatus.AccessDenied);

        IntPtr handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.SYNCHRONIZE,
            false,
            key.Pid);

        if (handle == IntPtr.Zero)
            return FromLastError();

        try
        {
            if (!NativeMethods.GetProcessTimes(handle, out NativeMethods.FileTime creation, out _, out _, out _))
                return FromLastError();

            if (key.CreateTime != 0 && creation.ToLong() != key.CreateTime)
                return new CommandLineResult(null, CommandLineStatus.ProcessReused);

            if (NativeMethods.WaitForSingleObject(handle, 0) == NativeMethods.WAIT_OBJECT_0)
                return new CommandLineResult(null, CommandLineStatus.Exited);

            // 第一次查询只拿长度
            uint status = NativeMethods.NtQueryInformationProcess(
                handle,
                NativeMethods.ProcessCommandLineInformation,
                IntPtr.Zero,
                0,
                out int required);

            if (status == NativeMethods.STATUS_SUCCESS)
                return new CommandLineResult(string.Empty, CommandLineStatus.Ok);

            if (!IsLengthMismatch(status))
                return new CommandLineResult(null, MapNtStatus(status));

            if (required < UnicodeStringSize)
                required = UnicodeStringSize;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                EnsureBuffer(required);

                status = NativeMethods.NtQueryInformationProcess(
                    handle,
                    NativeMethods.ProcessCommandLineInformation,
                    _buffer,
                    _capacity,
                    out required);

                if (status == NativeMethods.STATUS_SUCCESS)
                {
                    // 用本次实际返回长度做边界，而不是缓冲区容量；
                    // 否则可能把缓冲区尾部未初始化的内容当成字符串。
                    int actual = required > 0 ? required : _capacity;
                    string? text = ParseUnicodeString(_buffer, actual);
                    return new CommandLineResult(text, CommandLineStatus.Ok);
                }

                if (!IsLengthMismatch(status))
                    return new CommandLineResult(null, MapNtStatus(status));

                if (required <= _capacity && required < UnicodeStringSize)
                    break;
            }

            return new CommandLineResult(null, CommandLineStatus.Error);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// 解析 PROCESS_COMMAND_LINE_INFORMATION 里的 UNICODE_STRING。
    ///
    /// x64 / ARM64 布局：
    ///   +0  USHORT Length（字节数）
    ///   +2  USHORT MaximumLength
    ///   +8  PWSTR  Buffer（指向同一块返回缓冲区内部）
    /// 这个方法是 internal 的，SelfTest 会构造一块假缓冲区验证布局。
    /// </summary>
    internal static string? ParseUnicodeString(IntPtr buffer, int bufferLength)
    {
        if (buffer == IntPtr.Zero || bufferLength < UnicodeStringSize)
            return null;

        byte* start = (byte*)buffer;
        ushort byteLength = *(ushort*)start;
        nint textPointer = *(nint*)(start + 8);

        if (byteLength == 0)
            return string.Empty;

        if (textPointer == 0)
            return null;

        // 防御脏数据：UNICODE_STRING.Length 是字节数，必须落在缓冲区范围内。
        long offset = textPointer - (nint)buffer;
        if (offset < 0 || offset + byteLength > bufferLength)
            return null;

        return new string((char*)textPointer, 0, byteLength / 2);
    }

    private static bool IsLengthMismatch(uint status) =>
        status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH ||
        status == NativeMethods.STATUS_BUFFER_TOO_SMALL ||
        status == NativeMethods.STATUS_BUFFER_OVERFLOW;

    private static CommandLineStatus MapNtStatus(uint status) => status switch
    {
        NativeMethods.STATUS_ACCESS_DENIED => CommandLineStatus.AccessDenied,
        _ => CommandLineStatus.Error
    };

    private CommandLineResult FromLastError()
    {
        int error = Marshal.GetLastWin32Error();

        return error switch
        {
            NativeMethods.ERROR_ACCESS_DENIED => new CommandLineResult(null, CommandLineStatus.AccessDenied),
            NativeMethods.ERROR_INVALID_PARAMETER => new CommandLineResult(null, CommandLineStatus.Exited),
            NativeMethods.ERROR_NOT_FOUND => new CommandLineResult(null, CommandLineStatus.Exited),
            NativeMethods.ERROR_INVALID_HANDLE => new CommandLineResult(null, CommandLineStatus.Exited),
            _ => new CommandLineResult(null, CommandLineStatus.Error)
        };
    }

    private void EnsureBuffer(int required)
    {
        if (_capacity >= required && _buffer != IntPtr.Zero)
            return;

        GrowBuffer(
            ref _buffer,
            ref _capacity,
            required,
            size => Marshal.AllocHGlobal(size),
            pointer => Marshal.FreeHGlobal(pointer));
    }

    /// <summary>
    /// 先分配成功，再替换指针并释放旧缓冲。
    /// Alloc 抛 OOM 时 buffer/capacity 保持原值，绝不能先 Free 再 Alloc 留下悬空指针。
    /// internal 便于 selftest 注入会抛异常的 allocator。
    /// </summary>
    internal static void GrowBuffer(
        ref IntPtr buffer,
        ref int capacity,
        int required,
        Func<int, IntPtr> allocate,
        Action<IntPtr> release)
    {
        int size = Math.Max(required, 4096);

        IntPtr grown = allocate(size);   // throws -> old buffer untouched

        IntPtr old = buffer;
        buffer = grown;
        capacity = size;

        if (old != IntPtr.Zero)
            release(old);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _capacity = 0;
        }
    }
}
