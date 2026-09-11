using System.Windows;

namespace HistoryAurora.Shell.Base;

/// <summary>
/// Ctrl 标签态（REQ-UI-097）的开关，挂在窗口上：主窗体和每一个浮窗各挂一份。
///
/// 窗格模板按所在窗口的这个值，把内容换成一块写着页名的大标签（<see cref="CoverTag"/>），
/// 拖动协调器认这块标签起手。挂在窗口而不是停靠管理器上，是因为浮窗是独立 Window，
/// 顺着可视树往上走不到停靠管理器。
/// </summary>
internal static class PageLabelMode
{
    /// <summary>窗格模板里页名标签的标记（见 AuroraDocking.xaml）。</summary>
    public const string CoverTag = "PageLabelCover";

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(PageLabelMode),
        new PropertyMetadata(false));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
}
