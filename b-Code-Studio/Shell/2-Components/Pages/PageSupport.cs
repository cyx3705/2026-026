using HistoryAurora.Shell.Components.Catalog;

namespace HistoryAurora.Shell.Components.Pages;

/// <summary>
/// 页面协议「已经支持什么」的单一答案（2.1.0 起渲染在浏览器里，这里只剩判定）。
/// </summary>
/// <remarks>
/// 组件申请台账靠它判断一条申请是否已交付：节点类型以 <see cref="ComponentCatalog"/> 为准，
/// 能力是节点或面板上的附加约定，不是独立节点，因此单列。
/// </remarks>
internal static class PageSupport
{
    /// <summary>页面节点的 type 值；与组件目录的节点层一一对应。</summary>
    public static readonly IReadOnlySet<string> SupportedComponents =
        new HashSet<string>(ComponentCatalog.NodeTypes, StringComparer.OrdinalIgnoreCase);

    /// <summary>挂在节点或面板上的附加能力。</summary>
    public static readonly IReadOnlySet<string> SupportedCapabilities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "table.rowactions", "table.cellaction", "dialog.choice", "menu",
            "dialog.contentactions",
            "table.channel", "panel.follows", "panel.enabledwhen",
            "panel.rows", "panel.minwidth", "panel.flex", "panel.icon",
            "panel.optionssource",
            "panel.switch", "panel.sourcepicker", "panel.commitaction",
            "table.datasource.selection", "swimlane.datasource.selection",
            "panel.channel",
            "popup.trigger",
        };

    /// <summary>该名字是否已由页面协议提供。</summary>
    public static bool Supports(string component)
        => SupportedComponents.Contains(component) || SupportedCapabilities.Contains(component);
}
