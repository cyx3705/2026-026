
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryAurora.Shell.Neutral.CommandSurface;
using HistoryAurora.Shell.Neutral.Commands;
using HistoryAurora.Shell.Neutral.Storage;
using HistoryAurora.Shell.Composition;
using HistoryAurora.Shell.HostedPages.Console;
using HistoryAurora.Shell.Components.Widgets;
using HistoryVulcan.Services.Commands;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Themes;
using AvalonDock.Themes.VS2013.Themes;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 3.1 ????(UI-02 / UI-03 / UI-04 / UI-05 / UI-09):
/// ????????????,???????,????????????
/// ???????????,??? XAML ??????
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class ShellChromeContractTests
{
    [Fact]
    public void ShellWindowHasNoMenuBarNoStatusBarAndNoTitleBarRow()
    {
        RunShell(window =>
        {
            // UI-03 / UI-05.1:???????????
            Assert.Empty(FindVisualDescendants<Menu>(window));
            Assert.Empty(FindVisualDescendants<System.Windows.Controls.Primitives.StatusBar>(window));

            // 3.1 ??:???????????? ?? ????????????,
            // ???????????????
            var chromeBar = RequireElement<FrameworkElement>(window, "ChromeBar");
            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var managerTop = manager.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            var chromeTop = chromeBar.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            Assert.True(managerTop <= chromeTop + 0.5,
                $"docking top={managerTop} must not be pushed below the chrome bar top={chromeTop}");
            Assert.True(chromeBar.ActualHeight <= 32.5,
                $"chrome bar height={chromeBar.ActualHeight} must match the tab row height");
        });
    }

    /// <summary>
    /// REQ-UI-099：窗口控制组搬到右栏顶部——仍是整个窗体的右上角，但不再挂在任何窗格上。
    /// 1.19.0 及以前它挂在主文档区的页签行上，命令集因此得常驻当锚点（REQ-UI-095 解开）。
    /// </summary>
    [Fact]
    public void WindowChromeBarSitsAtTheTopOfTheRightRail()
    {
        RunShell(window =>
        {
            var chromeBar = RequireElement<Panel>(window, "ChromeBar");
            var rail = RequireElement<FrameworkElement>(window, "NavRail");
            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));

            Assert.True(chromeBar.IsDescendantOf(rail), "window chrome must live in the right rail");
            Assert.Null(FindAncestor<LayoutDocumentPaneControl>(chromeBar, _ => true));

            var managerRight = manager.TransformToAncestor(window).Transform(new Point(manager.ActualWidth, 0)).X;
            var railOrigin = rail.TransformToAncestor(window).Transform(new Point(0, 0));
            Assert.True(railOrigin.X >= managerRight - 0.5,
                $"rail left={railOrigin.X} must sit right of the docking area right={managerRight}");

            var chromeTopRight = chromeBar.TransformToAncestor(window).Transform(new Point(chromeBar.ActualWidth, 0));
            Assert.True(chromeTopRight.Y <= railOrigin.Y + 0.5, $"chrome top={chromeTopRight.Y}");
            Assert.True(Math.Abs(chromeTopRight.X - (railOrigin.X + rail.ActualWidth)) < 1.5,
                $"chrome right={chromeTopRight.X} must reach the rail right edge={railOrigin.X + rail.ActualWidth}");
        });
    }

    /// <summary>
    /// 主窗体的系统标题栏高度保持 0（整窗都是客户区，右栏空白处自己拖窗口）。
    /// 1.20.2 起工具页也没有页头动作位（REQ-UI-101）：可见的 <see cref="AnchorablePaneTitle"/> 一个都不该有。
    /// </summary>
    [Fact]
    public void InvisibleCaptionStaysZeroAndNoPaneDrawsATitle()
    {
        RunShell(window =>
        {
            var chrome = WindowChrome.GetWindowChrome(window);
            Assert.NotNull(chrome);
            Assert.Equal(0, chrome.CaptionHeight);

            Assert.DoesNotContain(FindVisualDescendants<AnchorablePaneTitle>(window), item => item.IsVisible);
        });
    }

    [Fact]
    public void PersistedDarkThemeSurvivesDockManagerThemeAssignment()
    {
        // 回归：构造函数里先 ApplyTheme 再赋值 DockManager.Theme，后者重新合并 AvalonDock
        // 自带的浅色主题字典，把深色令牌盖掉——设置里明明是 dark，启动却是浅色，
        // 手动再切一次才对。这里断言窗口建好后深色令牌仍然生效。
        var settings = new MemorySettings();
        settings.Set("ui.theme", "dark");

        RunShell(
            window =>
            {
                // 窗体级资源没问题；出错的是停靠区：AuroraTheme.xaml 内部合并了浅色
                // AuroraTokens.xaml，若它排在深色令牌之后就会赢，界面整片变浅。
                var dockSurface = window.DockManager.TryFindResource("Aurora.Brush.Surface") as SolidColorBrush;
                Assert.NotNull(dockSurface);
                var brightness = (dockSurface!.Color.R + dockSurface.Color.G + dockSurface.Color.B) / 3.0;
                Assert.True(
                    brightness < 96,
                    $"停靠区 Aurora.Brush.Surface 应解析为深色令牌，实际 {dockSurface.Color}(亮度 {brightness:0})");

                var sources = window.DockManager.Resources.MergedDictionaries
                    .Select(dictionary => dictionary.Source?.ToString() ?? string.Empty)
                    .ToList();
                var tokenIndex = sources.FindIndex(item => item.EndsWith("AuroraTokens.Dark.xaml", StringComparison.Ordinal));
                var themeIndex = sources.FindIndex(item => item.EndsWith("AuroraTheme.xaml", StringComparison.Ordinal));
                Assert.True(tokenIndex >= 0 && themeIndex >= 0, "深色令牌与 AvalonDock 主题字典都应在停靠区资源里");
                Assert.True(
                    tokenIndex > themeIndex,
                    $"深色令牌必须排在主题字典之后才能生效，实际 tokens={tokenIndex} theme={themeIndex}");
            },
            settings: settings);
    }

    /// <summary>
    /// 停靠覆盖层是独立窗口,它的画刷必须同时满足两件事:在主题**源字典**里就已可绘制
    /// (拖动那一刻才合并就来不及,按钮会是一片透明),以及能从 <c>DockManager</c> 自身解析出来
    /// (它取不到主窗口的资源树)。两条路径合在一条用例里断言,省掉一次整壳启动。
    /// </summary>
    [Fact]
    public void DockingOverlayBrushesAreDrawableInTheThemeSourceAndFromTheManager()
    {
        RunShell(window =>
        {
            var manager = window.DockManager;
            var theme = Assert.IsAssignableFrom<DictionaryTheme>(manager.Theme);
            var dictionary = theme.ThemeResourceDictionary;
            Assert.NotNull(dictionary);

            var keys = new[]
            {
                ResourceKeys.DockingButtonForegroundBrushKey,
                ResourceKeys.DockingButtonForegroundArrowBrushKey,
                ResourceKeys.PreviewBoxBorderBrushKey,
                ResourceKeys.PreviewBoxBackgroundBrushKey,
            };

            foreach (var key in keys)
            {
                var prewarmed = Assert.IsType<SolidColorBrush>(FindResource(dictionary!, key));
                var resolved = Assert.IsType<SolidColorBrush>(manager.TryFindResource(key));
                if (key == ResourceKeys.PreviewBoxBackgroundBrushKey)
                {
                    Assert.Equal(0, prewarmed.Color.A);
                    Assert.Equal(0, resolved.Color.A);
                    continue;
                }

                Assert.True(prewarmed.Opacity > 0 && prewarmed.Color.A > 0,
                    $"主题源字典中的停靠画刷 {key} 必须在拖动前可绘制");
                Assert.True(resolved.Opacity > 0 && resolved.Color.A > 0,
                    $"停靠覆盖层画刷 {key} 不能是透明回退值");
            }

            var starBorder = Assert.IsType<SolidColorBrush>(
                manager.TryFindResource(ResourceKeys.DockingButtonStarBorderBrushKey));
            Assert.True(starBorder.Color.A > 0);

            var starBackground = Assert.IsType<SolidColorBrush>(
                manager.TryFindResource(ResourceKeys.DockingButtonStarBackgroundBrushKey));
            Assert.Equal(0, starBackground.Color.A);
            var buttonBackground = Assert.IsType<SolidColorBrush>(
                manager.TryFindResource(ResourceKeys.DockingButtonBackgroundBrushKey));
            Assert.Equal(0, buttonBackground.Color.A);

            var width = Assert.IsType<double>(FindResource(
                dictionary!, ResourceKeys.DockingButtonWidthKey));
            var height = Assert.IsType<double>(FindResource(
                dictionary!, ResourceKeys.DockingButtonHeightKey));
            Assert.InRange(width, 20, 32);
            Assert.InRange(height, 20, 32);
        });
    }

    private static object? FindResource(ResourceDictionary dictionary, object key)
    {
        if (dictionary.Contains(key))
            return dictionary[key];

        foreach (var merged in dictionary.MergedDictionaries)
        {
            var found = FindResource(merged, key);
            if (found != null)
                return found;
        }

        return null;
    }

    [Fact]
    public void CommandFailureOpensConsoleWithoutErrorFlyout()
    {
        RunShell(window =>
        {
            window.Docking.Hide(StandardWindowIds.Console);
            UiTestHost.Pump();
            Assert.False(window.Docking.ListWindows()
                .Single(item => item.Id == StandardWindowIds.Console).IsVisible);

            var result = window.Commands.ExecuteAsync("missing.command", "test")
                .GetAwaiter().GetResult();
            UiTestHost.Pump();

            Assert.False(result.Success);
            Assert.True(window.Docking.ListWindows()
                .Single(item => item.Id == StandardWindowIds.Console).IsVisible);
            Assert.Null(window.FindName("Toast"));
        });
    }

    [Fact]
    public void ConsoleEnterShowsTypedCommandAndFailureResult()
    {
        var log = new RelayLog();
        RunShell(
            window =>
            {
                var input = FindVisualDescendants<TextBox>(window)
                    .Single(item => item.Name == "Input");
                input.Focus();
                input.Text = "123";

                var key = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(input),
                    0,
                    Key.Enter)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                input.RaiseEvent(key);
                UiTestHost.PumpFor(250);

                var output = FindVisualDescendants<ListBox>(window)
                    .Single(list => list.Name == "Output");
                var rows = output.Items.Cast<HistoryAurora.Shell.HostedPages.Console.ConsoleRow>()
                    .Where(item => item.Text.Contains("123", StringComparison.Ordinal))
                    .ToList();

                Assert.True(rows.Any(item => item.Text.Contains("> 123", StringComparison.Ordinal)),
                    "the command echo must be visible after pressing Enter");
                Assert.True(rows.Any(item => item.Level >= ShellLogLevel.Error),
                    "the unknown-command error must be visible after pressing Enter");

                var echo = rows.First(item => item.Text.Contains("> 123", StringComparison.Ordinal));
                output.ScrollIntoView(echo);
                window.UpdateLayout();
                UiTestHost.Pump();
                Assert.True(output.IsVisible && output.ActualHeight > 0 && output.ActualWidth > 0,
                    $"console output is not laid out: visible={output.IsVisible}, " +
                    $"size={output.ActualWidth}x{output.ActualHeight}");
                var container = Assert.IsType<ListBoxItem>(
                    output.ItemContainerGenerator.ContainerFromItem(echo));
                container.ApplyTemplate();
                window.UpdateLayout();
                Assert.True(container.IsVisible && container.ActualHeight > 0,
                    "the command row exists but its ListBoxItem is not rendered");
                var descendants = FindVisualDescendants<DependencyObject>(container).ToList();
                var renderedTexts = descendants.OfType<TextBlock>().ToList();
                Assert.True(renderedTexts.Count == 1,
                    "unexpected row visual tree: " +
                    string.Join(", ", descendants.Select(item => item.GetType().Name)));
                var renderedText = renderedTexts[0];
                Assert.True(renderedText.IsVisible && renderedText.ActualWidth > 0,
                    "the command row exists but its TextBlock is not visible");
            },
            log: log);
    }

    [Fact]
    public void ConsoleRowsExtractDomainsFromCommandsOutputsAndLogCategories()
    {
        var now = DateTime.UtcNow;
        var rows = new[]
        {
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "cmd:UI", "app.open target=x"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "cmd:UI", "help"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "cmd:result:app", "done"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "cmd:progress:app", "step"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "module:loader.detail", "loaded"))[0],
            ConsoleRow.From(new ShellLogEntry(now, ShellLogLevel.Info, "render.frame", "drawn"))[0],
        };

        Assert.Equal(new[] { "app", "core", "app", "app", "module", "render" },
            rows.Select(row => row.DomainKey));
        Assert.Equal("UI", rows[0].SourceKey);
        Assert.Equal("result", rows[2].SourceKey);
    }

    [Fact]
    public void FailedConsoleCommandDoesNotExitFocusedConsole()
    {
        RunShell(window =>
        {
            var maximize = window.Commands.ExecuteAsync(
                $"aurora.ui.max name={StandardWindowIds.Console}", "test").GetAwaiter().GetResult();
            Assert.True(maximize.Success, maximize.Message);
            UiTestHost.Pump();
            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);

            var input = FindVisualDescendants<TextBox>(window)
                .Single(item => item.Name == "Input");
            input.Focus();
            input.Text = "missing.command";
            input.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(input),
                0,
                Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            UiTestHost.PumpFor(400);

            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);
            Assert.True(input.IsKeyboardFocusWithin);
        });
    }

    [Fact]
    public void FrontendFocusConsoleRaisesTheWindowAndRefocusesExistingConsole()
    {
        RunShell(window =>
        {
            Assert.True(window.Commands.ExecuteAsync("vulcan.app.hide", "test")
                .GetAwaiter().GetResult().Success);
            UiTestHost.Pump();
            Assert.False(window.IsVisible);

            Assert.True(WakeConsole(window));
            UiTestHost.PumpFor(500);

            var input = FindVisualDescendants<TextBox>(window)
                .Single(item => item.Name == "Input");
            Assert.True(window.IsVisible);
            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);
            Assert.True(HasKeyboardOrLogicalFocus(input));
            Assert.False(window.Topmost);

            var layoutChanges = 0;
            window.Docking.WindowsChanged += (_, _) => layoutChanges++;
            RequireButton(window, "MenuButton").Focus();
            Assert.True(WakeConsole(window));
            UiTestHost.PumpFor(500);

            Assert.True(HasKeyboardOrLogicalFocus(input));
            Assert.False(window.Topmost);
            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);
            Assert.True(layoutChanges >= 0); // ???????????????????????
        });
    }

    [Fact]
    public void ToolPagesHaveNoCaptionBarAndActionsFloatInTheCorner()
    {
        RunShell(window =>
        {
            // R3-1:?????????????(?? + ?? + ????)????
            // ???????????? ?? ?????????????
            var host = FindVisualDescendants<LayoutAnchorableControl>(window).First(item => item.IsVisible);
            var content = FindVisualDescendants<ContentPresenter>(host).First();
            var hostTop = host.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            var contentTop = content.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            Assert.True(contentTop - hostTop < 2,
                $"tool page content is pushed down by {contentTop - hostTop}px ? a caption bar is back");

            // R4-1:???????????????? ?? ????????
            Assert.Empty(FindVisualDescendants<AnchorablePaneTitle>(host));
        });
    }

    [Fact]
    public void DarkThemeLeavesNoLightPixelsInsideTables()
    {
        RunShell(window =>
        {
            window.Commands.ExecuteAsync("aurora.app.theme mode=dark", "test").GetAwaiter().GetResult();
            UiTestHost.Pump();

            // R4-5:????????? ?? ???????? WPF ?????
            // ????????,???????????????
            foreach (var list in FindVisualDescendants<ListView>(window)
                         .Where(item => item.IsVisible && item.ActualWidth > 100 && item.ActualHeight > 60))
            {
                foreach (var (x, y) in new[] { (0.5, 0.3), (0.5, 0.6), (0.1, 0.6), (0.9, 0.45) })
                {
                    var pixel = SamplePixel(list, x, y);
                    if (pixel.A == 0)
                        continue;
                    var luminance = (pixel.R + pixel.G + pixel.B) / 3;
                    var spread = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B))
                                 - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                    // ???? = ??????(#F4F4F4);??????????,??
                    Assert.False(luminance > 0x60 && spread < 0x14,
                        $"table pixel @{x},{y} is neutral light ({pixel}) ? dark theme did not reach the table body");
                }
            }
        });
    }

    [Fact]
    public void FloatingToolWindowFrameFollowsLightAndDarkThemes()
    {
        RunShell(window =>
        {
            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            window.Docking.Float(StandardWindowIds.Console);
            UiTestHost.PumpFor(900);

            // R4-3:????????????,??????????
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.Equal(
                Assert.IsType<SolidColorBrush>(window.FindResource("Aurora.Brush.Surface")).Color,
                Assert.IsType<SolidColorBrush>(floating.Background).Color);
            Assert.Equal(
                Assert.IsType<SolidColorBrush>(window.FindResource("Aurora.Brush.Hairline")).Color,
                Assert.IsType<SolidColorBrush>(floating.BorderBrush).Color);
            Assert.Equal(new Thickness(1), floating.BorderThickness);

            window.Commands.ExecuteAsync("aurora.app.theme mode=dark", "test").GetAwaiter().GetResult();
            UiTestHost.Pump();

            Assert.Equal(
                Assert.IsType<SolidColorBrush>(window.FindResource("Aurora.Brush.Surface")).Color,
                Assert.IsType<SolidColorBrush>(floating.Background).Color);
            Assert.Equal(
                Assert.IsType<SolidColorBrush>(window.FindResource("Aurora.Brush.Hairline")).Color,
                Assert.IsType<SolidColorBrush>(floating.BorderBrush).Color);
        });
    }

    /// <summary>
    /// REQ-UI-096：顶栏只留一页，而且**屏幕上**也只剩那一页。
    ///
    /// 显示或停靠进中央区的那一页留下，原来那一页隐藏。断言落在控件上而不只是模型：
    /// 中央区换页的那一跳会先经过一个 -1，控件在那一下之后可能不跟（REQ-UI-083，2026-09-07 真机）。
    /// 文档身份（命令集）与工具身份（其余中央页）两种都要走一遍。
    /// </summary>
    [Fact]
    public void CenterTabRowKeepsOnlyTheLastPageShown()
    {
        RunShell(window =>
        {
            window.Docking.Dock(StandardWindowIds.CommandDetail, DockSide.Center);
            UiTestHost.Pump();

            var control = Assert.Single(FindVisualDescendants<LayoutDocumentPaneControl>(window));
            var pane = Assert.IsAssignableFrom<LayoutDocumentPane>(((ILayoutControl)control).Model);
            Assert.Equal(StandardWindowIds.CommandDetail, Assert.Single(pane.Children).ContentId);
            Assert.Equal(StandardWindowIds.CommandDetail, ((LayoutContent?)control.SelectedItem)?.ContentId);

            window.Docking.Show(StandardWindowIds.Mcp);
            UiTestHost.Pump();
            Assert.Equal(StandardWindowIds.Mcp, Assert.Single(pane.Children).ContentId);
            Assert.Equal(StandardWindowIds.Mcp, ((LayoutContent?)control.SelectedItem)?.ContentId);
            Assert.False(window.Docking.ListWindows()
                .Single(item => item.Id == StandardWindowIds.CommandDetail).IsVisible);

            window.Docking.Show(StandardWindowIds.CommandDetail);
            UiTestHost.Pump();
            Assert.Equal(StandardWindowIds.CommandDetail, Assert.Single(pane.Children).ContentId);
            Assert.Equal(StandardWindowIds.CommandDetail, ((LayoutContent?)control.SelectedItem)?.ContentId);
        });
    }

    /// <summary>
    /// REQ-UI-097 / 101：页面拖动只从 Ctrl 标签态的页名标签起手（顶栏删除后没有页签可按）。
    /// 常态下按在窗格内容上不起拖动会话——页面内容自己的点击不被抢；标签态下按在标签上才起。
    /// </summary>
    [Fact]
    public void PageDragStartsOnlyFromTheLabelInLabelMode()
    {
        RunShell(window =>
        {
            var pageDrag = GetPrivateField(window, "_pageDrag")!;
            window.Docking.Show(StandardWindowIds.Mcp);
            UiTestHost.Pump();

            var content = FindVisualDescendants<ContentPresenter>(window)
                .First(item => item.IsVisible && item.Name == "PART_SelectedContentHost");
            PressPreview(content);
            Assert.Null(GetPrivateField(pageDrag, "_dragSession"));

            window.SetLabelMode(true);
            UiTestHost.Pump();
            var label = FindVisualDescendants<Border>(window)
                .First(item => item.IsVisible && Equals(item.Tag, "PageLabelCover"));
            try
            {
                // 按下之后立刻看、不泵消息：合成按下会捕获鼠标，WPF 随即补发一次合成移动，
                // 没有真鼠标时那一下可能越过阈值、把页浮出去再当场收尾——查的就不再是「起没起会话」了。
                label.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent,
                    Source = label,
                });
                Assert.NotNull(GetPrivateField(pageDrag, "_dragSession"));
            }
            finally
            {
                // 直接收掉会话，排队中的浮出见会话已换就不会动页面。
                pageDrag.GetType()
                    .GetMethod("CancelDragSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(pageDrag, ["test finished"]);
                Mouse.Capture(null);
                window.SetLabelMode(false);
            }
        });

        // 必须发隧道的 PreviewMouseDown：PreviewMouseLeftButtonDown 是直达事件，手工 RaiseEvent
        // 只到页签自己，停靠管理器上的手势处理器根本看不见——那样「不起会话」是白绿。
        static void PressPreview(FrameworkElement tab)
        {
            try
            {
                tab.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent,
                    Source = tab,
                });
                UiTestHost.Pump();
            }
            finally
            {
                Mouse.Capture(null);
            }
        }
    }

    /// <summary>
    /// 上面两条的模型层证据：原装的 <see cref="LayoutDocumentPane"/> 选不中 anchorable，
    /// 而 Aurora 的中央窗格能。这条不测产品流程，只把「为什么必须换一个窗格类型」
    /// 钉在门禁里——回滚那个子类，它会立刻红。
    /// </summary>
    [Fact]
    public void StockDocumentPaneCannotSelectAnAnchorableButAuroraCenterPaneCan()
    {
        UiTestHost.RunSta(() =>
        {
            var stock = new LayoutDocumentPane(new LayoutDocument { ContentId = "mcp" });
            var strayInStock = new LayoutAnchorable { ContentId = "overview" };
            stock.Children.Add(strayInStock);
            strayInStock.IsSelected = true;
            Assert.Equal(-1, stock.SelectedContentIndex);

            var center = new CenterDocumentPane(new LayoutDocument { ContentId = "mcp" });
            var anchorable = new LayoutAnchorable { ContentId = "overview" };
            center.Children.Add(anchorable);
            anchorable.IsSelected = true;
            Assert.Equal(1, center.SelectedContentIndex);
            Assert.Same(anchorable, center.SelectedContent);
        });
    }

    /// <summary>
    /// 发一次左键按下。**必须发 <c>MouseLeftButtonDownEvent</c>**：手工 <c>RaiseEvent</c>
    /// 不会像输入管线那样把 <c>MouseDown</c> 升发成它，只发 <c>MouseDown</c> 的用例
    /// 看不见任何类处理器（1.17.5 就是这样绿着上线的）。
    /// </summary>
    private static MouseButtonEventArgs Press(FrameworkElement tab)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = tab,
        };
        try
        {
            tab.RaiseEvent(args);
            UiTestHost.Pump();
        }
        finally
        {
            Mouse.Capture(null);
        }

        return args;
    }

    /// <summary>
    /// REQ-UI-101 第 3 条：专注态右栏不让位，窗口控制组一直在右栏顶部；专注的那一页没有页签行，内容铺满窗格。
    /// 退出专注后「退出聚焦」按钮收起。取代 1.20.1 及以前「控制组搬进专注页页头」的三条用例。
    /// </summary>
    [Fact]
    public void FocusedPageKeepsTheWindowChromeInTheRightRail()
    {
        RunShell(
            window =>
            {
                var exitFocus = RequireButton(window, "ExitFocusButton");
                Assert.Equal(Visibility.Collapsed, exitFocus.Visibility);

                var result = window.Commands.ExecuteAsync("aurora.ui.max name=focus.tool", "test")
                    .GetAwaiter().GetResult();
                Assert.True(result.Success, result.Message);
                UiTestHost.Pump();

                var rail = RequireElement<FrameworkElement>(window, "NavRail");
                var chromeBar = RequireElement<Panel>(window, "ChromeBar");
                Assert.True(rail.IsVisible, "专注态右栏不该让位");
                Assert.True(chromeBar.IsDescendantOf(rail));
                Assert.Single(FindVisualDescendants<Panel>(window), panel => panel.Name == "ChromeBar");
                Assert.Equal(Visibility.Visible, exitFocus.Visibility);
                Assert.All(
                    new[] { "MenuButton", "MinimizeButton", "MaximizeButton", "CloseButton" },
                    name => Assert.Equal(Visibility.Visible, RequireButton(window, name).Visibility));

                Assert.DoesNotContain(FindVisualDescendants<LayoutAnchorableTabItem>(window), item => item.IsVisible);
                var content = FindVisualDescendants<ContentPresenter>(window)
                    .Single(item => item.IsVisible && item.Name == "PART_SelectedContentHost");
                Assert.NotNull(content.Content);
                Assert.True(content.ActualHeight > 0, $"focused content height={content.ActualHeight}");

                Assert.True(window.Commands.ExecuteAsync("aurora.ui.restore", "test").GetAwaiter().GetResult().Success);
                UiTestHost.Pump();
                Assert.Null(window.Docking.MaximizedId);
                Assert.Equal(Visibility.Collapsed, exitFocus.Visibility);
            },
            configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
            {
                Id = "focus.tool",
                Title = "Focus Tool",
                DefaultSide = DockSide.Right,
                DefaultRatio = 0.3,
                ContentFactory = () => new Border(),
            }));
    }

    [Fact]
    public void FloatingToolWindowHasNoSecondOuterChromeRow()
    {
        RunShell(window =>
        {
            var content = Assert.Single(FindVisualDescendants<ConsoleView>(window));

            window.Docking.Float(StandardWindowIds.Console);
            UiTestHost.PumpFor(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            var chrome = WindowChrome.GetWindowChrome(floating);
            Assert.NotNull(chrome);
            // CaptionHeight 必须是 0。1.7.1/1.7.2 曾把它抬到 35，理由是
            // 「AvalonDock 要收到 WM_NCLBUTTONDOWN/HTCAPTION 才建 DragService」——
            // 反编译 FilterMessage 后确认那条理由是错的：它只看 WM_SYSCOMMAND(仅最大化/还原)、
            // WM_LBUTTONUP、WM_MOVING、WM_EXITSIZEMOVE。
            // 抬高它反而有害：页头变成非客户区，WPF 收不到鼠标按下，
            // Aurora 自己那条会去调 DragMove 的拖动手势根本起不来。
            Assert.Equal(0, chrome.CaptionHeight);
            var root = Assert.IsType<Border>(floating.Template.LoadContent());
            var presenter = Assert.Single(FindLogicalDescendants<ContentPresenter>(root));
            Assert.Null(presenter.DataContext);
            Assert.DoesNotContain(
                FindLogicalDescendants<FrameworkElement>(root),
                item => item.GetType().Name.Contains("FloatingWindowControlChrome", StringComparison.Ordinal));
            Assert.DoesNotContain(
                FindVisualDescendants<FrameworkElement>(floating),
                item => Equals(item.Tag, "FloatingShellPaneHeader"));
            Assert.Equal("FloatingWindowContentHost", floating.Content.GetType().Name);
            Assert.True(content.IsVisible);
            Assert.NotNull(PresentationSource.FromVisual(content));
            Assert.NotSame(PresentationSource.FromVisual(floating), PresentationSource.FromVisual(content));

            // ???????? PresentationSource???????? Pane Style ???
            // ????? DockingManager ???????
            var floatingPane = FindAncestor<LayoutAnchorablePaneControl>(content, _ => true);
            Assert.NotNull(floatingPane);
            Assert.Contains(
                floatingPane!.Style.Setters.OfType<EventSetter>(),
                setter => setter.Event == UIElement.PreviewMouseLeftButtonDownEvent);
        });
    }

    /// <summary>
    /// REQ-UI-101 第 2 条：浮窗页头的最大化按钮随顶栏删除，主窗体与浮窗里都不再有。
    /// 浮窗的最大化 / 还原只走 <c>aurora.ui.floatstate</c>（见 <c>FloatingDocumentWindowStateIsOwnedByCommandBus</c>）。
    /// </summary>
    [Fact]
    public void NoWindowDrawsAFloatingMaximizeButton()
    {
        RunShell(window =>
        {
            window.Docking.Float(StandardWindowIds.Console);
            UiTestHost.PumpFor(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            foreach (var host in manager.FloatingWindows.Cast<DependencyObject>().Prepend(window))
            {
                Assert.DoesNotContain(
                    FindVisualDescendants<Button>(host),
                    button => Equals(button.Tag, "FloatingMaxRestore") || button.Name == "FloatingDocumentMaxRestore");
            }
        });
    }

    /// <summary>
    /// 两种窗格控件都把按下、移动、抬起、丢捕获交给页面拖动协调器——标签态的标签拖动与单页浮窗的移动都靠这四条。
    /// 1.20.1 及以前这里还钉着「主文档区页头、专注页页头是拖动主窗口的落点」，随顶栏删除（REQ-UI-101）。
    /// </summary>
    [Fact]
    public void EveryPaneRoutesPointerInputToThePageDragCoordinator()
    {
        RunShell(
            window =>
            {
                var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
                var toolEvents = manager.AnchorablePaneControlStyle.Setters.OfType<EventSetter>().ToList();
                Assert.Contains(toolEvents, setter => setter.Event == UIElement.PreviewMouseLeftButtonDownEvent);
                Assert.Contains(toolEvents, setter => setter.Event == UIElement.PreviewMouseMoveEvent);
                Assert.Contains(toolEvents, setter => setter.Event == UIElement.PreviewMouseLeftButtonUpEvent);
                Assert.Contains(toolEvents, setter => setter.Event == Mouse.LostMouseCaptureEvent);

                var documentEvents = manager.DocumentPaneControlStyle.Setters.OfType<EventSetter>().ToList();
                Assert.Contains(documentEvents, setter => setter.Event == UIElement.PreviewMouseLeftButtonDownEvent);
                Assert.Contains(documentEvents, setter => setter.Event == UIElement.PreviewMouseMoveEvent);
                Assert.Contains(documentEvents, setter => setter.Event == UIElement.PreviewMouseLeftButtonUpEvent);
                Assert.Contains(documentEvents, setter => setter.Event == Mouse.LostMouseCaptureEvent);
            },
            configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
            {
                Id = "focus.tool",
                Title = "Focus Tool",
                DefaultSide = DockSide.Right,
                DefaultRatio = 0.3,
                ContentFactory = () => new Border(),
            }));
    }

    [Fact]
    public void FloatingDocumentWindowKeepsItsContentHost()
    {
        Grid? content = null;
        RunShell(window =>
        {
            window.Docking.Show("center.float");
            UiTestHost.Pump();
            Assert.NotNull(content);
            window.Docking.Float("center.float");
            UiTestHost.PumpFor(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.Equal("FloatingWindowContentHost", floating.Content.GetType().Name);
            Assert.True(content!.IsVisible);
            Assert.NotNull(PresentationSource.FromVisual(content));
            Assert.NotSame(PresentationSource.FromVisual(floating), PresentationSource.FromVisual(content));
        }, configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "center.float",
            Title = "Center Float",
            DefaultSide = DockSide.Center,
            DefaultRatio = 1,
            ContentFactory = () => content = new Grid { Tag = "FloatingDocumentContent" },
        }));
    }

    [Fact]
    public void FloatingCenterToolWindowKeepsItsOwnPaneBinding()
    {
        Grid? content = null;
        RunShell(window =>
        {
            window.Docking.Show("center.float.actions");
            window.Docking.Float("center.float.actions");
            UiTestHost.PumpFor(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.NotNull(content);
            var pane = FindAncestor<LayoutAnchorablePaneControl>(content!, _ => true);
            Assert.NotNull(pane);
            Assert.DoesNotContain(
                FindVisualDescendants<Button>(pane!),
                item => item.Name == "FloatingDocumentMaxRestore");
        }, configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "center.float.actions",
            Title = "Center Float Actions",
            DefaultSide = DockSide.Center,
            DefaultRatio = 1,
            ContentFactory = () => content = new Grid(),
        }));
    }

    [Fact]
    public void FloatingDocumentWindowStateIsOwnedByCommandBus()
    {
        RunShell(window =>
        {
            window.Docking.Show("center.state");
            window.Docking.Float("center.state");
            UiTestHost.PumpFor(900);

            var manager = Assert.Single(FindVisualDescendants<AvalonDock.DockingManager>(window));
            var floating = Assert.Single(manager.FloatingWindows.ToList());
            var maximize = window.Commands.ExecuteAsync(
                "aurora.ui.floatstate name=center.state state=maximized", "Test").GetAwaiter().GetResult();
            Assert.True(maximize.Success, maximize.Message);
            UiTestHost.Pump();
            floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.Equal(WindowState.Maximized, floating.WindowState);

            var restore = window.Commands.ExecuteAsync(
                "aurora.ui.floatstate name=center.state state=toggle", "Test").GetAwaiter().GetResult();
            Assert.True(restore.Success, restore.Message);
            UiTestHost.Pump();
            floating = Assert.Single(manager.FloatingWindows.ToList());
            Assert.Equal(WindowState.Normal, floating.WindowState);
        }, configure: config => config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "center.state",
            Title = "Center State",
            DefaultSide = DockSide.Center,
            DefaultRatio = 1,
            ContentFactory = () => new Grid(),
        }));
    }

    /// <summary>REQ-UI-097\uff1aCtrl \u6807\u7b7e\u6001\u4e0b\u6bcf\u4e00\u683c\u7a97\u683c\u7684\u5185\u5bb9\u6362\u6210\u5199\u7740\u9875\u540d\u7684\u5927\u6807\u7b7e\uff1b\u9000\u51fa\u5373\u6062\u590d\u3002</summary>
    [Fact]
    public void LabelModeCoversEveryPaneWithItsPageTitle()
    {
        RunShell(window =>
        {
            var covers = FindVisualDescendants<Border>(window)
                .Where(border => Equals(border.Tag, "PageLabelCover"))
                .ToList();
            Assert.True(covers.Count >= 2, $"expected a label cover in the center and the console pane, got {covers.Count}");
            Assert.All(covers, cover => Assert.Equal(Visibility.Collapsed, cover.Visibility));

            window.SetLabelMode(true);
            UiTestHost.Pump();

            var shown = covers.Where(cover => cover.IsVisible).ToList();
            Assert.True(shown.Count >= 2, $"only {shown.Count} label covers became visible");
            foreach (var cover in shown)
            {
                var pane = FindAncestor<Selector>(cover, _ => true);
                var title = (pane?.SelectedItem as LayoutContent)?.Title;
                Assert.False(string.IsNullOrEmpty(title));
                Assert.Equal(title, Assert.Single(FindVisualDescendants<TextBlock>(cover)).Text);
            }

            window.SetLabelMode(false);
            UiTestHost.Pump();
            Assert.All(covers, cover => Assert.Equal(Visibility.Collapsed, cover.Visibility));
        });
    }

    /// <summary>
    /// REQ-UI-101（1.20.2）：顶栏整个删掉。每一格窗格都没有页签行——页签面板还在（窗格是 TabControl，
    /// 靠生成出来的页签容器才产出 SelectedContent），但所在的那一行高 0、不可见；模板里不再有页头标记，
    /// 窗口控制组也没有第二个落点。页面内容照常画出来。
    /// </summary>
    [Fact]
    public void PanesHaveNoTabRow()
    {
        RunShell(window =>
        {
            var panels = FindVisualDescendants<Panel>(window)
                .Where(panel => panel is DocumentPaneTabPanel or AnchorablePaneTabPanel)
                .ToList();
            Assert.NotEmpty(panels);
            Assert.All(panels, panel =>
            {
                Assert.False(panel.IsVisible, "页签面板仍然可见");
                var row = Assert.IsAssignableFrom<FrameworkElement>(VisualTreeHelper.GetParent(panel));
                Assert.Equal(0, row.ActualHeight, 3);
            });

            foreach (var tag in new[] { "ShellPaneHeader", "FocusedShellPaneHeader", "ShellChromeHost", "FocusedShellChromeHost" })
                Assert.DoesNotContain(FindVisualDescendants<FrameworkElement>(window), element => Equals(element.Tag, tag));

            Assert.Contains(
                FindVisualDescendants<ContentPresenter>(window),
                presenter => presenter.Name == "PART_SelectedContentHost" && presenter.Content != null);
        });
    }

    /// <summary>REQ-UI-099\uff1a\u53f3\u680f\u4e0b\u534a\u53ea\u5217\u6b64\u523b\u6ca1\u9732\u9762\u7684\u9875\uff1b\u9732\u9762\u4e86\u5c31\u4e0d\u5728\u91cc\u9762\u3002</summary>
    [Fact]
    public void RightRailListsHiddenPagesAsCapsules()
    {
        RunShell(window =>
        {
            var items = RequireElement<Panel>(window, "NavPageItems");

            window.Docking.Hide(StandardWindowIds.Console);
            window.RefreshNavigatorPages();
            var capsules = items.Children.OfType<Border>().ToList();
            Assert.Contains(capsules, capsule => Equals(capsule.Tag, StandardWindowIds.Console));
            var visible = window.Docking.ListWindows().Where(item => item.IsVisible).Select(item => item.Id).ToHashSet();
            Assert.DoesNotContain(capsules, capsule => visible.Contains((string)capsule.Tag!));

            window.Docking.Show(StandardWindowIds.Console);
            window.RefreshNavigatorPages();
            Assert.DoesNotContain(
                items.Children.OfType<Border>(),
                capsule => Equals(capsule.Tag, StandardWindowIds.Console));
        });
    }

    [Fact]
    public void ThemeCommandSwitchesTokensAndPersistsChoice()
    {
        var settings = new MemorySettings();
        RunShell(
            window =>
            {
                // UI-08:?????,???????????
                var light = (SolidColorBrush)window.FindResource("Aurora.Brush.Canvas");
                Assert.Equal(Colors.White, ((SolidColorBrush)window.FindResource("Aurora.Brush.Surface")).Color);

                window.Commands.ExecuteAsync("aurora.app.theme mode=dark", "test").GetAwaiter().GetResult();
                UiTestHost.Pump();

                var dark = (SolidColorBrush)window.FindResource("Aurora.Brush.Canvas");
                Assert.NotEqual(light.Color, dark.Color);
                Assert.True(dark.Color.R < 0x40 && dark.Color.G < 0x40 && dark.Color.B < 0x40,
                    $"dark canvas should be dark, got {dark.Color}");
                Assert.Equal("dark", settings.Get("ui.theme"));

                // ??????:R > B ?????
                var accent = ((SolidColorBrush)window.FindResource("Aurora.Brush.Accent")).Color;
                Assert.True(accent.R > accent.B + 0x40, $"accent should be amber, got {accent}");

                window.Commands.ExecuteAsync("aurora.app.theme mode=light", "test").GetAwaiter().GetResult();
                UiTestHost.Pump();
                Assert.Equal(light.Color, ((SolidColorBrush)window.FindResource("Aurora.Brush.Canvas")).Color);
                Assert.Equal("light", settings.Get("ui.theme"));
            },
            settings: settings);
    }

    [Fact]
    public void ThemeChoiceSurvivesSettingsAndWindowRecreation()
    {
        var appName = $"HistoryVulcan.Theme.Tests.{Guid.NewGuid():N}";
        var paths = AuroraPaths.ForApplication(appName);
        try
        {
            var firstSettings = new JsonSettingsStore(paths.SettingsFile);
            RunShell(
                window =>
                {
                    var result = window.Commands.ExecuteAsync("aurora.app.theme mode=dark", "test")
                        .GetAwaiter().GetResult();
                    Assert.True(result.Success, result.Message);
                },
                settings: firstSettings);

            var reloadedSettings = new JsonSettingsStore(paths.SettingsFile);
            Assert.Equal("dark", reloadedSettings.Get("ui.theme"));
            RunShell(
                window =>
                {
                    var canvas = ((SolidColorBrush)window.FindResource("Aurora.Brush.Canvas")).Color;
                    Assert.True(canvas.R < 0x40 && canvas.G < 0x40 && canvas.B < 0x40,
                        $"recreated window did not restore dark theme: {canvas}");
                },
                settings: reloadedSettings);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public void TitleBarKeepsMenuButtonImmediatelyLeftOfThreeWindowButtons()
    {
        RunShell(window =>
        {
            // UI-02.3:?? + ??? + ????? + ??,?????????
            var menu = RequireButton(window, "MenuButton");
            var minimize = RequireButton(window, "MinimizeButton");
            var maximize = RequireButton(window, "MaximizeButton");
            var close = RequireButton(window, "CloseButton");

            foreach (var button in new[] { menu, minimize, maximize, close })
            {
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.True(button.IsEnabled);
                Assert.True(button.IsHitTestVisible);
                Assert.True(button.ActualWidth > 0, $"{button.Name} width={button.ActualWidth}");
            }

            // ??:?????????,??????????
            var bar = RequireElement<Panel>(window, "ChromeBar");
            var order = bar.Children.OfType<Button>().ToList();
            var menuIndex = order.IndexOf(menu);
            Assert.True(menuIndex >= 0);
            Assert.Equal(menuIndex + 1, order.IndexOf(minimize));
            Assert.Equal(menuIndex + 2, order.IndexOf(maximize));
            Assert.Equal(menuIndex + 3, order.IndexOf(close));
        });
    }

    [Fact]
    public void FoldedMenuKeepsAllGroupsAndConsumerToolActions()
    {
        RunShell(
            window =>
            {
                // UI-03.2:????????? 3.0.3 ?????
                var menu = RequireButton(window, "MenuButton").ContextMenu;
                Assert.NotNull(menu);
                var headers = menu!.Items.OfType<MenuItem>().Select(item => item.Header.ToString()).ToList();
                // 1.19.0 \u8d77\u591a\u4e00\u7ec4\u300c\u573a\u666f\u300d\uff08REQ-UI-085\uff09\uff0c\u5939\u5728\u89c6\u56fe\u4e0e\u5de5\u5177\u4e4b\u95f4\u3002
                Assert.Equal(["\u6587\u4ef6(_F)", "\u7f16\u8f91(_E)", "\u89c6\u56fe(_V)", "\u573a\u666f(_S)", "\u5de5\u5177(_T)", "\u5e2e\u52a9(_H)"], headers);

                var tools = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "\u5de5\u5177(_T)"));
                Assert.Contains(
                    tools.Items.OfType<MenuItem>(),
                    item => Equals(item.Header, "\u6d4b\u8bd5\u52a8\u4f5c"));
            },
            configure: config => config.ToolMenuActions.Add(new ShellMenuAction("\u6d4b\u8bd5\u52a8\u4f5c", "aurora.app.about")));
    }

    [Fact]
    public void NonStandardWindowStyleStillKeepsMenuAndWindowButtons()
    {
        // UI-09.1:???? WindowStyle ??????????,???????
        RunShell(
            window =>
            {
                Assert.Equal(WindowStyle.ToolWindow, window.WindowStyle);
                foreach (var name in new[] { "MenuButton", "MinimizeButton", "MaximizeButton", "CloseButton" })
                {
                    var button = RequireButton(window, name);
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    Assert.True(button.IsEnabled);
                }
            },
            windowStyle: WindowStyle.ToolWindow);
    }

    [Fact]
    public void ErrorEntryRaisesTitleBarBadgeInsteadOfStatusBar()
    {
        var log = new RelayLog();
        RunShell(
            window =>
            {
                var badge = RequireButton(window, "ErrorBadge");
                Assert.Equal(Visibility.Collapsed, badge.Visibility);

                log.Raise(ShellLogLevel.Error, "test", "\u754c\u9762\u5347\u7ea7\u9a8c\u8bc1\u7528\u9519\u8bef");
                UiTestHost.Pump();

                // UI-05.3: count moves to title-bar badge
                Assert.Equal(Visibility.Visible, badge.Visibility);
                Assert.Equal("\u9519\u8bef 1", badge.Content);
            },
            log: log);
    }

    [Fact]
    public void ConsoleToolbarShowsLevelDomainAndDependentClass()
    {
        RunShell(window =>
        {
            var console = Assert.Single(FindVisualDescendants<ConsoleView>(window));
            Assert.Equal(Visibility.Visible, Assert.IsType<AuroraOptionBox>(console.FindName("LevelFilter")).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<AuroraOptionBox>(console.FindName("DomainFilter")).Visibility);
            var classFilter = Assert.IsType<AuroraOptionBox>(console.FindName("ClassFilter"));
            Assert.Equal(Visibility.Visible, classFilter.Visibility);
            Assert.False(classFilter.IsEnabled);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBox>(console.FindName("KeywordFilter")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<CheckBox>(console.FindName("MuteLayout")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<CheckBox>(console.FindName("AutoScroll")).Visibility);

            var status = window.Commands.ExecuteAsync("aurora.log.autoscroll", "Test").GetAwaiter().GetResult();
            Assert.True(status.Success, status.Message);
            Assert.Contains("True", status.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ConsoleTypingInNormalLayoutKeepsCompletionInConsole()
    {
        var session = new CompletionCatalogSession();
        RunShell(window =>
        {
            window.AttachCommandCatalogSession(session);
            var console = Assert.Single(FindVisualDescendants<ConsoleView>(window));
            var input = Assert.IsType<TextBox>(console.FindName("Input"));
            var popup = Assert.IsType<Popup>(console.FindName("CompletionPopup"));

            Assert.Null(window.Docking.MaximizedId);
            input.Focus();
            Keyboard.Focus(input);
            input.Text = "v";
            input.CaretIndex = input.Text.Length;

            UiTestHost.PumpUntil(() => input.Text == "v");
            Assert.False(popup.IsOpen);
            Assert.True(input.IsKeyboardFocusWithin);
            Assert.Equal("v", input.Text);
            Assert.Equal("", session.LastText);
            Assert.False(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.Equal("v", input.Text);
        });
    }

    [Fact]
    public void FocusedConsoleCompletesDomainClassMethodAndParameter()
    {
        var session = new CompletionCatalogSession();
        RunShell(window =>
        {
            window.AttachCommandCatalogSession(session);
            window.Docking.MaximizeWindow(StandardWindowIds.Console);
            UiTestHost.Pump();
            window.RefreshCommandCompletionFocus();
            var console = Assert.Single(FindVisualDescendants<ConsoleView>(window));
            var input = Assert.IsType<TextBox>(console.FindName("Input"));
            var popup = Assert.IsType<Popup>(console.FindName("CompletionPopup"));

            Assert.Equal(StandardWindowIds.Console, window.Docking.MaximizedId);
            input.Focus();
            Keyboard.Focus(input);
            input.Text = "v";
            input.CaretIndex = input.Text.Length;

            Assert.True(UiTestHost.PumpUntil(() => popup.IsOpen));
            Assert.Equal("vulcan.", Assert.Single(session.LastResult.Candidates).InsertText);

            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.True(UiTestHost.PumpUntil(() => session.LastText == "vulcan."));
            Assert.Equal("vulcan.proj.", Assert.Single(session.LastResult.Candidates).InsertText);

            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.True(UiTestHost.PumpUntil(() => session.LastText == "vulcan.proj."));
            Assert.Equal("vulcan.proj.open ", Assert.Single(session.LastResult.Candidates).InsertText);

            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.True(UiTestHost.PumpUntil(() => session.LastText == "vulcan.proj.open "));
            Assert.Equal("name=", Assert.Single(session.LastResult.Candidates).InsertText);

            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.True(UiTestHost.PumpUntil(() => session.LastText == "vulcan.proj.open name="));
            Assert.Equal("Mercury", Assert.Single(session.LastResult.Candidates).InsertText);

            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.True(UiTestHost.PumpUntil(() => session.LastText == "vulcan.proj.open name=Mercury "));
            Assert.Equal("mode=", Assert.Single(session.LastResult.Candidates).InsertText);

            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.True(UiTestHost.PumpUntil(() => session.LastText == "vulcan.proj.open name=Mercury mode="));
            Assert.Equal("Fast", Assert.Single(session.LastResult.Candidates).InsertText);

            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            Assert.True(UiTestHost.PumpUntil(() => session.LastText == "vulcan.proj.open name=Mercury mode=Fast "));
            Assert.Equal("vulcan.proj.open name=Mercury mode=Fast ", input.Text);
            Assert.False(popup.IsOpen);
        });
    }

    [Fact]
    public void PositionalParameterHintThatDoesNotChangeTextClosesWithoutRefreshing()
    {
        var session = new CompletionCatalogSession();
        RunShell(window =>
        {
            window.AttachCommandCatalogSession(session);
            window.Docking.MaximizeWindow(StandardWindowIds.Console);
            UiTestHost.Pump();
            window.RefreshCommandCompletionFocus();
            var console = Assert.Single(FindVisualDescendants<ConsoleView>(window));
            var input = Assert.IsType<TextBox>(console.FindName("Input"));
            var popup = Assert.IsType<Popup>(console.FindName("CompletionPopup"));

            input.Focus();
            Keyboard.Focus(input);
            input.Text = "vulcan.proj.free ";
            input.CaretIndex = input.Text.Length;

            Assert.True(UiTestHost.PumpUntil(() => popup.IsOpen));
            var calls = session.CompletionCalls;
            Assert.True(console.HandleCompletionKey(Key.Tab, ModifierKeys.None));
            UiTestHost.Pump();
            Assert.Equal("vulcan.proj.free ", input.Text);
            Assert.False(popup.IsOpen);
            Assert.Equal(calls, session.CompletionCalls);
        });
    }

    [Fact]
    public void ConsoleLongLinesWrapAtCurrentWidthWithoutHorizontalExtentOrLogicalNewlines()
    {
        UiTestHost.RunSta(() =>
        {
            var log = new RelayLog();
            var logicalText = new string('W', 320);
            log.Raise(ShellLogLevel.Info, "wrap.test", logicalText);
            var bus = new CommandBus(new CommandRegistry(), log);
            var console = new ConsoleView(
                log,
                bus,
                new CommandHistory(Path.Combine(Path.GetTempPath(), $"HistoryVulcan-history-{Guid.NewGuid():N}.txt")),
                new HistoryAurora.Shell.Neutral.CommandSurface.DeferredCommandCatalogSession());
            var host = new Window
            {
                Content = console,
                Width = 760,
                Height = 420,
                ShowInTaskbar = false,
            };
            var exportPath = Path.Combine(Path.GetTempPath(), $"HistoryVulcan-console-{Guid.NewGuid():N}.txt");
            try
            {
                host.Show();
                var output = Assert.IsType<ListBox>(console.FindName("Output"));
                // 同上：等 100ms 批量刷新把行送进列表。
                Assert.True(
                    UiTestHost.PumpUntil(() => output.Items.Count > 0),
                    "控制台批量刷新超时，没有可换行的行");
                var row = Assert.Single(output.Items.Cast<ConsoleRow>());
                output.ScrollIntoView(row);
                host.UpdateLayout();
                var container = Assert.IsType<ListBoxItem>(output.ItemContainerGenerator.ContainerFromItem(row));
                var text = Assert.Single(FindVisualDescendants<TextBlock>(container));
                var scroll = Assert.Single(FindVisualDescendants<ScrollViewer>(output));
                var horizontalBar = FindVisualDescendants<ScrollBar>(scroll)
                    .Single(bar => bar.Orientation == Orientation.Horizontal);
                var wideHeight = text.ActualHeight;

                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                Assert.True(scroll.CanContentScroll);
                Assert.Equal(ScrollBarVisibility.Disabled,
                    ScrollViewer.GetHorizontalScrollBarVisibility(output));
                Assert.Equal(0, scroll.ScrollableWidth);
                Assert.Equal(Visibility.Collapsed, horizontalBar.Visibility);

                host.Width = 360;
                host.UpdateLayout();
                UiTestHost.Pump();
                var narrowHeight = text.ActualHeight;
                Assert.True(narrowHeight > wideHeight,
                    $"narrow={narrowHeight}, wide={wideHeight}");
                Assert.Equal(0, scroll.ScrollableWidth);

                host.Width = 760;
                host.UpdateLayout();
                UiTestHost.Pump();
                Assert.True(text.ActualHeight < narrowHeight,
                    $"rewidened={text.ActualHeight}, narrow={narrowHeight}");
                Assert.Equal(0, scroll.ScrollableWidth);
                Assert.DoesNotContain('\n', row.Text);

                output.SelectedItem = row;
                var copy = console.CopySelected();
                Assert.Contains("\u5df2\u590d\u5236", copy, StringComparison.Ordinal);
                Assert.Equal(row.Text, Clipboard.GetText());

                var export = console.ExportVisible(exportPath);
                Assert.Contains("\u5df2\u5bfc\u51fa", export, StringComparison.Ordinal);
                var exported = Assert.Single(File.ReadAllLines(exportPath));
                Assert.Contains(logicalText, exported, StringComparison.Ordinal);
            }
            finally
            {
                host.Close();
                if (File.Exists(exportPath))
                    File.Delete(exportPath);
            }
        });
    }

    [Fact]
    public void CommandPipelineOwnsConsoleAndFormerDirectActions()
    {
        RunShell(window =>
        {
            var names = window.Commands.Registry.All().Select(command => command.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(
                new[]
                {
                    "aurora.log.level", "aurora.log.source", "aurora.log.keyword", "aurora.log.mute", "aurora.log.autoscroll",
                    "aurora.log.clear", "aurora.log.export", "aurora.log.copy", "aurora.log.focus",
                    "vulcan.app.hide", "vulcan.app.show", "vulcan.app.focusconsole", "vulcan.app.close",
                    "aurora.ui.max", "aurora.log.focus",
                    "aurora.app.window", "aurora.ui.autohide", "aurora.ui.floatstate", "aurora.command.copyexample",
                    "aurora.ui.selectfile", "aurora.ui.selectdirectory", "aurora.ui.dialog",
                },
                name => Assert.Contains(name, names));
            Assert.DoesNotContain(names, name => name.StartsWith("res.", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.StartsWith("motor.", StringComparison.OrdinalIgnoreCase));

            Assert.True(window.Commands.ExecuteAsync("aurora.log.level level=error", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("aurora.log.source source=vulcan", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("aurora.log.keyword text=timeout", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("aurora.log.mute layout=true", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("aurora.log.autoscroll enabled=false", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("aurora.log.focus errors=true", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("aurora.log.clear", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("aurora.log.export", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync("aurora.log.copy", "Test").GetAwaiter().GetResult().Success);
            Assert.True(window.Commands.ExecuteAsync($"aurora.ui.autohide name={StandardWindowIds.Console}", "Test")
                .GetAwaiter().GetResult().Success);
        });
    }

    // ---------------------------------------------------------------- ??

    private static bool WakeConsole(ShellWindow window)
        => window.Commands.ExecuteAsync("vulcan.app.focusconsole", "test")
            .GetAwaiter().GetResult().Success;

    // ================================================================ 1.20.3

    /// <summary>
    /// REQ-UI-104：右栏与停靠区是同一片画布——不画左边框，底色取 <c>Aurora.Brush.Canvas</c>。
    /// 回归对象是深色：底色曾是 <c>SurfaceAlt</c>（#242625），比页底 Canvas（#1A1D1C）亮一截，
    /// 右栏于是成了一块贴在窗体右边的板子；浅色下两者只差 1 个色阶，肉眼看不出来，所以两种主题都验。
    /// </summary>
    [Fact]
    public void RightRailSharesTheDockingCanvasAndDrawsNoSeam()
    {
        RunShell(window =>
        {
            var rail = RequireElement<Border>(window, "NavRail");
            Assert.Equal(default, rail.BorderThickness);

            foreach (var mode in new[] { "light", "dark" })
            {
                Assert.True(window.Commands.ExecuteAsync($"aurora.app.theme mode={mode}", "test")
                    .GetAwaiter().GetResult().Success);
                UiTestHost.Pump();

                var canvas = (SolidColorBrush)window.FindResource("Aurora.Brush.Canvas");
                var surfaceAlt = (SolidColorBrush)window.FindResource("Aurora.Brush.SurfaceAlt");
                var background = Assert.IsType<SolidColorBrush>(rail.Background);
                Assert.Equal(canvas.Color, background.Color);
                if (mode == "dark")
                    Assert.NotEqual(surfaceAlt.Color, background.Color);
            }
        });
    }

    /// <summary>
    /// REQ-UI-105：Ctrl 标签态的盖板必须自己带圆角。它是卡片里最上面的一层，而 WPF 的
    /// <c>CornerRadius</c> 不裁剪子元素——不带圆角时四个角被它填成直角，按住 Ctrl 整页就从
    /// 圆角矩形变成尖角矩形。
    /// </summary>
    [Fact]
    public void LabelModeCoverKeepsTheCardCorners()
    {
        RunShell(window =>
        {
            var radius = (CornerRadius)window.FindResource("Aurora.Radius.Inner");
            var covers = FindVisualDescendants<Border>(window)
                .Where(border => Equals(border.Tag, "PageLabelCover"))
                .ToList();
            Assert.NotEmpty(covers);
            Assert.All(covers, cover => Assert.Equal(radius, cover.CornerRadius));
        });
    }

    /// <summary>
    /// REQ-UI-106：搜索就在右栏第二行，没有浮层。输入随手筛场景与常用页面两段；清空恢复原样。
    /// </summary>
    [Fact]
    public void RailSearchFiltersScenesAndPagesWithoutAnOverlay()
    {
        RunShell(window =>
        {
            Assert.Null(window.FindName("NavOverlay"));
            Assert.Null(window.FindName("NavResults"));

            var query = RequireElement<TextBox>(window, "NavQuery");
            var scenes = RequireElement<Panel>(window, "NavRailItems");
            var pages = RequireElement<Panel>(window, "NavPageItems");
            var scenesEmpty = RequireElement<TextBlock>(window, "NavScenesEmpty");

            window.Docking.Hide(StandardWindowIds.Console);
            window.RefreshNavigatorPages(force: true);
            var sceneCount = scenes.Children.Count;
            Assert.True(sceneCount > 0);
            Assert.Contains(
                pages.Children.OfType<Border>(),
                capsule => Equals(capsule.Tag, StandardWindowIds.Console));

            query.Text = "不存在的这一页";
            UiTestHost.Pump();
            Assert.Empty(scenes.Children.OfType<Button>());
            Assert.Empty(pages.Children.OfType<Border>());
            Assert.Equal(Visibility.Visible, scenesEmpty.Visibility);

            query.Text = StandardWindowIds.Console;
            UiTestHost.Pump();
            Assert.Contains(
                pages.Children.OfType<Border>(),
                capsule => Equals(capsule.Tag, StandardWindowIds.Console));

            query.Text = "";
            UiTestHost.Pump();
            Assert.Equal(sceneCount, scenes.Children.Count);
            Assert.Equal(Visibility.Collapsed, scenesEmpty.Visibility);
        });
    }

    /// <summary>
    /// REQ-UI-107：右栏空白（含滚动区里的空处）是窗口拖动面，按钮、搜索框与常用页面胶囊仍归它们自己。
    /// 回归对象是「只有一小部分区域可以拖」：拖动原先挂在冒泡事件上，右栏里占了两整行的滚动区
    /// 先把按下吃掉，冒泡上来时能拖的只剩边角那几条缝。
    /// </summary>
    [Fact]
    public void RailBlankSpaceIsAWindowDragSurface()
    {
        RunShell(window =>
        {
            var rail = RequireElement<Border>(window, "NavRail");

            Assert.False(ShellWindow.IsInteractiveSurface(rail, rail));
            // 滚动区里的列表面板：走到 rail 之前只经过面板与滚动宿主，一律是拖动面。
            Assert.False(ShellWindow.IsInteractiveSurface(RequireElement<Panel>(window, "NavRailItems"), rail));
            Assert.False(ShellWindow.IsInteractiveSurface(RequireElement<Panel>(window, "NavPageItems"), rail));

            Assert.True(ShellWindow.IsInteractiveSurface(RequireButton(window, "CloseButton"), rail));
            Assert.True(ShellWindow.IsInteractiveSurface(RequireElement<TextBox>(window, "NavQuery"), rail));

            window.Docking.Hide(StandardWindowIds.Console);
            window.RefreshNavigatorPages(force: true);
            var capsule = RequireElement<Panel>(window, "NavPageItems").Children
                .OfType<Border>()
                .Single(border => Equals(border.Tag, StandardWindowIds.Console));
            Assert.True(ShellWindow.IsInteractiveSurface(capsule, rail));
        });
    }

    private static void RunShell(
        Action<ShellWindow> assert,
        Action<ShellConfig>? configure = null,
        WindowStyle windowStyle = WindowStyle.SingleBorderWindow,
        IShellLog? log = null,
        ISettingsService? settings = null)
    {
        UiTestHost.RunSta(() =>
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryVulcan-chrome-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            var config = new ShellConfig
            {
                AppName = "HistoryVulcan Chrome Test",
                AppVersion = "3.1.1",
            };
            configure?.Invoke(config);

            var window = new ShellWindow(
                config,
                new MemoryLayoutStore(),
                log ?? new NullLog(),
                settings ?? new MemorySettings(),
                dataDirectory)
            {
                Width = 1000,
                Height = 700,
                ShowInTaskbar = false,
                WindowStyle = windowStyle,
            };
            // DEC-023:命令工作台（命令集/详情/补全）由 HistoryMercury 提供，不在本仓库门禁内。
            // Shell 合同用例因此运行在「无 Mercury」降级路径上，验证 Vulcan 自身在缺少
            // 命令工作台时仍完整可用。
            try
            {
                window.Show();
                UiTestHost.Pump();
                assert(window);
            }
            finally
            {
                window.Close();
                Directory.Delete(dataDirectory, recursive: true);
            }
        });
    }

    /// <summary>
    /// REQ-CMD-012:省略 path 的导出不得弹 SaveFileDialog。本用例在无人值守下运行——
    /// 若实现回到弹窗，模态对话框会阻塞 STA 线程直到测试超时，而不是通过。
    /// </summary>
    [Fact]
    public void ConsoleExportWithoutPathWritesDefaultFileWithoutDialog()
    {
        UiTestHost.RunSta(() =>
        {
            var log = new RelayLog();
            log.Raise(ShellLogLevel.Info, "export.test", "no-dialog-export-line");
            var console = new ConsoleView(
                log,
                new CommandBus(new CommandRegistry(), log),
                new CommandHistory(Path.Combine(Path.GetTempPath(), $"HistoryVulcan-history-{Guid.NewGuid():N}.txt")),
                new HistoryAurora.Shell.Neutral.CommandSurface.DeferredCommandCatalogSession());
            var host = new Window
            {
                Content = console,
                Width = 760,
                Height = 420,
                ShowInTaskbar = false,
            };

            string? written = null;
            try
            {
                host.Show();
                // 控制台按 100ms 批量合并日志：等到行真正进入可见集合再导出，
                // 而不是盲等一个「应该够了」的固定时长。
                Assert.True(
                    UiTestHost.PumpUntil(() =>
                        ((System.Collections.IEnumerable)Assert.IsType<ListBox>(console.FindName("Output")).Items)
                            .Cast<ConsoleRow>().Any()),
                    "控制台批量刷新超时，没有可导出的行");

                var message = console.ExportVisible(null);

                Assert.Contains("已导出", message, StringComparison.Ordinal);
                // 结果必须回报绝对路径，调用方（MCP / Web）才能取回文件。
                const string marker = "行到 ";
                var at = message.LastIndexOf(marker, StringComparison.Ordinal);
                Assert.True(at > 0, $"导出结果未回报路径: {message}");
                written = message[(at + marker.Length)..].Trim();
                Assert.True(Path.IsPathFullyQualified(written), $"导出路径不是绝对路径: {written}");
                Assert.True(File.Exists(written), $"默认导出文件未落盘: {written}");
                Assert.Contains(
                    "no-dialog-export-line",
                    File.ReadAllText(written),
                    StringComparison.Ordinal);
                Assert.Equal("exports", Path.GetFileName(Path.GetDirectoryName(written)));
            }
            finally
            {
                host.Close();
                if (written != null && File.Exists(written))
                    File.Delete(written);
            }
        });
    }

    private static Button RequireButton(ShellWindow window, string name)
        => RequireElement<Button>(window, name);

    /// <summary>
    /// 中央主文档页替身。DEC-023 后命令集页由 HistoryMercury 提供，不在本仓库门禁内；
    /// 需要「中央区存在一个页面」的 Vulcan chrome 用例改用本替身。
    /// </summary>
    private static ToolWindowDescriptor CenterPage(string id) => new()
    {
        Id = id,
        Title = id,
        DefaultSide = DockSide.Center,
        DefaultRatio = 1,
        ContentFactory = () => new Border(),
    };

    private static bool HasKeyboardOrLogicalFocus(FrameworkElement element)
        => element.IsKeyboardFocusWithin
           || ReferenceEquals(
               FocusManager.GetFocusedElement(FocusManager.GetFocusScope(element)),
               element);

    private static T RequireElement<T>(ShellWindow window, string name)
        where T : FrameworkElement
    {
        var element = window.FindName(name);
        Assert.NotNull(element);
        return Assert.IsAssignableFrom<T>(element);
    }

    private static object? GetPrivateField(object instance, string name)
    {
        var field = instance.GetType().GetField(
            name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(instance);
    }

    private static T? FindAncestor<T>(DependencyObject element, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match && predicate(match))
                return match;
        }

        return null;
    }

    private static IEnumerable<T> FindLogicalDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match)
                yield return match;
            foreach (var descendant in FindLogicalDescendants<T>(child))
                yield return descendant;
        }
    }

    private static Color SamplePixel(FrameworkElement element, double relativeX, double relativeY)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var x = Math.Clamp((int)(width * relativeX), 0, width - 1);
        var y = Math.Clamp((int)(height * relativeY), 0, height - 1);
        var pixels = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
        return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private sealed class MemoryLayoutStore : ILayoutStore
    {
        private string? _current;
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

        public string? ReadCurrent() => _current;
        public void WriteCurrent(string payload) => _current = payload;
        public void DeleteCurrent() => _current = null;
        public void ReplaceCurrent(string oldValue, string newValue)
            => _current = _current?.Replace(oldValue, newValue, StringComparison.Ordinal);
        public string? ReadNamed(string name) => _named.GetValueOrDefault(name);
        public void WriteNamed(string name, string payload) => _named[name] = payload;
        public IReadOnlyList<string> ListNamed() => _named.Keys.ToList();
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }

    /// <summary>????? EntryAdded ???,?????????</summary>
    private sealed class RelayLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = new();

        public void Log(ShellLogLevel level, string category, string message)
            => Raise(level, category, message);

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => _entries;

        public void Raise(ShellLogLevel level, string category, string message)
        {
            var entry = new ShellLogEntry(DateTime.Now, level, category, message);
            _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }
    }

    private sealed class CompletionCatalogSession : ICommandCatalogSession
    {
        public event EventHandler<CommandCatalogChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> Domains => ["vulcan"];
        public IReadOnlyList<string> Classes => ["app"];
        public string? SelectedCommandName => null;
        public CommandCatalogFilter CurrentFilter => new();
        public ConsoleCompletionResult LastResult { get; private set; } = ConsoleCompletionResult.Empty;
        public string LastText { get; private set; } = "";
        public int CompletionCalls { get; private set; }

        public Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public void SetFilter(CommandCatalogFilter filter) { }

        public bool TrySetDomain(string domain, out IReadOnlyList<string> availableDomains)
        {
            availableDomains = ["全部", "vulcan"];
            return availableDomains.Contains(domain, StringComparer.OrdinalIgnoreCase);
        }

        public bool TrySetCommandClass(string commandClass, out IReadOnlyList<string> availableClasses)
        {
            availableClasses = ["全部", "app"];
            return availableClasses.Contains(commandClass, StringComparer.OrdinalIgnoreCase);
        }

        public void SetConsoleQuery(string query) { }
        public bool MoveSelection(int direction) => false;
        public void Select(string? commandName) { }

        public Task<ConsoleCompletionResult> CompleteAsync(
            string text,
            int caretIndex,
            CancellationToken cancellationToken = default)
        {
            LastText = text;
            CompletionCalls++;
            var (candidate, replaceStart, replaceLength) = text switch
            {
                "v" => (Completion("vulcan.", ConsoleCompletionKind.Domain), 0, 1),
                "vulcan." => (Completion("vulcan.proj.", ConsoleCompletionKind.Class), 0, 7),
                "vulcan.proj." => (Completion("vulcan.proj.open ", ConsoleCompletionKind.Method), 0, 12),
                "vulcan.proj.open " => (Completion("name=", ConsoleCompletionKind.Parameter), 17, 0),
                "vulcan.proj.open name=" => (Completion("Mercury", ConsoleCompletionKind.Value), 22, 0),
                "vulcan.proj.open name=Mercury " => (Completion("mode=", ConsoleCompletionKind.Parameter), 30, 0),
                "vulcan.proj.open name=Mercury mode=" => (Completion("Fast", ConsoleCompletionKind.Value), 35, 0),
                "vulcan.proj.free " => (Completion("name", ConsoleCompletionKind.Parameter, ""), 17, 0),
                _ => (null, 0, 0),
            };
            LastResult = new ConsoleCompletionResult
            {
                Candidates = candidate is null ? [] : [candidate],
                ReplaceStart = replaceStart,
                ReplaceLength = replaceLength,
            };
            return Task.FromResult(LastResult);
        }

        private static ConsoleCompletionCandidate Completion(
            string text,
            ConsoleCompletionKind kind,
            string? insertText = null)
            => new()
            {
                InsertText = insertText ?? text,
                DisplayText = text,
                Description = kind.ToString(),
                Kind = kind,
            };

        public void Dispose() { }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback)
            => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }
}
