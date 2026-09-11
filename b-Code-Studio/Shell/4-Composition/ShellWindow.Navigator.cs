using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HistoryAurora.Shell.Base;
using HistoryAurora.Shell.Base.Docking;
using HistoryAurora.Shell.Components.Scenes;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Composition;

/// <summary>
/// 导航器（REQ-UI-088 / 089 / 099）：右栏 + 搜索浮层。
///
/// 它承担「去哪」这一件事——以前是全局页签条在做，模块一多就排成长长一排，
/// 用 Minerva 时 Janus 的三页也挂在眼前。现在去别处走这里。
///
/// **不做成停靠页面**：那样它自己要占一格，还会卷进每个场景的布局里。
///
/// 1.20.0 起右栏从左侧搬到右侧，并接下两件从顶栏转过来的事：顶部的窗口控制组，
/// 和下半部分的常用页面胶囊——按住拖出来就是一个带蓝色停靠点的浮窗，落到停靠点上就嵌进场景。
/// 1.20.2 顶栏整个删掉（REQ-UI-101），右栏在专注态也不让位：窗口控制组一直在这里。
///
/// 索引一律派生：行来自场景清单、停靠注册表、动作注册表，不落盘；
/// 检索文本只用已声明的字段（标题、owner、动作说明），不新增要人维护的关键词——
/// 要人维护的元数据，会在项目变成历史的那一刻起开始腐烂。
///
/// 场景膨胀的对策是**搜索 + 频次**（用户拍板）：右栏只挑用得多的几个，
/// 其余一律靠搜，频次给结果加权。不给场景做管理页。
/// </summary>
internal partial class ShellWindow
{
    internal const string NavigatorHotkeyId = "aurora.nav";

    /// <summary>设置键：导航器全局快捷键的按键序列，文法同 <c>mercury.hotkey.register stroke=</c>。</summary>
    internal const string NavigatorHotkeySetting = "aurora.nav.hotkey";

    /// <summary>
    /// 默认 Ctrl+Alt+K，不是 Ctrl+K：这是 Mercury 注册的**全局**快捷键，
    /// 抢 Ctrl+K 等于在浏览器、VS 与每一个编辑器里拿走它。
    /// </summary>
    internal const string DefaultNavigatorHotkey = "Ctrl+Alt+K";

    private const int RailLimit = 12;
    private const int ResultLimit = 30;
    private const string KindScene = "场景";
    private const string KindPage = "页面";
    private const string KindAction = "动作";

    /// <summary>指针在胶囊拖出来的新浮窗里的落点：页签行上靠左的一点，拖起来像拎着页签。</summary>
    private static readonly Point CapsuleDragAnchor = new(48, 16);

    private sealed record NavEntry(
        string Kind,
        string UsageKey,
        string Title,
        string Detail,
        string Command,
        string Haystack,
        double Boost);

    private string NavigatorHotkey
    {
        get
        {
            var stroke = _settings.Get(NavigatorHotkeySetting);
            return string.IsNullOrWhiteSpace(stroke) ? DefaultNavigatorHotkey : stroke.Trim();
        }
    }

    private void InitializeNavigator()
    {
        NavSearchButton.Click += (_, _) => OpenNavigator();
        NavQuery.TextChanged += (_, _) => RefreshNavigatorResults();
        NavQuery.PreviewKeyDown += OnNavigatorKeyDown;
        NavResults.MouseDoubleClick += (_, _) => PickNavigatorEntry();
        NavBackdrop.MouseLeftButtonDown += (_, _) => CloseNavigator();
        NavRail.MouseLeftButtonDown += OnNavRailMouseLeftButtonDown;
        _scenes.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            RefreshNavigatorRail();
            try
            {
                BuildMenus();
            }
            catch (Exception ex)
            {
                _log.Error("menu", $"菜单重建失败，已保留上一版菜单: {ex.Message}");
            }
            UpdateLayoutIndicator();
        });
        RefreshNavigatorRail();
    }

    // ---------------------------------------------------------------- 场景

    /// <summary>
    /// 右栏上半挑「全部」+ 用得最多的几个 + 当前场景，**按标题排**而不是按频次排：
    /// 频次每切一次就变，按它排的话按钮会在手底下跳位置。
    /// </summary>
    private void RefreshNavigatorRail()
    {
        if (_closing)
            return;

        NavSearchButton.ToolTip = $"搜索场景、页面与动作（{NavigatorHotkey}）";

        var scenes = _scenes.List();
        var shown = scenes.Where(s => s.Source != SceneSource.All).Take(RailLimit).ToList();
        var active = scenes.FirstOrDefault(s => s.Active);
        if (active != null && active.Source != SceneSource.All && !shown.Contains(active))
            shown.Add(active);

        NavRailItems.Children.Clear();
        if (scenes.FirstOrDefault(s => s.Source == SceneSource.All) is { } all)
            NavRailItems.Children.Add(RailButton(all));
        foreach (var scene in shown.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase))
            NavRailItems.Children.Add(RailButton(scene));

        RefreshNavigatorPages(force: true);
    }

    private Button RailButton(SceneInfo scene)
    {
        var tip = $"{scene.Title} · {SceneKindText(scene.Source)}场景";
        if (scene.Uses > 0)
            tip += $" · 用过 {scene.Uses} 次";

        var button = new Button
        {
            Content = new TextBlock { Text = scene.Title, TextTrimming = TextTrimming.CharacterEllipsis },
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 1, 0, 1),
            ToolTip = tip,
        };
        button.SetResourceReference(StyleProperty, "Aurora.Button.Ghost");
        if (scene.Active)
        {
            button.SetResourceReference(BackgroundProperty, "Aurora.Brush.AccentSoft");
            button.FontWeight = FontWeights.SemiBold;
        }

        button.Click += (_, _) => _ = _bus.ExecuteAsync("aurora.scene.go id=" + CommandParser.QuoteArg(scene.Id), "UI");
        return button;
    }

    private static string SceneKindText(SceneSource source) => source switch
    {
        SceneSource.All => "内置",
        SceneSource.Derived => "模块",
        _ => "另存",
    };

    /// <summary>右栏空白处承担一部分标题栏职责：拖动窗口、双击最大化。按钮与胶囊自己吃掉按下，不会走到这里。</summary>
    private void OnNavRailMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        if (WindowState != WindowState.Normal)
            return;
        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // 鼠标已抬起时 DragMove 会抛；这一下拖动本来就没有发生。
        }
    }

    // ---------------------------------------------------------------- 常用页面（REQ-UI-099）

    /// <summary>上一次画出来的胶囊（id + 标题，按顺序）。没变就不重画，巡检才敢每 400ms 调一次。</summary>
    private string _pagesSignature = "";

    private Border? _capsulePressed;
    private Point _capsuleStart;

    /// <summary>
    /// 右栏下半：此刻没露面的页，按使用频次、再按标题排。
    /// 露着的页不列——场景之间的区别只是显隐（REQ-UI-094），右栏要回答的是「还有什么没打开」。
    /// </summary>
    internal void RefreshNavigatorPages(bool force = false)
    {
        if (_closing || _capsulePressed != null)
            return;

        var hidden = _docking.ListWindows()
            .Where(window => !window.IsVisible)
            .OrderByDescending(window => _usage.Get(PageUsageKey(window.Id))?.Count ?? 0)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var signature = string.Join("\n", hidden.Select(window => window.Id + "\t" + window.Title));
        if (!force && signature == _pagesSignature)
            return;

        _pagesSignature = signature;
        NavPageItems.Children.Clear();

        // 同名的页（Aurora 与 Mercury 各有一页「命令集」）光看胶囊分不出来，附上模块名。
        var clashing = hidden
            .GroupBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (var window in hidden)
            NavPageItems.Children.Add(PageCapsule(window, clashing.Contains(window.Title)));
        NavPagesEmpty.Visibility = hidden.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border PageCapsule(ToolWindowInfo window, bool withOwner)
    {
        var text = new TextBlock
        {
            Text = withOwner ? $"{window.Title} · {SceneManager.TitleFor(window.Owner)}" : window.Title,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "Aurora.Font.Family");
        text.SetResourceReference(TextBlock.FontSizeProperty, "Aurora.Font.Small");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Aurora.Brush.TextPrimary");

        var uses = _usage.Get(PageUsageKey(window.Id))?.Count ?? 0;
        var capsule = new Border
        {
            Tag = window.Id,
            Child = text,
            Margin = new Thickness(0, 0, 4, 4),
            Padding = new Thickness(10, 3, 10, 3),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = $"{window.Title} · {SceneManager.TitleFor(window.Owner)}"
                      + (uses > 0 ? $" · 用过 {uses} 次" : "")
                      + "\n按住拖出来，落到蓝色停靠点上嵌入",
        };
        capsule.SetResourceReference(Border.BackgroundProperty, "Aurora.Brush.Surface");
        capsule.SetResourceReference(Border.BorderBrushProperty, "Aurora.Brush.Hairline");
        capsule.MouseEnter += (_, _) => capsule.SetResourceReference(Border.BackgroundProperty, "Aurora.Brush.SurfaceHover");
        capsule.MouseLeave += (_, _) => capsule.SetResourceReference(Border.BackgroundProperty, "Aurora.Brush.Surface");
        capsule.MouseLeftButtonDown += OnCapsuleMouseLeftButtonDown;
        capsule.MouseMove += OnCapsuleMouseMove;
        capsule.MouseLeftButtonUp += OnCapsuleMouseLeftButtonUp;
        capsule.LostMouseCapture += (_, _) =>
        {
            if (ReferenceEquals(_capsulePressed, capsule))
                _capsulePressed = null;
        };
        return capsule;
    }

    private void OnCapsuleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border capsule)
            return;

        _capsulePressed = capsule;
        _capsuleStart = e.GetPosition(this);
        capsule.CaptureMouse();

        // 不让右栏把这一下当成拖窗口。
        e.Handled = true;
    }

    /// <summary>越过系统拖动阈值：把这一页交给拖动协调器，走与 Ctrl 标签态同一条浮出 → 停靠的路。</summary>
    private void OnCapsuleMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border { Tag: string id } capsule ||
            !ReferenceEquals(capsule, _capsulePressed) ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _capsuleStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _capsuleStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // 先清按下状态再交出去：交接会放掉捕获，LostMouseCapture 不该再当成一次点击。
        _capsulePressed = null;
        _usage.Record(PageUsageKey(id));
        _pageDrag.BeginExternalPageDrag(id, capsule, CapsuleDragAnchor);
        e.Handled = true;
    }

    /// <summary>
    /// 胶囊只用来拖（1.20.2 用户拍板）：没越过拖动阈值就松开，什么都不做——不打开页、不换窗口里的页。
    /// 要打开一页走搜索（导航器）或 <c>aurora.scene.open</c>。
    /// </summary>
    private void OnCapsuleMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border capsule || !ReferenceEquals(capsule, _capsulePressed))
            return;

        _capsulePressed = null;
        capsule.ReleaseMouseCapture();
        e.Handled = true;
    }

    private static string PageUsageKey(string id) => "page:" + id;

    // ---------------------------------------------------------------- 搜索浮层

    /// <summary>aurora.nav.open 的落点：已开且窗口在前台就关，否则打开。返回是否打开。</summary>
    internal bool ToggleNavigator()
    {
        if (NavOverlay.Visibility == Visibility.Visible && IsActive)
        {
            CloseNavigator();
            return false;
        }

        OpenNavigator();
        return true;
    }

    private void OpenNavigator()
    {
        // 全局快捷键可能在窗口隐藏或最小化时按下：先把窗口请到前台。
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        WindowForegroundActivator.Activate(this);

        NavOverlay.Visibility = Visibility.Visible;
        if (NavQuery.Text.Length > 0)
            NavQuery.Text = "";
        else
            RefreshNavigatorResults();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            NavQuery.Focus();
            Keyboard.Focus(NavQuery);
        });
    }

    private void CloseNavigator() => NavOverlay.Visibility = Visibility.Collapsed;

    private void OnNavigatorKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveNavigatorSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveNavigatorSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                PickNavigatorEntry();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseNavigator();
                e.Handled = true;
                break;
        }
    }

    private void MoveNavigatorSelection(int delta)
    {
        if (NavResults.Items.Count == 0)
            return;
        var index = Math.Clamp(NavResults.SelectedIndex + delta, 0, NavResults.Items.Count - 1);
        NavResults.SelectedIndex = index;
        NavResults.ScrollIntoView(NavResults.Items[index]);
    }

    private void PickNavigatorEntry()
    {
        if ((NavResults.SelectedItem as ListBoxItem)?.Tag is not NavEntry entry)
            return;

        CloseNavigator();

        // 场景的次数由 scene.go 自己记，这里只记页面与动作，免得一次切换记两笔。
        if (entry.Kind != KindScene)
            _usage.Record(entry.UsageKey);
        _ = _bus.ExecuteAsync(entry.Command, "UI");
    }

    private void RefreshNavigatorResults()
    {
        var query = NavQuery.Text.Trim();
        var hits = NavigatorIndex()
            .Select(entry => (Entry: entry, Score: MatchNavigatorEntry(query, entry)))
            .Where(hit => hit.Score >= 0)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Entry.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(ResultLimit)
            .ToList();

        NavResults.Items.Clear();
        foreach (var hit in hits)
            NavResults.Items.Add(NavigatorRow(hit.Entry));
        if (NavResults.Items.Count > 0)
            NavResults.SelectedIndex = 0;

        NavHint.Text = hits.Count == 0
            ? "没有匹配项"
            : "Enter 打开 · Esc 关闭";
    }

    private IEnumerable<NavEntry> NavigatorIndex()
    {
        foreach (var scene in _scenes.List())
        {
            var detail = SceneKindText(scene.Source) + "场景" + (scene.Active ? " · 当前" : "");
            yield return new NavEntry(
                KindScene, "scene:" + scene.Id, scene.Title, detail,
                "aurora.scene.go id=" + CommandParser.QuoteArg(scene.Id),
                scene.Title + " " + scene.Id, Boost(scene.Uses));
        }

        // 页面条目在当前场景里打开：场景不拥有页面（REQ-UI-094），没有「切到含它的场景」这回事。
        foreach (var window in _docking.ListWindows())
        {
            var key = PageUsageKey(window.Id);
            var detail = SceneManager.TitleFor(window.Owner) + (window.IsVisible ? " · 已打开" : "");
            yield return new NavEntry(
                KindPage, key, window.Title, detail,
                "aurora.scene.open page=" + CommandParser.QuoteArg(window.Id),
                $"{window.Title} {window.Id} {window.Owner}", Boost(_usage.Get(key)?.Count ?? 0));
        }

        // 动作只用来**定位**：回车切到它所在模块的场景，不在这里执行。
        // 动作的参数多从页面的选择通道取值，离开页面执行，填进去的是空的或是别处的选中行。
        foreach (var action in _actions.Actions)
        {
            var key = "action:" + action.Id;
            yield return new NavEntry(
                KindAction, key, action.Title,
                SceneManager.TitleFor(action.Owner) + " · " + (action.Summary ?? action.Command),
                "aurora.scene.go id=" + CommandParser.QuoteArg(SceneManager.SceneIdFor(action.Owner)),
                $"{action.Title} {action.Id} {action.Summary} {action.Owner}", Boost(_usage.Get(key)?.Count ?? 0));
        }
    }

    /// <summary>
    /// 空查询只列场景；有查询时每个词都得命中，标题开头 &gt; 标题包含 &gt; 其余字段包含，
    /// 再加使用频次（对数，免得一个用了几百次的条目压住一切）。
    /// </summary>
    private static double MatchNavigatorEntry(string query, NavEntry entry)
    {
        if (query.Length == 0)
            return entry.Kind == KindScene ? 1 + entry.Boost : -1;

        var score = 0.0;
        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Title.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                score += 4;
            else if (entry.Title.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 3;
            else if (entry.Haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 1;
            else
                return -1;
        }

        return score + entry.Boost + (entry.Kind == KindScene ? 0.5 : 0);
    }

    private static double Boost(int uses) => uses <= 0 ? 0 : Math.Log(1 + uses) * 1.5;

    private static ListBoxItem NavigatorRow(NavEntry entry)
    {
        var kind = new TextBlock { Text = entry.Kind, Width = 40 };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "Aurora.Brush.TextSecondary");
        var title = new TextBlock { Text = entry.Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) };
        var detail = new TextBlock { Text = entry.Detail, TextTrimming = TextTrimming.CharacterEllipsis };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Aurora.Brush.TextSecondary");

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(kind, Dock.Left);
        DockPanel.SetDock(title, Dock.Left);
        row.Children.Add(kind);
        row.Children.Add(title);
        row.Children.Add(detail);

        var item = new ListBoxItem { Content = row, Tag = entry };
        item.SetResourceReference(StyleProperty, "Aurora.Suggest.Item");
        return item;
    }

    // ---------------------------------------------------------------- 全局快捷键

    /// <summary>本进程是否已注册成功。成功一次就不再每轮发现都注册——那会让控制台每轮多一条回显。</summary>
    private bool _navigatorHotkeyRegistered;

    /// <summary>
    /// 请 HistoryMercury 注册导航器的全局快捷键（DEC-022：快捷键整体归 Mercury，
    /// Shell 不自己挂 InputBinding）。每轮模块发现之后调用，成功一次后不再重复。
    ///
    /// 没装 Mercury 时只记一条 Info——先 Validate 再执行，免得一条「未知指令」把控制台弹出来；
    /// 下一轮发现（例如 Mercury 后装上）会再试。
    ///
    /// 已知缺口：Mercury 单独热重载会丢掉它的全部注册，而本进程已记为成功，不会补注册，
    /// 要等 Aurora 下次启动或热重载。
    /// </summary>
    internal async Task RegisterNavigatorHotkeyAsync()
    {
        if (_navigatorHotkeyRegistered)
            return;

        var stroke = NavigatorHotkey;
        var text = "mercury.hotkey.register id=" + NavigatorHotkeyId
                   + " stroke=" + CommandParser.QuoteArg(stroke)
                   + " command=" + CommandParser.QuoteArg("aurora.nav.open")
                   + " owner=HistoryAurora";
        var invalid = _bus.Validate(text);
        if (invalid != null)
        {
            _log.Info("nav", $"导航器全局快捷键未注册（{invalid}）；可点右栏的「搜索」或执行 aurora.nav.open");
            return;
        }

        var result = await _bus.ExecuteAsync(text, "UI").ConfigureAwait(true);
        _navigatorHotkeyRegistered = result.Success;
        if (result.Success)
            _log.Info("nav", $"导航器全局快捷键 {stroke} 已由 HistoryMercury 注册");
        else
            _log.Warn("nav", $"导航器全局快捷键 {stroke} 注册失败: {result.Message}");
    }
}
