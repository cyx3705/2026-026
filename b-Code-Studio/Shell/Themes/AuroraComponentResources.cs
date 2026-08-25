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

    // **按线程**缓存，不是按进程。字典里装的是 Style / ControlTemplate / Brush，
    // 它们都是 DispatcherObject：在 A 线程建好、拿到 B 线程用，会抛
    // 「调用线程无法访问此对象，因为另一个线程拥有该对象」。
    // 正式运行时只有一个界面线程，所以效果仍然是"整个进程加载一次"；
    // 而门禁里每个用例各起一条 STA 线程，共享一份会让测试宿主直接崩掉（实测）。
    [ThreadStatic]
    private static ResourceDictionary? _controls;

    private static readonly Uri ThemeUri =
        new("/HistoryAurora;component/Themes/AuroraTheme.xaml", UriKind.Relative);

    [ThreadStatic]
    private static ResourceDictionary? _theme;

    [ThreadStatic]
    private static string? _failure;

    /// <summary>
    /// 字典加载失败的原因；从未失败时为 null。
    ///
    /// 加载**不抛**：这个方法被组件构造函数调用，而组件构造函数又被停靠系统的
    /// 内容工厂调用——在那里抛异常会让"最大化某一页"这种无关操作整个失败，
    /// 且经指令总线回来的只剩一个异常类型名（总线刻意不外发 Message）。
    /// 失败时组件退回继承容器的字典：在主窗体里那本来就够用，只有浮窗里才会退化。
    /// 所以失败要记下来、可查，但不能炸。
    /// </summary>
    public static string? LoadFailure => _failure;

    /// <summary>
    /// 把控件字典并进元素自己的资源。幂等：重复调用不会叠加。
    ///
    /// 字典**每条界面线程只加载一次**并共享同一个实例。此前每个组件各自
    /// `new ResourceDictionary { Source = … }`：那条路径每次都要按 URI 重新解析资源，
    /// 而按 URI 解析依赖 WPF 的应用级资源上下文——在模块被装进可回收 ALC、
    /// 且入口程序集不是本程序集的宿主进程里，它并不是任何时候都成立的。
    /// 共享单例把这件事收敛成一次，且发生在启动期第一个视图构造时。
    /// </summary>
    public static void Ensure(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var controls = Load();
        if (controls == null)
            return;

        foreach (var merged in element.Resources.MergedDictionaries)
        {
            if (ReferenceEquals(merged, controls) || merged.Source == ControlsUri)
                return;
        }

        element.Resources.MergedDictionaries.Add(controls);
    }

    /// <summary>
    /// 把**主题**字典（令牌 + 停靠画刷）并进元素自己的资源。幂等。
    ///
    /// 给 AvalonDock 的覆盖窗（拖动时那组蓝色方位指示）用。覆盖窗是独立 `Window`，
    /// 按 DEC-009 属于"能离开主窗体的东西"，本该自带字典，此前漏了。它的画刷靠
    /// AvalonDock 自己按 URI 重新解析主题字典，而按 URI 解析依赖 WPF 的应用级资源
    /// 上下文——模块被装进可回收 ALC、入口程序集不是本程序集时并不总成立。
    ///
    /// 解析不到画刷时 WPF **不抛异常**，只是不套用：元素照常排布、照常"可见"、尺寸
    /// 也对，就是一个像素都不画。真机实测覆盖窗 210 个元素、105 个可见、Z 序在主窗体
    /// 之上，渲染成位图后非透明像素为 0——正是这个形态。
    /// </summary>
    public static void EnsureTheme(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var theme = LoadTheme();
        if (theme == null)
            return;

        foreach (var merged in element.Resources.MergedDictionaries)
        {
            if (ReferenceEquals(merged, theme) || merged.Source == ThemeUri)
                return;
        }

        element.Resources.MergedDictionaries.Add(theme);
    }

    private static ResourceDictionary? LoadTheme()
    {
        if (_theme != null || _failure != null)
            return _theme;

        try
        {
            _theme = new ResourceDictionary { Source = ThemeUri };
        }
        catch (Exception ex)
        {
            _failure = ex.GetType().Name + ": " + ex.Message;
            System.Diagnostics.Debug.WriteLine("AuroraTheme.xaml 加载失败: " + ex);
        }

        return _theme;
    }

    private static ResourceDictionary? Load()
    {
        if (_controls != null || _failure != null)
            return _controls;

        try
        {
            _controls = new ResourceDictionary { Source = ControlsUri };
        }
        catch (Exception ex)
        {
            _failure = ex.GetType().Name + ": " + ex.Message;
            System.Diagnostics.Debug.WriteLine("AuroraControls.xaml 加载失败: " + ex);
        }

        return _controls;
    }
}
