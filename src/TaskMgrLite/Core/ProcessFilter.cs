namespace TaskMgrLite.Core;

/// <summary>
/// 纯字符串过滤，不依赖 WPF，方便 selftest。
/// 搜索只做名称 / PID 子串匹配，大小写不敏感；空查询匹配全部。
/// </summary>
public static class ProcessFilter
{
    public static bool Matches(string name, uint pid, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || pid.ToString().Contains(query, StringComparison.Ordinal);
    }
}
