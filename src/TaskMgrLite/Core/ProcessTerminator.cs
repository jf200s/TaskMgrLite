using System.Runtime.InteropServices;
using System.Text;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

public enum KillStatus
{
    Success,
    AccessDenied,
    AlreadyExited,
    ProcessReused,
    SystemProcess,
    SelfProcess,
    NotSupported,
    Failed
}

public readonly record struct KillOutcome(ProcKey Key, KillStatus Status, int Win32Error)
{
    public bool IsSuccess => Status == KillStatus.Success;
}

/// <summary>
/// 一次“结束进程 / 结束进程树”的逐个结果。
/// 不在这里弹 UI：调用方拿到报告后决定用 Toast 还是弹窗。
/// </summary>
public sealed class KillReport
{
    public KillReport(IReadOnlyList<KillOutcome> outcomes)
    {
        Outcomes = outcomes;

        int succeeded = 0;
        int failed = 0;
        bool accessDenied = false;

        for (int i = 0; i < outcomes.Count; i++)
        {
            if (outcomes[i].IsSuccess) succeeded++;
            else failed++;

            if (outcomes[i].Status == KillStatus.AccessDenied)
                accessDenied = true;
        }

        Succeeded = succeeded;
        Failed = failed;
        HasAccessDenied = accessDenied;
    }

    public IReadOnlyList<KillOutcome> Outcomes { get; }
    public int Attempted => Outcomes.Count;
    public int Succeeded { get; }
    public int Failed { get; }
    public bool HasAccessDenied { get; }
    public bool HasFailures => Failed > 0;

    public string BuildSummary(string actionLabel)
    {
        if (Attempted == 0)
            return $"{actionLabel}：没有可结束的进程";

        return Failed == 0
            ? $"{actionLabel}：成功 {Succeeded} 个"
            : $"{actionLabel}：成功 {Succeeded} 个，失败 {Failed} 个";
    }

    public string BuildMessage(string actionLabel)
    {
        if (Attempted == 0)
            return $"{actionLabel}：没有可结束的进程。";

        var text = new StringBuilder(256);
        text.Append(actionLabel).Append("：成功 ").Append(Succeeded)
            .Append(" 个，失败 ").Append(Failed).Append(" 个。");

        if (Failed == 0)
            return text.ToString();

        text.AppendLine();
        int shown = 0;

        for (int i = 0; i < Outcomes.Count; i++)
        {
            KillOutcome outcome = Outcomes[i];
            if (outcome.IsSuccess) continue;

            if (shown == 8)
            {
                text.Append("…另有 ").Append(Failed - shown).Append(" 个失败。");
                break;
            }

            text.Append("PID ").Append(outcome.Key.Pid).Append("：")
                .Append(Describe(outcome)).AppendLine();
            shown++;
        }

        return text.ToString();
    }

    public static string Describe(KillOutcome outcome) => outcome.Status switch
    {
        KillStatus.Success => "已结束",
        KillStatus.AccessDenied => "拒绝访问（权限不足）",
        KillStatus.AlreadyExited => "进程已退出",
        KillStatus.ProcessReused => "PID 已被复用，已跳过",
        KillStatus.SystemProcess => "系统进程，已跳过",
        KillStatus.SelfProcess => "不能结束本程序",
        KillStatus.NotSupported => "当前模式不支持，未执行",
        _ => $"失败（Win32 {outcome.Win32Error}）"
    };
}

/// <summary>
/// 结束进程。所有调用都先做三步身份校验：
///   1. OpenProcess 拿到句柄；
///   2. GetProcessTimes 的创建时间必须和快照里的 ProcKey 一致，防止 PID 复用误杀；
///   3. 句柄可等待且已 signaled 说明进程已经退出，不重复杀。
/// </summary>
public static class ProcessTerminator
{
    private const uint KillAccess =
        NativeMethods.PROCESS_TERMINATE |
        NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION |
        NativeMethods.SYNCHRONIZE;

    public static KillOutcome Kill(ProcKey key, uint selfPid)
    {
        // Idle / System 不能杀，也不该把“拒绝访问”甩给用户看
        if (key.Pid == 0 || key.Pid == 4)
            return new KillOutcome(key, KillStatus.SystemProcess, 0);

        if (key.Pid == selfPid)
            return new KillOutcome(key, KillStatus.SelfProcess, 0);

        IntPtr handle = NativeMethods.OpenProcess(KillAccess, false, key.Pid);
        if (handle == IntPtr.Zero)
            return FromLastError(key);

        try
        {
            if (!NativeMethods.GetProcessTimes(
                    handle,
                    out NativeMethods.FileTime creation,
                    out _,
                    out _,
                    out _))
            {
                return FromLastError(key);
            }

            // 快照里的 CreateTime 是 FILETIME 的 100ns 计数。
            // 0 表示快照没给，这时只按 PID 匹配，和树构建的退化规则一致。
            if (key.CreateTime != 0 && creation.ToLong() != key.CreateTime)
                return new KillOutcome(key, KillStatus.ProcessReused, 0);

            uint wait = NativeMethods.WaitForSingleObject(handle, 0);
            if (wait == NativeMethods.WAIT_OBJECT_0)
                return new KillOutcome(key, KillStatus.AlreadyExited, 0);

            if (!NativeMethods.TerminateProcess(handle, 1))
                return FromLastError(key);

            return new KillOutcome(key, KillStatus.Success, 0);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// 按给定顺序逐个结束。顺序由 TreeFlattener.TryCollectSubtreePostOrder 提供：
    /// 后代先于祖先，某一项失败不会中断后面的项。
    /// </summary>
    public static KillReport KillMany(IReadOnlyList<ProcKey> keys, uint selfPid)
    {
        if (keys.Count == 0)
            return new KillReport(Array.Empty<KillOutcome>());

        var outcomes = new List<KillOutcome>(keys.Count);
        for (int i = 0; i < keys.Count; i++)
            outcomes.Add(Kill(keys[i], selfPid));

        return new KillReport(outcomes);
    }

    public static KillStatus ClassifyWin32Error(int error) => error switch
    {
        NativeMethods.ERROR_ACCESS_DENIED => KillStatus.AccessDenied,
        NativeMethods.ERROR_INVALID_PARAMETER => KillStatus.AlreadyExited,
        NativeMethods.ERROR_NOT_FOUND => KillStatus.AlreadyExited,
        NativeMethods.ERROR_INVALID_HANDLE => KillStatus.AlreadyExited,
        _ => KillStatus.Failed
    };

    private static KillOutcome FromLastError(ProcKey key)
    {
        int error = Marshal.GetLastWin32Error();
        return new KillOutcome(key, ClassifyWin32Error(error), error);
    }
}
