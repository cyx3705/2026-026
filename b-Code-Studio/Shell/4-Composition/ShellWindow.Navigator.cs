using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using HistoryAurora.Shell.Base;
using HistoryAurora.Shell.Base.Docking;
using HistoryAurora.Shell.Components.Scenes;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Composition;

/// <summary>
/// 导航器（REQ-UI-088 / 099 / 106 / 107）：右栏——窗口控制组、搜索、场景、常用页面，外加两片拖窗口的空白。
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
/// 1.20.3（REQ-UI-106）搜索浮层退役：搜索框就是右栏第二行，输入随手**筛下面这两段**——
/// 场景与常用页面，回车打开排在最前的那个候选。浮层是「另开一个满屏的界面去找东西」，
/// 而要找的东西本来就列在右栏里；两处各画一遍同一份清单，是两处都要维护的重复。
/// 动作不再进搜索：动作条目从来只是「切到它所在模块的场景」，与直接搜场景重合。
///
/// 索引一律派生：行来自场景清单与停靠注册表，不落盘；检索文本只用已声明的字段
/// （标题、id、owner），不新增要人维护的关键词——要人维护的元数据，
/// 会在项目变成历史的那一刻起开始腐烂。
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

    /// <summary>没有搜索词时右栏最多列几个场景。搜索时不限——筛出来的就那么几个。</summary>
    private const int RailLimit = 12;

    /// <summary>指针在胶囊拖出来的新浮窗里的落点：靠左上的一点，拖起来像拎着页签。</summary>
    private static readonly Point CapsuleDragAnchor = new(48, 16);

    /// <summary>搜索框此刻的内容。空串表示不筛。</summary>
    private string NavFilter => NavQuery.Text.Trim();

    /// <summary>当前筛选下排在最前的场景与页面，回车打开它们（场景优先）。</summary>
    private string? _topSceneId;
    private string? _topPageId;

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
        NavQuery.TextChanged += (_, _) => OnNavigatorQueryChanged();
        NavQuery.PreviewKeyDown += OnNavigatorKeyDown;

        // 拖窗口的两片空白（REQ-UI-107）：右栏整条，以及停靠区里卡片之外的画布。
        // 走 Preview 而不是冒泡：右栏里的滚动区、停靠区里的窗格都会先把按下吃掉，
        // 冒泡上来时能拖的只剩边角上那几条缝——用户报的「只有一小部分区域可以拖」就是这个。
        NavRail.PreviewMouseLeftButtonDown += OnNavRailPreviewMouseLeftButtonDown;
        DockManager.PreviewMouseLeftButtonDown += OnDockCanvasPreviewMouseLeftButtonDown;
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
    /// 右栏上半。没有搜索词时挑「全部」+ 用得最多的几个 + 当前场景，**按标题排**而不是按频次排：
    /// 频次每切一次就变，按它排的话按钮会在手底下跳位置。
    ///
    /// 有搜索词时改为筛选（REQ-UI-106）：不限条数、按匹配得分排，不再硬塞「全部」与当前场景——
    /// 搜索时用户要的是「叫这个名字的那个」，不是「顺手能看到的那几个」。
    /// </summary>
    private void RefreshNavigatorRail()
    {
        if (_closing)
            return;

        NavQuery.ToolTip = $"搜索场景与页面（{NavigatorHotkey}）；回车打开排在最前的候选";

        var filter = NavFilter;
        var scenes = _scenes.List();
        List<SceneInfo> shown;
        if (filter.Length > 0)
        {
            shown = scenes
                .Select(scene => (Scene: scene, Score: MatchScore(filter, scene.Title + " " + scene.Id)))
                .Where(hit => hit.Score >= 0)
                .OrderByDescending(hit => hit.Score + Boost(hit.Scene.Uses))
                .ThenBy(hit => hit.Scene.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(hit => hit.Scene)
                .ToList();
        }
        else
        {
            var rest = scenes.Where(scene => scene.Source != SceneSource.All).Take(RailLimit).ToList();
            var active = scenes.FirstOrDefault(scene => scene.Active);
            if (active != null && active.Source != SceneSource.All && !rest.Contains(active))
                rest.Add(active);
            rest = rest.OrderBy(scene => scene.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            shown = scenes.Where(scene => scene.Source == SceneSource.All).Concat(rest).ToList();
        }

        NavRailItems.Children.Clear();
        foreach (var scene in shown)
            NavRailItems.Children.Add(RailButton(scene));

        _topSceneId = filter.Length > 0 ? shown.FirstOrDefault()?.Id : null;
        NavScenesEmpty.Visibility = filter.Length > 0 && shown.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

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

    // ---------------------------------------------------------------- 拖动窗口的空白（REQ-UI-107）

    /// <summary>
    /// 右栏整条都能拖窗口、双击最大化（REQ-UI-107）。
    ///
    /// 走 Preview，因此要自己把「这一下是给控件的」挑出去：按钮、搜索框、滚动条、常用页面胶囊
    /// 都还没收到这一下，先让它们收。剩下的空白——行与行之间、列表下方、胶囊之间——一律是拖动面。
    /// </summary>
    private void OnNavRailPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveSurface(e.OriginalSource as DependencyObject, NavRail))
            return;
        BeginShellWindowGesture(e);
    }

    /// <summary>
    /// 停靠区里卡片之外的画布也能拖窗口（REQ-UI-107）：卡片有 <c>Aurora.Space.Gap</c> 的外缘间隙，
    /// 那片底色与右栏是同一片（REQ-UI-104），手感上就该是同一块可拖的面。
    ///
    /// 判据是**命中的就是 DockingManager 自己**：窗格、分隔条、自动隐藏边条都有自己的命中面，
    /// 落在它们上面时 <c>OriginalSource</c> 不会是管理器。页面内容再怎么点也走不到这里。
    /// </summary>
    private void OnDockCanvasPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, DockManager))
            return;
        BeginShellWindowGesture(e);
    }

    private void BeginShellWindowGesture(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        // 最大化时 DragMove 会抛；这一下本来也没有「拖到哪去」的语义。
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

    /// <summary>
    /// 这一下按在了控件上（<paramref name="root"/> 之内）。胶囊是带页面 id 的 <see cref="Border"/>，
    /// 它自己要起拖动会话，不能被窗口拖动抢走。
    /// </summary>
    internal static bool IsInteractiveSurface(DependencyObject? source, DependencyObject root)
    {
        for (var node = source; node != null && !ReferenceEquals(node, root); node = VisualOrLogicalParent(node))
        {
            if (node is ButtonBase or TextBoxBase or ScrollBar or Thumb)
                return true;
            if (node is Border { Tag: string tag } && tag.Length > 0)
                return true;
        }

        return false;
    }

    private static DependencyObject? VisualOrLogicalParent(DependencyObject node)
        => node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    // ---------------------------------------------------------------- 常用页面（REQ-UI-099）

    /// <summary>上一次画出来的胶囊（id + 标题，按顺序）。没变就不重画，巡检才敢每 400ms 调一次。</summary>
    private string _pagesSignature = "";

    private Border? _capsulePressed;
    private Point _capsuleStart;

    /// <summary>
    /// 右栏下半：此刻没露面的页，按使用频次、再按标题排；有搜索词时先筛一道、按匹配得分排。
    /// 露着的页不列——场景之间的区别只是显隐（REQ-UI-094），右栏要回答的是「还有什么没打开」，
    /// 搜索也在这份清单里搜（REQ-UI-106）。
    /// </summary>
    internal void RefreshNavigatorPages(bool force = false)
    {
        if (_closing || _capsulePressed != null)
            return;

        var filter = NavFilter;
        var hidden = _docking.ListWindows()
            .Where(window => !window.IsVisible)
            .Select(window => (Page: window, Score: filter.Length == 0
                ? 0
                : MatchScore(filter, $"{window.Title} {window.Id} {window.Owner}")))
            .Where(hit => hit.Score >= 0)
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => _usage.Get(PageUsageKey(hit.Page.Id))?.Count ?? 0)
            .ThenBy(hit => hit.Page.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(hit => hit.Page)
            .ToList();

        // 签名带上搜索词：不带的话，改了搜索词而命中集恰好没变时会被这一行挡掉。
        var signature = filter + "|" + string.Join(
            "\n", hidden.Select(window => window.Id + "\t" + window.Title));
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

        _topPageId = filter.Length > 0 ? hidden.FirstOrDefault()?.Id : null;
        NavPagesEmpty.Text = filter.Length > 0 ? "没有匹配的页面" : "所有页面都已打开";
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

    // ---------------------------------------------------------------- 搜索（REQ-UI-106）

    /// <summary>
    /// <c>aurora.nav.open</c> 的落点：把窗口请到前台并聚焦右栏搜索框，选中已有内容好直接改写。
    ///
    /// 1.20.2 及以前这条指令开的是一层盖住整窗的搜索浮层；1.20.3 浮层退役，搜索并进右栏，
    /// 指令与 HistoryMercury 注册的全局快捷键都还在，只是落点从「开浮层」换成「聚焦搜索框」。
    /// </summary>
    internal bool FocusNavigatorSearch()
    {
        // 全局快捷键可能在窗口隐藏或最小化时按下：先把窗口请到前台。
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        WindowForegroundActivator.Activate(this);

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            NavQuery.Focus();
            Keyboard.Focus(NavQuery);
            NavQuery.SelectAll();
        });
        return true;
    }

    private void OnNavigatorQueryChanged()
    {
        NavQueryHint.Visibility = NavQuery.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshNavigatorRail();
    }

    /// <summary>回车打开排在最前的候选（场景优先），Esc 清空——清空之后再按一次才把焦点让出去。</summary>
    private void OnNavigatorKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                PickTopNavigatorCandidate();
                e.Handled = true;
                break;
            case Key.Escape:
                if (NavQuery.Text.Length > 0)
                    NavQuery.Text = "";
                else
                    Keyboard.ClearFocus();
                e.Handled = true;
                break;
        }
    }

    private void PickTopNavigatorCandidate()
    {
        if (_topSceneId is { } sceneId)
        {
            // 场景的次数由 scene.go 自己记，这里不再记一笔。
            _ = _bus.ExecuteAsync("aurora.scene.go id=" + CommandParser.QuoteArg(sceneId), "UI");
            return;
        }

        if (_topPageId is not { } pageId)
            return;
        _usage.Record(PageUsageKey(pageId));
        _ = _bus.ExecuteAsync("aurora.scene.open page=" + CommandParser.QuoteArg(pageId), "UI");
    }

    /// <summary>
    /// 每个词都得命中，标题开头 &gt; 标题包含 &gt; 其余字段包含；一个词都不命中判 -1（出局）。
    /// 检索文本只用已声明的字段（标题、id、owner），不新增要人维护的关键词。
    /// </summary>
    private static double MatchScore(string query, string haystack)
    {
        var score = 0.0;
        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (haystack.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                score += 4;
            else if (haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 2;
            else
                return -1;
        }

        return score;
    }

    /// <summary>使用频次加权，取对数，免得一个用了几百次的条目压住一切。</summary>
    private static double Boost(int uses) => uses <= 0 ? 0 : Math.Log(1 + uses) * 1.5;

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
            _log.Info("nav", $"导航器全局快捷键未注册（{invalid}）；右栏的搜索框照常可用，或执行 aurora.nav.open");
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
