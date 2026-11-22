namespace TaskMgrLite.Core;

/// <summary>
/// 提权重启时跨进程传递的最小 UI 状态。
/// 只保存搜索词、视图模式和折叠集合；不保存选中行、滚动位置或窗口位置。
/// </summary>
public readonly record struct UiState(
    string SearchText,
    ProcessViewMode ViewMode,
    ProcKey[] Collapsed,
    DateTime SavedAtUtc)
{
    public const int MaxSearchLength = 256;
    public const int MaxCollapsed = 4096;

    /// <summary>裁剪到可接受范围；搜索非空时按现有规则强制平铺。</summary>
    public UiState Normalize()
    {
        string search = SearchText ?? string.Empty;
        if (search.Length > MaxSearchLength)
            search = search[..MaxSearchLength];

        ProcKey[] collapsed = Collapsed ?? Array.Empty<ProcKey>();
        if (collapsed.Length > MaxCollapsed)
            Array.Resize(ref collapsed, MaxCollapsed);

        ProcessViewMode mode = string.IsNullOrWhiteSpace(search)
            ? ViewMode
            : ProcessViewMode.Flat;

        return new UiState(search, mode, collapsed, SavedAtUtc);
    }
}
