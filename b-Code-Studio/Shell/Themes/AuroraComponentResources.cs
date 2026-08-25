using System.Windows;

namespace HistoryAurora.Shell.Themes;

/// <summary>
/// 让一个组件在**任何宿主容器里**都能解析到 `Aurora.*` 具名样式。
///
/// 为什么需要这一步：具名样式住在 `AuroraControls.xaml`，而它只被
/// `ShellWindow.xaml` 与几个视图显式合并。AvalonDock 的浮动窗口是独立 `Window`，
/// 拿到的是 `AuroraTheme.xaml`（令牌 + 停靠样式）与主题切换时补进去的令牌字典——
/// **里面没有控件字典**。组件被拖进浮动窗口后，`DynamicResource Aurora.Table.Row`
/// 找不到键，而 WPF 对此**不抛异常**，只是不套用——表现为"只有这一页长得不一样"，
/// 且浅色下几乎看不出来。
///
/// 这与 DEC-009 给弹窗定的规矩是同一条：**能离开主窗体的东西，自带字典**。
/// 令牌不在这里合并——令牌随主题切换，仍应从容器（主窗体或浮动窗口）继承，
/// 在组件上钉死一份反而会让它切不动主题。
/// </summary>
public static class AuroraComponentResources
{
    private static readonly Uri ControlsUri =
        new("/HistoryAurora;component/Themes/AuroraControls.xaml", UriKind.Relative);

    /// <summary>
    /// 把控件字典并进元素自己的资源。幂等：重复调用不会叠加。
    /// <c>ResourceDictionary.Source</c> 相同的字典由 WPF 缓存复用，
    /// 因此每个组件实例并不会各自解析一遍那一千多行。
    /// </summary>
    public static void Ensure(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        foreach (var merged in element.Resources.MergedDictionaries)
        {
            if (merged.Source == ControlsUri)
                return;
        }

        element.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ControlsUri });
    }
}
