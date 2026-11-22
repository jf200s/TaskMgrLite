using System.ComponentModel;

namespace TaskMgrLite.Core;

/// <summary>
/// 列排序状态。只描述“按哪一列、什么方向”，不持有视图。
/// 树形模式挂起、平铺模式应用，由调用方根据 ShouldApply / IsSuspended 判断。
/// </summary>
public readonly record struct SortState(string? Key, ListSortDirection Direction)
{
    public static SortState Empty => new(null, ListSortDirection.Ascending);

    public bool HasSort => Key is not null;

    /// <summary>
    /// 同一列在升序/降序之间翻转；换一列按列类型给默认方向：
    /// 数值列先降序（找最大/最多），文本列先升序。
    /// 命令行列懒加载，不允许排序。
    /// </summary>
    public SortState Next(string key)
    {
        if (!CanSort(key))
            return this;

        if (Key == key)
        {
            ListSortDirection flipped = Direction == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
            return new SortState(key, flipped);
        }

        return new SortState(key, DefaultDirection(key));
    }

    /// <summary>PID / 父 PID / CPU / 内存 / 句柄属于数值列。</summary>
    public static bool IsNumericKey(string? key) => key is
        "Pid" or "ParentPid" or "CpuPercent" or "MemoryMb" or "HandleCount";

    public static ListSortDirection DefaultDirection(string key) =>
        IsNumericKey(key) ? ListSortDirection.Descending : ListSortDirection.Ascending;

    /// <summary>命令行是懒加载的，排序结果不完整，直接禁用。</summary>
    public static bool CanSort(string? key) =>
        IsKnownKey(key) && key != "CommandLine";

    /// <summary>平铺模式且有排序时才应用。</summary>
    public bool ShouldApply(ProcessViewMode mode) =>
        mode == ProcessViewMode.Flat && HasSort;

    /// <summary>树形模式下排序被挂起，但状态仍然保留。</summary>
    public bool IsSuspended(ProcessViewMode mode) =>
        mode == ProcessViewMode.Tree && HasSort;

    public static bool IsKnownKey(string? key) => key is
        "Name" or "Pid" or "ParentPid" or "CpuPercent" or
        "MemoryMb" or "HandleCount" or "CommandLine";

    /// <summary>只接受可排序的真实列，避免坏 JSON 让 SortDescription 抛异常。</summary>
    public static string? NormalizeKey(string? key) =>
        CanSort(key) ? key : null;

    public static ListSortDirection NormalizeDirection(ListSortDirection direction) =>
        direction == ListSortDirection.Descending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

    public static string ColumnName(string key) => key switch
    {
        "Name" => "名称",
        "Pid" => "PID",
        "ParentPid" => "父 PID",
        "CpuPercent" => "CPU %",
        "MemoryMb" => "内存（专用）",
        "HandleCount" => "句柄",
        "CommandLine" => "命令行",
        _ => key
    };
}
