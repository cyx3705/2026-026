using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryAurora.Shell.Base;
using HistoryAurora.Shell.Components.Themes;
using HistoryVulcan.Core.Logging;
using AvalonDock.Themes;

namespace HistoryAurora.Shell.Composition;

internal partial class ShellWindow
{
    private void ApplyTheme(string mode, bool persist)
    {
        var dark = mode.Equals(ThemeDark, StringComparison.OrdinalIgnoreCase);
        var uri = dark ? DarkTokensUri : LightTokensUri;

        SwapTokens(Resources, uri, atEnd: false);
        SwapTokens(DockManager.Resources, uri, atEnd: true);
        if (Application.Current != null)
            SwapTokens(Application.Current.Resources, uri, atEnd: true);
        ApplyThemeToFloatingWindows(uri);

        _theme = dark ? ThemeDark : ThemeLight;
        if (persist)
        {
            _settings.Set(ThemeSettingsKey, _theme);
            if (_menusInitialized)
                BuildMenus();
        }
    }

    /// <summary>
    /// R4-3:浮动窗口是独立 Window,自带一份 AvalonDock 主题字典(里面是浅色令牌),
    /// 优先级高于应用级资源 —— 不单独换,拖出来的工具页外框就一直是白的。
    /// 新浮动窗口在 WindowsChanged 后补一次。
    /// </summary>
    private void ApplyThemeToFloatingWindows(Uri? tokensUri = null)
    {
        var uri = tokensUri ?? (_theme == ThemeDark ? DarkTokensUri : LightTokensUri);
        foreach (var floating in DockManager.FloatingWindows.ToList())
        {
            // 已经是目标主题就整窗跳过 —— 本方法在布局回调里跑,不幂等会死循环
            SwapTokens(floating.Resources, uri, atEnd: true);
            if (floating.TryFindResource("Aurora.Brush.Surface") is Brush surface)
                floating.Background = surface;
            if (floating.TryFindResource("Aurora.Brush.Hairline") is Brush hairline)
            {
                floating.BorderBrush = hairline;
                FloatingWindowTheme.ApplyNativeBorder(floating, hairline);
            }
        }
    }

    /// <summary>
    /// 令牌字典换位。必须幂等:这个方法会被布局回调反复调用,
    /// 每次都无脑换字典会让资源全量失效 → 触发布局 → 再回调,直接转成死循环。
    /// </summary>
    private static bool SwapTokens(ResourceDictionary target, Uri uri, bool atEnd)
    {
        var existing = target.MergedDictionaries
            .Where(dictionary => dictionary.Source == LightTokensUri || dictionary.Source == DarkTokensUri)
            .ToList();
        if (existing.Count == 1 && existing[0].Source == uri)
            return false;

        foreach (var dictionary in existing)
            target.MergedDictionaries.Remove(dictionary);

        var tokens = new ResourceDictionary { Source = uri };
        if (atEnd)
            target.MergedDictionaries.Add(tokens);
        else
            target.MergedDictionaries.Insert(0, tokens);
        return true;
    }

    // ---------------------------------------------------------------- 窗格样式

    /// <summary>
    /// 取主题字典供窗格样式查基底键用。
    ///
    /// 主题按**实例**下发（REQ-UI-029），这里直接拿那一份，不再按 URI 重新解析。
    /// "按 URI 重解析"仓里原本有两处：AvalonDock 给覆盖窗的那处，和这里。前者让覆盖窗
    /// 拿不到画刷、蓝色方位指示一个像素都不画；后者一旦失效，窗格样式整个退回
    /// AvalonDock 默认外观。**同一个毛病要一次找全。**
    /// </summary>
    private void LoadThemeResources()
    {
        if (DockManager.Theme is DictionaryTheme { ThemeResourceDictionary: { } dictionary })
        {
            _themeResources = dictionary;
            return;
        }

        try
        {
            _themeResources = new ResourceDictionary { Source = DockManager.Theme.GetResourceUri() };
        }
        catch (Exception ex)
        {
            _themeResources = null;
            _log.Warn("shell", $"读取主题字典失败,窗格保持 AvalonDock 默认外观: {ex.Message}");
        }
    }

    /// <summary>
    /// 窗格外观下发(UI-01 卡片化 + UI-04 专注形态)。1.20.2 起窗格没有页签行（REQ-UI-101，顶栏删除）。
    /// 必须经 DockingManager.AnchorablePaneControlStyle / DocumentPaneControlStyle 属性下发:
    /// 主题字典里的隐式 Style 不会命中窗格控件,浮动窗口也走这两个属性。
    /// 以主题内置窗格样式为基底(继承 ItemContainerStyle 等),只覆盖模板。
    /// v5 迁移注意:基底样式的资源键随主题版本变化,需同步调整。
    /// </summary>
    private void ApplyPaneStyles(bool chromeless)
    {
        var suffix = chromeless ? ".Chromeless" : string.Empty;

        if (BuildPaneStyle(
                typeof(AvalonDock.Controls.LayoutAnchorablePaneControl),
                "AvalonDockThemeVs2013AnchorablePaneControlStyle",
                $"Aurora.Docking.AnchorablePaneTemplate{suffix}",
                "Aurora.Docking.AnchorableTabContainerStyle") is { } anchorableStyle)
        {
            DockManager.AnchorablePaneControlStyle = anchorableStyle;
        }

        if (BuildPaneStyle(
                typeof(AvalonDock.Controls.LayoutDocumentPaneControl),
                "AvalonDockThemeVs2013DocumentPaneControlStyle",
                $"Aurora.Docking.DocumentPaneTemplate{suffix}",
                "Aurora.Docking.DocumentTabContainerStyle") is { } documentStyle)
        {
            DockManager.DocumentPaneControlStyle = documentStyle;
        }

        ScheduleFloatingTheme();
    }

    private Style? BuildPaneStyle(
        Type paneType, string baseStyleKey, string templateKey, string tabItemStyleKey)
    {
        if (_themeResources == null)
            return null;

        if (FindInDictionary(_themeResources, templateKey) is not ControlTemplate template)
        {
            _log.Warn("shell", $"未找到窗格模板 {templateKey},该窗格保持主题默认外观");
            return null;
        }

        // 基底样式取不到时不放弃改造:直接建裸样式,只是失去主题的 ItemContainerStyle 等继承项
        var baseStyle = FindInDictionary(_themeResources, baseStyleKey) as Style;
        if (baseStyle == null)
            _log.Warn("shell", $"未找到主题窗格基底样式 {baseStyleKey},已改用裸样式");

        var style = baseStyle == null ? new Style(paneType) : new Style(paneType, baseStyle);
        style.Setters.Add(new Setter(TabControl.TabStripPlacementProperty, Dock.Top));
        style.Setters.Add(new Setter(Control.PaddingProperty, default(Thickness)));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        // 模板可能由 WPF 跨热重载缓存；用框架自身的 Tag/IsChecked 连接当前实例，
        // 避免旧 ALC 的 PageLabelMode 附加属性与新实例的属性身份不一致。
        style.Setters.Add(new Setter(FrameworkElement.TagProperty, PageAdjustmentToggle));

        // 窗格样式同时应用到 AvalonDock 的独立内容宿主。Ctrl 标签态的按下与拖动必须随样式下发，
        // 不能只扫描主 DockingManager 的视觉树，否则浮窗里的页名标签收不到输入。
        style.Setters.Add(new EventSetter(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnPanePreviewMouseLeftButtonDown))
        {
            HandledEventsToo = true,
        });
        style.Setters.Add(new EventSetter(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnPanePreviewMouseMove))
        {
            HandledEventsToo = true,
        });
        style.Setters.Add(new EventSetter(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnPanePreviewMouseLeftButtonUp))
        {
            HandledEventsToo = true,
        });
        style.Setters.Add(new EventSetter(
            Mouse.LostMouseCaptureEvent,
            new MouseEventHandler(OnPaneLostMouseCapture))
        {
            HandledEventsToo = true,
        });

        // 页签行不画了，但页签容器仍要生成：TabControl 靠它产出 SelectedContent。
        if (FindInDictionary(_themeResources, tabItemStyleKey) is Style tabStyle)
            style.Setters.Add(new Setter(TabControl.ItemContainerStyleProperty, tabStyle));
        else
            _log.Warn("shell", $"未找到页签样式 {tabItemStyleKey},页签保持主题默认外观");

        return style;
    }

    private void OnPanePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _pageDrag.HandlePaneMouseLeftButtonDown(sender, e);

    private void OnPanePreviewMouseMove(object sender, MouseEventArgs e)
        => _pageDrag.HandlePaneMouseMove(sender, e);

    private void OnPanePreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _pageDrag.HandlePaneMouseLeftButtonUp(sender, e);

    private void OnPaneLostMouseCapture(object sender, MouseEventArgs e)
        => _pageDrag.HandlePaneLostMouseCapture(sender, e);

    /// <summary>R4-3:浮动窗口是布局后才建出来的,换完窗格样式后补一次主题最稳妥。</summary>
    private void ScheduleFloatingTheme()
    {
        if (_floatingThemePending || _closing)
            return;
        _floatingThemePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _floatingThemePending = false;
            if (!_closing)
                ApplyThemeToFloatingWindows();
        });
    }

    // ---------------------------------------------------------------- 专注模式(UI-04)
    //
    // 页面最大化 = 专注态:窗格铺满,右栏(含窗口控制组)照常在右侧。
    // 由 DockingHost.WindowsChanged 驱动 —— MaximizeWindow 与
    // RestoreLayoutFromMaximized 都从那里出口,不需要新增公开 API。
}
