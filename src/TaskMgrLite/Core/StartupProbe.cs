using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace TaskMgrLite.Core;

/// <summary>
/// 冷启动测量：进程创建（内核记录）→ 数据就绪后的第一个 CompositionTarget.Rendering。
///
/// 注意：直接取第一个 Rendering 会量到「空壳窗口」——实测它比 Window.Loaded 还早 140ms。
/// 所以必须等列表填充完成（MarkDataReady）之后再取下一帧，才是用户真正看到数据的时间。
/// 不用轮询 MainWindowHandle，那个时间点更早。
/// </summary>
public static class StartupProbe
{
    private static DateTime _processStartUtc;
    private static bool _reported;
    private static bool _shellReported;
    private static volatile bool _dataReady;

    public static void Begin()
    {
        try
        {
            _processStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            // 极少数受限环境读不到 StartTime，退化为「现在」，测出来会偏小，日志里能看出来
            _processStartUtc = DateTime.UtcNow;
        }

        double toMain = (DateTime.UtcNow - _processStartUtc).TotalMilliseconds;
        Log.WriteLine($"[probe] process-start={_processStartUtc:O} to-main={toMain:F1}ms");
    }

    /// <summary>启动阶段打点，用来把 to-main 之后的时间拆开看。</summary>
    public static void Mark(string stage)
    {
        double ms = (DateTime.UtcNow - _processStartUtc).TotalMilliseconds;
        Log.WriteLine($"[probe] {stage} @ {ms:F1}ms");
    }

    /// <summary>列表数据已经进入可视化树，下一帧才是「有内容的首帧」。</summary>
    public static void MarkDataReady()
    {
        _dataReady = true;
        Mark("data-ready");
    }

    public static void Attach(Window window)
    {
        EventHandler? handler = null;

        handler = (_, _) =>
        {
            // 第一个 Rendering：窗口壳子已经上屏，但列表还是空的，只作为参考值
            if (!_shellReported)
            {
                _shellReported = true;
                double shell = (DateTime.UtcNow - _processStartUtc).TotalMilliseconds;
                Log.WriteLine($"[probe] shell-frame-ms={shell:F1}");
            }

            if (!_dataReady || _reported || handler is null) return;

            _reported = true;
            CompositionTarget.Rendering -= handler;

            double ms = (DateTime.UtcNow - _processStartUtc).TotalMilliseconds;
            Log.WriteLine($"[probe] first-frame-ms={ms:F1}");
        };

        CompositionTarget.Rendering += handler;
    }
}
