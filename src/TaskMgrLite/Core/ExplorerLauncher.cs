using System.Diagnostics;
using System.IO;

namespace TaskMgrLite.Core;

/// <summary>
/// “打开文件所在位置”的纯逻辑部分。
/// BuildSelectArguments 单独抽出来，selftest 可以验证带空格路径的引号处理。
/// </summary>
public static class ExplorerLauncher
{
    public static string BuildSelectArguments(string path) => $"/select,\"{path}\"";

    public static bool TryOpen(string? imagePath, out string error)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            error = "没有可用的进程路径。";
            return false;
        }

        if (!File.Exists(imagePath))
        {
            error = "文件已不存在或无法访问。";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = BuildSelectArguments(imagePath),
                UseShellExecute = true
            });

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
