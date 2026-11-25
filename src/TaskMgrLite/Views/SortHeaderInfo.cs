using System.Windows;

namespace TaskMgrLite.Views;

/// <summary>
/// GridViewColumn 的轻量表头数据。Key 是排序属性名，Text 是显示文案；
/// 共享的 SortHeaderTemplate 负责渲染文字和 ▲/▼ 箭头。
/// </summary>
public sealed class SortHeaderInfo
{
    public string SortKey { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public double Opacity { get; set; } = 1.0;
    public string? ToolTip { get; set; }

    /// <summary>兼容旧 StackPanel.Tag 和新的 SortHeaderInfo 两种列头内容。</summary>
    public static string? GetKey(object? headerContent) => headerContent switch
    {
        SortHeaderInfo info => info.SortKey,
        FrameworkElement { Tag: string key } => key,
        _ => null
    };
}
