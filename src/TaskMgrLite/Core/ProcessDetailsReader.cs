using System.Runtime.InteropServices;
using System.Text;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

public enum ProcessInfoStatus
{
    Ok,
    AccessDenied,
    Exited,
    ProcessReused,
    Error
}

public readonly record struct ProcessDetailsResult(
    string? ImagePath,
    ProcessInfoStatus PathStatus,
    string? UserName,
    ProcessInfoStatus UserStatus)
{
    public bool HasAccessDenied =>
        PathStatus == ProcessInfoStatus.AccessDenied ||
        UserStatus == ProcessInfoStatus.AccessDenied;

    public string PathDisplay => ImagePath ?? Describe(PathStatus, "路径");
    public string UserDisplay => UserName ?? Describe(UserStatus, "用户");

    public static string Describe(ProcessInfoStatus status, string field) => status switch
    {
        ProcessInfoStatus.Ok => field == "路径" ? "（无路径）" : "（未知用户）",
        ProcessInfoStatus.AccessDenied => "（拒绝访问）",
        ProcessInfoStatus.Exited => "（进程已退出）",
        ProcessInfoStatus.ProcessReused => "（PID 已复用）",
        _ => "（读取失败）"
    };
}

/// <summary>
/// 详情面板的懒加载读取器：进程全路径 + 用户名。
/// 无状态，可由详情 worker 或用户动作的 Task.Run 并发调用；每次调用独立分配，
/// 不进入 1Hz 热路径。
/// </summary>
public static class ProcessDetailsReader
{
    private const uint Access =
        NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.SYNCHRONIZE;

    public static ProcessDetailsResult Read(ProcKey key)
    {
        IntPtr handle = NativeMethods.OpenProcess(Access, false, key.Pid);
        if (handle == IntPtr.Zero)
        {
            ProcessInfoStatus status = ClassifyWin32Error(Marshal.GetLastWin32Error());
            return new ProcessDetailsResult(null, status, null, status);
        }

        try
        {
            if (!NativeMethods.GetProcessTimes(handle, out NativeMethods.FileTime creation, out _, out _, out _))
            {
                ProcessInfoStatus status = ClassifyWin32Error(Marshal.GetLastWin32Error());
                return new ProcessDetailsResult(null, status, null, status);
            }

            if (key.CreateTime != 0 && creation.ToLong() != key.CreateTime)
            {
                return new ProcessDetailsResult(
                    null, ProcessInfoStatus.ProcessReused,
                    null, ProcessInfoStatus.ProcessReused);
            }

            if (NativeMethods.WaitForSingleObject(handle, 0) == NativeMethods.WAIT_OBJECT_0)
            {
                return new ProcessDetailsResult(
                    null, ProcessInfoStatus.Exited,
                    null, ProcessInfoStatus.Exited);
            }

            ProcessInfoStatus pathStatus = ReadImagePath(handle, out string? path);
            ProcessInfoStatus userStatus = ReadUserName(handle, out string? user);

            return new ProcessDetailsResult(path, pathStatus, user, userStatus);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public static ProcessInfoStatus ClassifyWin32Error(int error) => error switch
    {
        NativeMethods.ERROR_ACCESS_DENIED => ProcessInfoStatus.AccessDenied,
        NativeMethods.ERROR_INVALID_PARAMETER => ProcessInfoStatus.Exited,
        NativeMethods.ERROR_NOT_FOUND => ProcessInfoStatus.Exited,
        NativeMethods.ERROR_INVALID_HANDLE => ProcessInfoStatus.Exited,
        _ => ProcessInfoStatus.Error
    };

    private static ProcessInfoStatus ReadImagePath(IntPtr process, out string? path)
    {
        path = null;
        int capacity = 1024;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new StringBuilder(capacity);
            int size = capacity;

            if (NativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size))
            {
                path = buffer.ToString();
                return ProcessInfoStatus.Ok;
            }

            int error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ERROR_INSUFFICIENT_BUFFER || capacity >= 32768)
                return ClassifyWin32Error(error);

            capacity *= 2;
        }

        return ProcessInfoStatus.Error;
    }

    private static ProcessInfoStatus ReadUserName(IntPtr process, out string? userName)
    {
        userName = null;

        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out IntPtr token))
            return ClassifyWin32Error(Marshal.GetLastWin32Error());

        try
        {
            NativeMethods.GetTokenInformation(
                token, NativeMethods.TokenUser, IntPtr.Zero, 0, out int length);

            if (length <= 0)
                return ClassifyWin32Error(Marshal.GetLastWin32Error());

            IntPtr info = Marshal.AllocHGlobal(length);

            try
            {
                if (!NativeMethods.GetTokenInformation(
                        token, NativeMethods.TokenUser, info, length, out _))
                {
                    return ClassifyWin32Error(Marshal.GetLastWin32Error());
                }

                IntPtr sid = Marshal.ReadIntPtr(info, 0);
                if (sid == IntPtr.Zero)
                    return ProcessInfoStatus.Error;

                return LookupAccount(sid, out userName);
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    private static ProcessInfoStatus LookupAccount(IntPtr sid, out string? userName)
    {
        userName = null;
        int nameCapacity = 256;
        int domainCapacity = 128;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var name = new StringBuilder(nameCapacity);
            var domain = new StringBuilder(domainCapacity);
            int nameLength = nameCapacity;
            int domainLength = domainCapacity;

            if (NativeMethods.LookupAccountSidW(
                    null, sid, name, ref nameLength, domain, ref domainLength, out _))
            {
                string account = name.ToString();
                string domainName = domain.ToString();

                userName = domainName.Length > 0
                    ? domainName + "\\" + account
                    : account;

                return ProcessInfoStatus.Ok;
            }

            int error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ERROR_INSUFFICIENT_BUFFER)
                return ClassifyWin32Error(error);

            if (nameLength > nameCapacity) nameCapacity = nameLength;
            else nameCapacity *= 2;

            if (domainLength > domainCapacity) domainCapacity = domainLength;
            else domainCapacity *= 2;
        }

        return ProcessInfoStatus.Error;
    }
}
