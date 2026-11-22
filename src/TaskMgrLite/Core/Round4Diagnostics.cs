using System.Diagnostics;
using System.IO;

namespace TaskMgrLite.Core;

/// <summary>
/// --diag-round4：验证详情面板的真实内核路径，不创建窗口。
///   1. 读自己的全路径（QueryFullProcessImageName）；
///   2. 读自己的用户名（进程 token → SID → LookupAccountSid）；
///   3. 验证“打开文件所在位置”对空路径/不存在路径会失败，不会真的弹 Explorer。
/// </summary>
public static class Round4Diagnostics
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        _passed = 0;
        _failed = 0;

        Check("read own image path and user", ReadOwnDetails);
        Check("explorer path validation", ExplorerValidation);
        Check("manual refresh wakes collector", ManualRefreshWakesCollector);

        Log.WriteLine($"[diag-r4] {_passed} passed, {_failed} failed");
        return _failed;
    }

    private static void ReadOwnDetails()
    {
        var key = new ProcKey((uint)Environment.ProcessId, 0);
        ProcessDetailsResult result = ProcessDetailsReader.Read(key);

        if (result.PathStatus != ProcessInfoStatus.Ok ||
            string.IsNullOrEmpty(result.ImagePath) ||
            !File.Exists(result.ImagePath))
        {
            throw new Exception($"path status={result.PathStatus}, path={result.ImagePath}");
        }

        if (!result.ImagePath!.EndsWith("TaskMgrLite.exe", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"unexpected path: {result.ImagePath}");

        if (result.UserStatus != ProcessInfoStatus.Ok || string.IsNullOrEmpty(result.UserName))
            throw new Exception($"user status={result.UserStatus}, user={result.UserName}");

        Log.WriteLine($"[diag-r4] self path={result.ImagePath}");
        Log.WriteLine($"[diag-r4] self user={result.UserName}");
    }

    private static void ExplorerValidation()
    {
        if (ExplorerLauncher.TryOpen(null, out _))
            throw new Exception("null path should not open");

        if (ExplorerLauncher.TryOpen(@"C:\__taskmgr_no_such_file__.exe", out _))
            throw new Exception("missing file should not open");
    }

    private static void ManualRefreshWakesCollector()
    {
        using var collector = new ProcessCollector();
        using var first = new ManualResetEventSlim(false);
        using var second = new ManualResetEventSlim(false);
        int seen = 0;

        collector.SnapshotReady += _ =>
        {
            int n = Interlocked.Increment(ref seen);
            if (n == 1) first.Set();
            else if (n == 2) second.Set();
        };

        collector.Start();

        if (!first.Wait(3000))
            throw new Exception("first snapshot timeout");

        var clock = Stopwatch.StartNew();
        collector.RequestRefresh();

        if (!second.Wait(1500))
            throw new Exception("refresh snapshot timeout");

        clock.Stop();
        if (clock.ElapsedMilliseconds > 900)
            throw new Exception($"refresh took {clock.ElapsedMilliseconds}ms");

        Log.WriteLine($"[diag-r4] manual refresh latency={clock.ElapsedMilliseconds}ms");
        collector.TakeLatest()?.Release();
    }

    private static void Check(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Log.WriteLine($"[diag-r4] PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Log.WriteLine($"[diag-r4] FAIL  {name}: {ex.Message}");
        }
    }
}
