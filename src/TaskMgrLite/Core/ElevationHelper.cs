using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;

namespace TaskMgrLite.Core;

/// <summary>
/// 权限不足时的一键提权重启。保留当前命令行参数（--real 等），
/// 并在提权前把最小 UI 状态写入临时文件，供新进程恢复。
/// </summary>
public static class ElevationHelper
{
    public static bool TryRestartElevated(UiState? state = null)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;

        string? statePath = null;
        if (state is UiState snapshot)
        {
            try
            {
                statePath = UiStateStore.Save(snapshot);
            }
            catch
            {
                // 状态保存失败不影响提权本身
                statePath = null;
            }
        }

        var start = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas"
        };

        string arguments = BuildArguments(Environment.GetCommandLineArgs(), statePath);
        if (arguments.Length > 0)
            start.Arguments = arguments;

        try
        {
            Process.Start(start);
            Application.Current.Shutdown();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 对话框点了“否”
            UiStateStore.TryDelete(statePath);
            return false;
        }
        catch
        {
            UiStateStore.TryDelete(statePath);
            return false;
        }
    }

    /// <summary>
    /// 重建给新进程的参数：过滤掉旧的 --restore-ui，避免二次提权引用已删除的文件。
    /// internal 供自检直接覆盖参数拼接。
    /// </summary>
    internal static string BuildArguments(string[] args, string? restorePath)
    {
        var text = new StringBuilder(64);

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--restore-ui=", StringComparison.OrdinalIgnoreCase))
                continue;

            AppendArgument(text, args[i]);
        }

        if (!string.IsNullOrEmpty(restorePath))
            AppendArgument(text, "--restore-ui=" + restorePath);

        return text.ToString();
    }

    private static void AppendArgument(StringBuilder text, string value)
    {
        if (text.Length > 0) text.Append(' ');
        AppendQuoted(text, value);
    }

    private static void AppendQuoted(StringBuilder text, string value)
    {
        if (value.Length > 0 &&
            value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            text.Append(value);
            return;
        }

        text.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"') text.Append('\\');
            text.Append(c);
        }
        text.Append('"');
    }
}
