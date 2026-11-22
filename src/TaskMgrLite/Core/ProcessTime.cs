namespace TaskMgrLite.Core;

/// <summary>FILETIME（100ns since 1601）→ 本地时间显示。</summary>
public static class ProcessTime
{
    public static DateTime? FromFileTime(long value)
    {
        if (value <= 0) return null;

        try
        {
            return DateTime.FromFileTimeUtc(value).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string Format(long value) =>
        FromFileTime(value)?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
}
