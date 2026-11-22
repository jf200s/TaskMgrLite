using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;

namespace TaskMgrLite.Core;

/// <summary>
/// 权限不足时的一键提权重启。保留当前命令行参数（--real 等）。
/// 搜索词和展开状态本轮不持久化，提权后会回到默认视图。
/// </summary>
public static class ElevationHelper
{
    public static bool TryRestartElevated()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;

        var start = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas"
        };

        string arguments = BuildArguments();
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
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildArguments()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Length <= 1)
            return string.Empty;

        var text = new StringBuilder(64);

        for (int i = 1; i < args.Length; i++)
        {
            if (text.Length > 0) text.Append(' ');
            AppendQuoted(text, args[i]);
        }

        return text.ToString();
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
