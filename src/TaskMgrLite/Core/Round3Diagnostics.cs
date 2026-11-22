using System.Diagnostics;

namespace TaskMgrLite.Core;

/// <summary>
/// --diag-round3：只验证 Round 3 的两个内核路径，不创建窗口。
///   1. 读自己的命令行（NtQueryInformationProcess class 60）；
///   2. 起一个受控 cmd.exe，先验证 PID 复用保护，再真正结束它。
/// 这个模式不会碰用户进程，失败也只影响子进程。
/// </summary>
public static class Round3Diagnostics
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        _passed = 0;
        _failed = 0;

        Check("read own command line", ReadOwnCommandLine);
        Check("read child command line", RunControlledKill);
        Check("kill controlled process tree", RunControlledTreeKill);

        Log.WriteLine($"[diag] {_passed} passed, {_failed} failed");
        return _failed;
    }

    private static void ReadOwnCommandLine()
    {
        using var reader = new CommandLineReader();
        CommandLineResult result = reader.Read(new ProcKey((uint)Environment.ProcessId, 0));

        if (!result.IsOk || result.Text is null)
            throw new Exception($"status={result.Status}");

        if (result.Text.IndexOf("--diag-round3", StringComparison.OrdinalIgnoreCase) < 0)
            throw new Exception($"command line missing flag: {result.Text}");
    }

    private static void RunControlledKill()
    {
        uint selfPid = (uint)Environment.ProcessId;

        var start = new ProcessStartInfo
        {
            // 直接起 ping.exe，不经过 cmd：单进程，杀掉后不会留下孙进程孤儿。
            FileName = "ping.exe",
            Arguments = "127.0.0.1 -n 30",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process child = Process.Start(start)
            ?? throw new Exception("cmd.exe did not start");

        try
        {
            Thread.Sleep(250);
            var key = new ProcKey((uint)child.Id, 0);

            using (var reader = new CommandLineReader())
            {
                CommandLineResult commandLine = reader.Read(key);
                if (!commandLine.IsOk ||
                    commandLine.Text is null ||
                    commandLine.Text.IndexOf("ping", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw new Exception($"child command line: status={commandLine.Status}, text={commandLine.Text}");
                }
            }

            // 创建时间不匹配必须拒绝，而且不能碰进程
            KillOutcome stale = ProcessTerminator.Kill(new ProcKey(key.Pid, 12345), selfPid);
            if (stale.Status != KillStatus.ProcessReused)
                throw new Exception($"stale create time: expected ProcessReused, got {stale.Status}");

            if (child.HasExited)
                throw new Exception("stale guard killed the child");

            // CreateTime=0 表示这次测试不校验创建时间，只验证 TerminateProcess 路径
            KillOutcome killed = ProcessTerminator.Kill(key, selfPid);
            if (killed.Status != KillStatus.Success)
                throw new Exception($"kill status={killed.Status} win32={killed.Win32Error}");

            if (!child.WaitForExit(3000))
                throw new Exception("child did not exit within 3s");
        }
        finally
        {
            try
            {
                if (!child.HasExited)
                    child.Kill();
            }
            catch
            {
                // 测试进程可能已经被结束
            }
        }
    }

    private static void RunControlledTreeKill()
    {
        uint selfPid = (uint)Environment.ProcessId;

        var start = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // 父 cmd → 子 cmd → ping。不要用 start：start 会多一层解析，
            // 诊断里可能出现“快照之后才出生的另一条子链”，留下孤儿 ping。
            Arguments = "/c cmd.exe /c ping 127.0.0.1 -n 30 >nul",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using Process parent = Process.Start(start)
            ?? throw new Exception("parent cmd.exe did not start");

        // 给 start /wait → 子 cmd → ping 留出完整的出生时间，
        // 避免快照只看到半棵树，留下 30 秒孤儿。
        Thread.Sleep(700);

        var plan = new List<ProcKey>();
        int childPid = 0;
        ProcKey parentKey = default;

        try
        {
            var flattener = new TreeFlattener();

            // 等快照里出现“父 + 至少一个子”
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Thread.Sleep(100);

                using var enumerator = new ProcessEnumerator();
                if (!enumerator.SampleRaw(out RawProcessEntry[] raw, out int count, out _))
                    continue;

                var nodes = new NodeRef[count];
                int parentIndex = -1;

                for (int i = 0; i < count; i++)
                {
                    nodes[i] = new NodeRef(
                        new ProcKey(raw[i].Pid, raw[i].CreateTime),
                        raw[i].ParentPid);

                    if (raw[i].Pid == parent.Id)
                        parentIndex = i;
                }

                if (parentIndex < 0) continue;

                parentKey = nodes[parentIndex].Key;
                plan.Clear();

                if (!flattener.TryCollectSubtreePostOrder(nodes, count, parentKey, plan))
                    continue;

                // 等整棵树都出现：父 cmd → 子 cmd → ping。
                // 不能只看节点数，conhost 等进程也可能让 count 提前到 3；
                // 必须确认计划里真的有 PING 这个孙进程，否则会留下 30 秒孤儿。
                bool hasPing = false;
                for (int i = 0; i < plan.Count; i++)
                {
                    try
                    {
                        using Process probe = Process.GetProcessById((int)plan[i].Pid);

                        // 只认本次父进程之后出生的 PING；上一次测试留下的孤儿
                        // 可能因为 PID 复用挂到新树上，创建时间会更早。
                        if (plan[i].CreateTime >= parentKey.CreateTime &&
                            probe.ProcessName.Equals("PING", StringComparison.OrdinalIgnoreCase))
                        {
                            hasPing = true;
                        }

                        Log.WriteLine($"[diag] tree plan: pid={plan[i].Pid} name={probe.ProcessName}");
                    }
                    catch
                    {
                        // 进程可能在两次查询之间退出
                    }
                }

                if (!hasPing) continue;

                for (int i = 0; i < count; i++)
                {
                    if (raw[i].ParentPid == parent.Id)
                    {
                        childPid = (int)raw[i].Pid;
                        break;
                    }
                }

                if (childPid != 0) break;
            }

            if (parentKey.Pid == 0 || plan.Count < 2)
                throw new Exception("controlled tree was not observed in snapshot");

            // 按快照后序执行，和 UI 的“结束进程树”走同一条路径
            KillReport report = ProcessTerminator.KillMany(plan, selfPid);
            for (int i = 0; i < report.Outcomes.Count; i++)
            {
                KillOutcome outcome = report.Outcomes[i];
                Log.WriteLine(
                    $"[diag] kill pid={outcome.Key.Pid} status={outcome.Status} win32={outcome.Win32Error}");
            }

            if (!parent.WaitForExit(3000))
                throw new Exception("parent did not exit");

            if (childPid != 0)
            {
                try
                {
                    using Process child = Process.GetProcessById(childPid);
                    if (!child.HasExited)
                        throw new Exception($"child {childPid} still alive");
                }
                catch (ArgumentException)
                {
                    // 进程已经不存在，正常
                }
            }
        }
        finally
        {
            try
            {
                if (!parent.HasExited)
                    parent.Kill();
            }
            catch { }

            try
            {
                // 兜底：如果第一次计划漏掉了某个后代，再按同一份计划杀一遍。
                if (plan.Count > 0)
                    ProcessTerminator.KillMany(plan, selfPid);
                else if (childPid != 0)
                    ProcessTerminator.Kill(new ProcKey((uint)childPid, 0), selfPid);
            }
            catch { }
        }
    }

    private static void Check(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Log.WriteLine($"[diag] PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Log.WriteLine($"[diag] FAIL  {name}: {ex.Message}");
        }
    }
}
