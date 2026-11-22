using System.IO;
using System.Text;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

/// <summary>
/// 三通道日志：
///   1. 控制台（仅 --stats 时挂接，方便现场看）
///   2. %TEMP%\TaskMgrLite\run.log（测量脚本读这里）
///   3. &lt;程序目录&gt;\logs\run.log（双写一份，Claude 的沙箱只能读项目文件夹）
/// 3 打不开时（例如装在 Program Files）静默跳过，不影响 1 和 2。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;
    private static StreamWriter? _mirror;
    private static bool _console;

    public static string? FilePath { get; private set; }
    public static string? MirrorPath { get; private set; }
    public static bool ConsoleAttached => _console;

    public static void Initialize(bool attachConsole)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "TaskMgrLite");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "run.log");
            _file = TryOpen(FilePath);
        }
        catch
        {
            _file = null;
        }

        try
        {
            string localDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(localDir);
            MirrorPath = Path.Combine(localDir, "run.log");
            _mirror = TryOpen(MirrorPath);
        }
        catch
        {
            _mirror = null;
        }

        if (attachConsole)
        {
            // WinExe 默认没有控制台：优先挂到启动它的终端，失败则自己开一个
            if (!NativeMethods.AttachConsole(-1))
                NativeMethods.AllocConsole();

            _console = true;
        }
    }

    private static StreamWriter? TryOpen(string path)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            // 1Hz 采集日志不逐行刷盘；退出时 Flush，避免日志 I/O 污染采样。
            return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>进程退出/致命异常时调用；缓冲写入不保证崩溃时仍有完整日志。</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            try { _file?.Flush(); } catch { }
            try { _mirror?.Flush(); } catch { }
        }
    }

    public static void WriteLine(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";

        if (_console)
        {
            try { Console.WriteLine(line); } catch { /* 控制台句柄可能已失效 */ }
        }

        lock (Gate)
        {
            try { _file?.WriteLine(line); } catch { /* 磁盘满等场景不影响主流程 */ }
            try { _mirror?.WriteLine(line); } catch { }
        }

        System.Diagnostics.Debug.WriteLine(line);
    }
}
