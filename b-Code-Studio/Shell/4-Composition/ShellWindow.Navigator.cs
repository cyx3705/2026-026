using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HistoryAurora.Shell.Base;
using HistoryAurora.Shell.Components.Scenes;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Composition;

/// <summary>
/// 导航器（REQ-UI-088 / 089）：左侧场景栏 + 搜索浮层。
///
/// 它承担「去哪」这一件事——以前是全局页签条在做，模块一多就排成长长一排，
/// 用 Minerva 时 Janus 的三页也挂在眼前。现在页签只显示当前场景的页，去别处走这里。
///
/// **不做成停靠页面**：那样它自己要占一格，还会卷进每个场景的布局里。
///
/// 索引一律派生：行来自场景清单、停靠注册表、动作注册表，不落盘；
/// 检索文本只用已声明的字段（标题、owner、动作说明），不新增要人维护的关键词——
/// 要人维护的元数据，会在项目变成历史的那一刻起开始腐烂。
///
/// 场景膨胀的对策是**搜索 + 频次**（用户拍板）：左栏只挑用得多的几个，
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

    private sealed record NavEntry(
        string Kind,
        string UsageKey,
        string Title,
        string Detail,
        string Command,
        string? AltCommand,
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
        NavResults.MouseDoubleClick += (_, _) => PickNavigatorEntry(alternate: false);
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

    // ---------------------------------------------------------------- 左栏

    /// <summary>
    /// 左栏挑「全部」+ 用得最多的几个 + 当前场景，**按标题排**而不是按频次排：
    /// 频次每切一次就变，按它排的话按钮会在手底下跳位置。
    /// </summary>
    private void RefreshNavigatorRail()
    {
        if (_closing)
            return;

        // 专注态（F11）只看一页，左栏跟着让位。
        NavRail.Visibility = _docking.MaximizedId == null ? Visibility.Visible : Visibility.Collapsed;
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
    }

    private Button RailButton(SceneInfo scene)
    {
        var tip = $"{scene.Title} · {scene.Pages.Count} 页";
        if (scene.Uses > 0)
            tip += $" · 用过 {scene.Uses} 次";
        if (scene.Broken.Count > 0)
            tip += $" · {scene.Broken.Count} 页未注册";

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

    /// <summary>左栏空白处承担一部分标题栏职责：拖动窗口、双击最大化。按钮自己吃掉按下，不会走到这里。</summary>
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
                PickNavigatorEntry(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
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

    private void PickNavigatorEntry(bool alternate)
    {
        if ((NavResults.SelectedItem as ListBoxItem)?.Tag is not NavEntry entry)
            return;

        var command = alternate && entry.AltCommand != null ? entry.AltCommand : entry.Command;
        CloseNavigator();

        // 场景的次数由 scene.go 自己记，这里只记页面与动作，免得一次切换记两笔。
        if (entry.Kind != KindScene)
            _usage.Record(entry.UsageKey);
        _ = _bus.ExecuteAsync(command, "UI");
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
            : "Enter 打开 · Shift+Enter 把页面加入当前场景 · Esc 关闭";
    }

    private IEnumerable<NavEntry> NavigatorIndex()
    {
        foreach (var scene in _scenes.List())
        {
            var detail = $"{scene.Pages.Count} 页" + (scene.Active ? " · 当前" : "");
            yield return new NavEntry(
                KindScene, "scene:" + scene.Id, scene.Title, detail,
                "aurora.scene.go id=" + CommandParser.QuoteArg(scene.Id), null,
                scene.Title + " " + scene.Id, Boost(scene.Uses));
        }

        foreach (var window in _docking.ListWindows())
        {
            var key = "page:" + window.Id;
            var detail = SceneManager.TitleFor(window.Owner) + (window.IsVisible ? " · 已打开" : "");
            yield return new NavEntry(
                KindPage, key, window.Title, detail,
                "aurora.scene.open page=" + CommandParser.QuoteArg(window.Id),
                SceneManager.Resident.Contains(window.Id)
                    ? null
                    : "aurora.scene.add page=" + CommandParser.QuoteArg(window.Id),
                $"{window.Title} {window.Id} {window.Owner}", Boost(_usage.Get(key)?.Count ?? 0));
        }

        // 动作只用来**定位**：回车切到它所在的场景，不在这里执行。
        // 动作的参数多从页面的选择通道取值，离开页面执行，填进去的是空的或是别处的选中行。
        foreach (var action in _actions.Actions)
        {
            var key = "action:" + action.Id;
            yield return new NavEntry(
                KindAction, key, action.Title,
                SceneManager.TitleFor(action.Owner) + " · " + (action.Summary ?? action.Command),
                "aurora.scene.go id=" + CommandParser.QuoteArg(SceneManager.SceneIdFor(action.Owner)), null,
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
            _log.Info("nav", $"导航器全局快捷键未注册（{invalid}）；可点左栏的「搜索」或执行 aurora.nav.open");
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
