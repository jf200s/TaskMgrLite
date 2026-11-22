using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Windows;
using System.Windows.Threading;
using TaskMgrLite.Core;
using TaskMgrLite.ViewModels;

namespace TaskMgrLite;

public partial class App : Application
{
    public App()
    {
        // 首轮跑起来最怕「闪退但没有信息」，先把异常落到 run.log
        DispatcherUnhandledException += (_, args) =>
        {
            Log.WriteLine("[fatal] " + args.Exception);
            Log.Flush();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.WriteLine("[fatal-domain] " + args.ExceptionObject);
            Log.Flush();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Flush();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool stats = HasFlag(e.Args, "--stats");
        bool real = ResolveUseRealData(e.Args);
        bool probeExit = HasFlag(e.Args, "--probe-exit");
        bool selftest = HasFlag(e.Args, "--selftest");
        bool diagRound3 = HasFlag(e.Args, "--diag-round3");
        bool diagRound4 = HasFlag(e.Args, "--diag-round4");

        // 只在诊断/统计模式接管控制台，--probe-exit 静默跑，避免污染测量输出
        Log.Initialize(attachConsole: stats || selftest || diagRound3 || diagRound4);

        if (diagRound3)
        {
            Log.WriteLine("[boot] diag-round3（不创建窗口）");
            int failed = Round3Diagnostics.Run();
            Environment.ExitCode = failed;
            Shutdown(failed);
            return;
        }

        if (diagRound4)
        {
            Log.WriteLine("[boot] diag-round4（不创建窗口）");
            int failed = Round4Diagnostics.Run();
            Environment.ExitCode = failed;
            Shutdown(failed);
            return;
        }

        if (selftest)
        {
            Log.WriteLine("[boot] selftest（不创建窗口）");

            int failed;
            try
            {
                failed = SelfTest.Run();
            }
            catch (Exception ex)
            {
                Log.WriteLine("[test] SUITE FAILED " + ex);
                failed = 99;
            }

            Environment.ExitCode = failed;
            Shutdown(failed);
            return;
        }

        Log.WriteLine($"[boot] args=[{string.Join(' ', e.Args)}]");
        Log.WriteLine($"[boot] os={Environment.OSVersion.VersionString} arch={RuntimeInformation.ProcessArchitecture} cores={Environment.ProcessorCount}");
        Log.WriteLine($"[boot] runtime={RuntimeInformation.FrameworkDescription} version={Environment.Version}");
        // 这条用来判断 R2R 镜像在这台机器上到底会不会被采用
        Log.WriteLine($"[boot] avx2={Avx2.IsSupported} avx512f={Avx512F.IsSupported} vector256={Vector256.IsHardwareAccelerated}");

        StartupProbe.Begin();

        var vm = new MainViewModel(useRealData: real, stats: stats, probeExit: probeExit);
        StartupProbe.Mark("vm-created");

        var window = new MainWindow { DataContext = vm };
        window.Closed += (_, _) => vm.Dispose();
        StartupProbe.Mark("window-created");

        this.MainWindow = window;
        window.Loaded += (_, _) =>
        {
            StartupProbe.Mark("window-loaded");
            vm.Start();   // 真实模式下，数据就绪由第一份快照应用时标记

            // 等首帧渲染完再数一次可视化树里的行容器
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(window.LogUiDiagnostics));
        };

        // 之前漏了这一行，所以日志里一直没有 first-frame-ms
        StartupProbe.Attach(window);

        window.Show();
        StartupProbe.Mark("show-returned");
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 数据源选择：默认真实数据（双击即用），只有显式 --fake 才进入假数据压测模式。
    /// --real 仍然有效，作为显式声明；同时给出 --real 和 --fake 时以 --real 为准，
    /// 免得旧快捷方式因为多带一个 --fake 就悄悄变成假数据。
    /// </summary>
    internal static bool ResolveUseRealData(string[] args) =>
        HasFlag(args, "--real") || !HasFlag(args, "--fake");
}
