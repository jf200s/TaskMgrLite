using System.IO;
using System.Text;
using System.Text.Json;

namespace TaskMgrLite.Core;

/// <summary>
/// 提权状态文件的存取。文件只放在 %TEMP%\TaskMgrLite\ 下，
/// 读取前校验路径、大小、版本和时间戳，任何一项不通过都静默忽略。
/// </summary>
public static class UiStateStore
{
    public const int Version = 1;
    public const int MaxBytes = 64 * 1024;

    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed class StateDto
    {
        public int Version { get; set; }
        public DateTime SavedAtUtc { get; set; }
        public string? SearchText { get; set; }
        public string? ViewMode { get; set; }
        public List<KeyDto>? Collapsed { get; set; }
    }

    private sealed class KeyDto
    {
        public uint Pid { get; set; }
        public long CreateTime { get; set; }
    }

    /// <summary>写入状态文件，返回最终路径。调用方负责在提权失败时删除。</summary>
    public static string Save(UiState state, string? rootDir = null)
    {
        string root = ResolveRoot(rootDir);
        Directory.CreateDirectory(root);

        UiState normalized = state.Normalize();

        var dto = new StateDto
        {
            Version = Version,
            SavedAtUtc = normalized.SavedAtUtc.ToUniversalTime(),
            SearchText = normalized.SearchText,
            ViewMode = normalized.ViewMode.ToString(),
            Collapsed = new List<KeyDto>(normalized.Collapsed.Length)
        };

        foreach (ProcKey key in normalized.Collapsed)
            dto.Collapsed.Add(new KeyDto { Pid = key.Pid, CreateTime = key.CreateTime });

        string path = Path.Combine(root, $"elevate-ui-{Environment.ProcessId}.json");
        string tmp = path + ".tmp";

        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        finally
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
                // 临时文件清理失败不影响主流程
            }
        }
    }

    /// <summary>读取并删除状态文件；路径越界或内容无效时返回 null。</summary>
    public static UiState? TryLoad(string? path, string? rootDir = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string root = ResolveRoot(rootDir);
        if (!TryResolveUnderRoot(path, root, out string full))
            return null;

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
                return null;

            if (info.Length > MaxBytes)
            {
                TryDelete(full, root);
                return null;
            }

            string json = File.ReadAllText(full, Encoding.UTF8);
            StateDto? dto = JsonSerializer.Deserialize<StateDto>(json, JsonOptions);

            if (dto is null || dto.Version != Version)
            {
                TryDelete(full, root);
                return null;
            }

            DateTime saved = dto.SavedAtUtc.Kind == DateTimeKind.Utc
                ? dto.SavedAtUtc
                : dto.SavedAtUtc.ToUniversalTime();

            DateTime now = DateTime.UtcNow;
            if (now - saved > MaxAge || saved > now.AddMinutes(1))
            {
                TryDelete(full, root);
                return null;
            }

            ProcessViewMode mode = ProcessViewMode.Tree;
            if (!string.IsNullOrWhiteSpace(dto.ViewMode) &&
                Enum.TryParse(dto.ViewMode, ignoreCase: true, out ProcessViewMode parsed) &&
                Enum.IsDefined(parsed))
            {
                mode = parsed;
            }

            string search = dto.SearchText ?? string.Empty;
            if (search.Length > UiState.MaxSearchLength)
                search = search[..UiState.MaxSearchLength];

            var collapsed = Array.Empty<ProcKey>();
            if (dto.Collapsed is { Count: > 0 } keys)
            {
                int count = Math.Min(keys.Count, UiState.MaxCollapsed);
                collapsed = new ProcKey[count];

                for (int i = 0; i < count; i++)
                    collapsed[i] = new ProcKey(keys[i].Pid, keys[i].CreateTime);
            }

            var state = new UiState(search, mode, collapsed, saved).Normalize();
            TryDelete(full, root);
            return state;
        }
        catch
        {
            TryDelete(full, root);
            return null;
        }
    }

    /// <summary>只删除 %TEMP%\TaskMgrLite\ 下的文件，避免调用方传入任意路径。</summary>
    public static bool TryDelete(string? path, string? rootDir = null)
    {
        string root = ResolveRoot(rootDir);
        if (!TryResolveUnderRoot(path, root, out string full))
            return false;

        try
        {
            if (File.Exists(full))
                File.Delete(full);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveRoot(string? rootDir) =>
        Path.GetFullPath(rootDir ?? Path.Combine(Path.GetTempPath(), "TaskMgrLite"));

    private static bool TryResolveUnderRoot(string? path, string root, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        string normalizedRoot =
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
