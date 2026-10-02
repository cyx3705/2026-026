using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HistoryAurora.Shell.Base.Dialogs;
using HistoryAurora.Shell.Components.Scenes;
using HistoryVulcan.Core.Commands;

namespace HistoryAurora.Shell.Composition;

/// <summary>右栏场景按钮与小栏胶囊上挂的身份：拖动会话只认它，不认控件。</summary>
internal sealed record SceneHandle(string Id, string Title, bool InTray);

/// <summary>
/// 场景拖放（REQ-UI-135，1.30.0）。页面调整打开时，右栏的场景按钮与右下小栏里的胶囊都能按住拖：
/// <list type="bullet">
///   <item>在场景段里上下拖：排队，松手处画一道墨色线（<c>aurora.scene.move</c>）；</item>
///   <item>拖进小栏：收起，右栏不再列它（<c>aurora.scene.hide</c>）；从小栏拖回场景段就是放回；</item>
///   <item>拖出整个窗体：删除，先弹确认（<c>aurora.scene.delete</c>）；</item>
///   <item>其余地方松手或按 Esc：什么都不做。</item>
/// </list>
///
/// 不走 OLE 拖放：要判断「出了窗体」，而 OLE 拖到窗体外就交给了别的程序。这里自己捕获鼠标，
/// 指针在窗体外照样收得到移动与抬起；跟手的那枚胶囊是一个不吃点击的弹出层，能画到窗体外面去。
/// 落点最后都变成一条场景指令，与切场景同一条总线路径，控制台里看得到。
/// </summary>
internal partial class ShellWindow
{
    /// <summary>跟手胶囊离指针的偏移：放在指针右下，不挡住落点。</summary>
    private static readonly Vector SceneGhostOffset = new(12, 10);

    private SceneDragSession? _sceneDrag;

    /// <summary>拖动期间场景表变过：松手后补画一次右栏。</summary>
    private bool _railStale;

    private FrameworkElement? _scenePressed;
    private Point _scenePressStart;

    private enum SceneDropKind
    {
        None,
        Reorder,
        Tray,
        Delete,
    }

    private sealed class SceneDragSession(SceneHandle scene, Popup ghost, Border card, TextBlock text)
    {
        public SceneHandle Scene { get; } = scene;

        public Popup Ghost { get; } = ghost;

        public Border Card { get; } = card;

        public TextBlock Text { get; } = text;

        public SceneDropKind Kind { get; set; }

        /// <summary>排队落点：挪到这个场景前面；null 表示挪到最后。</summary>
        public string? Before { get; set; }
    }

    private void InitializeSceneDrag()
    {
        MouseMove += OnSceneDragMouseMove;
        MouseLeftButtonUp += OnSceneDragMouseUp;
        LostMouseCapture += (_, _) =>
        {
            // 捕获被别人拿走（切到别的程序、弹出系统菜单）：这一次拖动作废。
            if (_sceneDrag != null && !IsMouseCaptured)
                EndSceneDrag(commit: false);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (_sceneDrag != null && e.Key == Key.Escape)
            {
                EndSceneDrag(commit: false);
                e.Handled = true;
            }
        };
    }

    private void AttachSceneDrag(FrameworkElement element)
    {
        element.PreviewMouseLeftButtonDown += OnScenePreviewMouseDown;
        element.PreviewMouseMove += OnScenePreviewMouseMove;
        element.PreviewMouseLeftButtonUp += OnScenePreviewMouseUp;
    }

    private void OnScenePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SceneHandle handle } element)
            return;

        // 右栏按钮只在页面调整时能拖；平时按下就是切场景，交给按钮自己。
        if (!handle.InTray && !_labelMode)
            return;

        _scenePressed = element;
        _scenePressStart = e.GetPosition(this);
        if (handle.InTray)
        {
            // 胶囊不是按钮：自己捕获，才收得到抬起（没拖就当一次点击）。
            element.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnScenePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SceneHandle handle } element ||
            !ReferenceEquals(element, _scenePressed) ||
            e.LeftButton != MouseButtonState.Pressed ||
            _sceneDrag != null)
        {
            return;
        }

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _scenePressStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _scenePressStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _scenePressed = null;
        BeginSceneDrag(handle, element, now);
        e.Handled = true;
    }

    /// <summary>没越过拖动阈值就松开：小栏胶囊当一次点击，切到那个场景；右栏按钮由它自己的 Click 处理。</summary>
    private void OnScenePreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SceneHandle handle } element ||
            !ReferenceEquals(element, _scenePressed))
        {
            return;
        }

        _scenePressed = null;
        if (!handle.InTray)
            return;

        element.ReleaseMouseCapture();
        e.Handled = true;
        _ = _bus.ExecuteAsync("aurora.scene.go id=" + CommandParser.QuoteArg(handle.Id), "UI");
    }

    private void BeginSceneDrag(SceneHandle handle, FrameworkElement source, Point at)
    {
        // 先放掉按钮的捕获：按钮因此不再处于按下态，松手时不会再触发一次切场景。
        source.ReleaseMouseCapture();

        var text = new TextBlock
        {
            Text = handle.Title,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "Aurora.Font.Family");
        text.SetResourceReference(TextBlock.FontSizeProperty, "Aurora.Font.Small");
        var card = new Border
        {
            Child = text,
            Padding = new Thickness(10, 3, 10, 3),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
        };
        // 弹出层不在本窗体的资源链上：把本窗体的字典并进来，颜色照样按令牌引用。
        card.Resources.MergedDictionaries.Add(Resources);
        var ghost = new Popup
        {
            Child = card,
            AllowsTransparency = true,
            Placement = PlacementMode.Absolute,
            IsHitTestVisible = false,
            Focusable = false,
            StaysOpen = true,
        };

        _sceneDrag = new SceneDragSession(handle, ghost, card, text);
        PaintGhost(SceneDropKind.None);
        CaptureMouse();
        ghost.IsOpen = true;
        UpdateSceneDrag(at);
    }

    private void OnSceneDragMouseMove(object sender, MouseEventArgs e)
    {
        if (_sceneDrag == null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // 抬起没送到（系统吞了）：按当前落点收尾，与正常松手一样。
            EndSceneDrag(commit: true);
            return;
        }

        UpdateSceneDrag(e.GetPosition(this));
        e.Handled = true;
    }

    private void OnSceneDragMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_sceneDrag == null)
            return;
        UpdateSceneDrag(e.GetPosition(this));
        EndSceneDrag(commit: true);
        e.Handled = true;
    }

    /// <summary>按指针位置（本窗体坐标）判落点、挪跟手胶囊、画排队线。</summary>
    private void UpdateSceneDrag(Point at)
    {
        if (_sceneDrag is not { } drag)
            return;

        var screen = PointToScreen(at);
        var dpi = VisualTreeHelper.GetDpi(this);
        drag.Ghost.HorizontalOffset = screen.X / dpi.DpiScaleX + SceneGhostOffset.X;
        drag.Ghost.VerticalOffset = screen.Y / dpi.DpiScaleY + SceneGhostOffset.Y;

        var kind = SceneDropKind.None;
        string? before = null;
        if (at.X < 0 || at.Y < 0 || at.X > ActualWidth || at.Y > ActualHeight)
        {
            kind = SceneDropKind.Delete;
        }
        else if (SceneTray.IsVisible && Contains(SceneTray, at))
        {
            kind = SceneDropKind.Tray;
        }
        else if (NavFilter.Length == 0 && Contains(NavScenesScroll, at))
        {
            // 搜索时右栏是筛出来的子集，在子集里排队没有意义。
            kind = SceneDropKind.Reorder;
            before = ReorderAnchor(at, out var lineY);
            ShowDropLine(lineY);
        }

        if (kind != SceneDropKind.Reorder)
            SceneDropLine.Visibility = Visibility.Collapsed;

        drag.Before = before;
        if (drag.Kind != kind)
        {
            drag.Kind = kind;
            PaintGhost(kind);
            PaintTray(kind == SceneDropKind.Tray);
        }
    }

    private bool Contains(FrameworkElement element, Point at)
    {
        if (!element.IsVisible || element.ActualWidth <= 0)
            return false;
        var origin = element.TransformToAncestor(this).Transform(new Point(0, 0));
        return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight)).Contains(at);
    }

    /// <summary>
    /// 指针落在哪两个场景之间：返回下面那个的 id（挪到它前面），落在最后一个的下半截以下返回 null（挪到最后）。
    /// <paramref name="lineY"/> 是排队线的位置（本窗体坐标）。
    /// </summary>
    private string? ReorderAnchor(Point at, out double lineY)
    {
        lineY = 0;
        var items = NavRailItems.Children.OfType<FrameworkElement>().Where(item => item.IsVisible).ToList();
        if (items.Count == 0)
        {
            lineY = NavRailItems.TransformToAncestor(this).Transform(new Point(0, 0)).Y;
            return null;
        }

        foreach (var item in items)
        {
            var top = item.TransformToAncestor(this).Transform(new Point(0, 0)).Y;
            if (at.Y < top + item.ActualHeight / 2)
            {
                lineY = top - 1;
                return (item.Tag as SceneHandle)?.Id;
            }
        }

        var last = items[^1];
        lineY = last.TransformToAncestor(this).Transform(new Point(0, 0)).Y + last.ActualHeight + 1;
        return null;
    }

    private void ShowDropLine(double y)
    {
        var origin = NavRailItems.TransformToAncestor(this).Transform(new Point(0, 0));
        var layer = SceneDropLayer.TransformToAncestor(this).Transform(new Point(0, 0));
        Canvas.SetLeft(SceneDropLine, origin.X - layer.X);
        Canvas.SetTop(SceneDropLine, y - layer.Y - SceneDropLine.Height / 2);
        SceneDropLine.Width = Math.Max(0, NavRailItems.ActualWidth);
        SceneDropLine.Visibility = Visibility.Visible;
    }

    /// <summary>跟手胶囊：平时墨色块；出了窗体变危险色并写明「松开删除」，免得人不知道会发生什么。</summary>
    private void PaintGhost(SceneDropKind kind)
    {
        if (_sceneDrag is not { } drag)
            return;

        var danger = kind == SceneDropKind.Delete;
        var back = danger ? "Aurora.Brush.Danger" : "Aurora.Brush.Ink";
        drag.Card.SetResourceReference(Border.BackgroundProperty, back);
        drag.Card.SetResourceReference(Border.BorderBrushProperty, back);
        drag.Text.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Aurora.Brush.TextOnAccent" : "Aurora.Brush.OnInk");
        drag.Text.Text = kind switch
        {
            SceneDropKind.Delete => $"松开删除 · {drag.Scene.Title}",
            SceneDropKind.Tray when !drag.Scene.InTray => $"收起 · {drag.Scene.Title}",
            _ => drag.Scene.Title,
        };
    }

    /// <summary>拖到小栏上方时小栏描一圈墨色线，表示「松手就收进来」。</summary>
    private void PaintTray(bool armed)
    {
        if (armed)
        {
            SceneTray.SetResourceReference(Border.BorderBrushProperty, "Aurora.Brush.Ink");
            SceneTray.BorderThickness = new Thickness(2);
        }
        else
        {
            SceneTray.SetResourceReference(Border.BorderBrushProperty, "Aurora.Brush.ControlBorder");
            SceneTray.BorderThickness = new Thickness(1);
        }
    }

    private void EndSceneDrag(bool commit)
    {
        if (_sceneDrag is not { } drag)
            return;

        // 先清会话再放捕获：放捕获会打到 LostMouseCapture，那时不该再收一次尾。
        _sceneDrag = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        drag.Ghost.IsOpen = false;
        SceneDropLine.Visibility = Visibility.Collapsed;
        PaintTray(false);

        if (_railStale)
            RefreshNavigatorRail();
        if (commit)
            _ = DropSceneAsync(drag.Scene, drag.Kind, drag.Before);

        // Ctrl 临时进的页面调整：拖动期间按住不退，松手时 Ctrl 已放开就退出。
        if (_labelMode && _labelFollowsCtrl && !IsKeyDown(VirtualKeyControl))
            SetLabelMode(false);
    }

    private async Task DropSceneAsync(SceneHandle scene, SceneDropKind kind, string? before)
    {
        var id = CommandParser.QuoteArg(scene.Id);
        switch (kind)
        {
            case SceneDropKind.Reorder:
                if (scene.InTray)
                    await _bus.ExecuteAsync($"aurora.scene.hide id={id} hidden=false", "UI").ConfigureAwait(true);
                await _bus.ExecuteAsync(
                    before == null
                        ? $"aurora.scene.move id={id}"
                        : $"aurora.scene.move id={id} before={CommandParser.QuoteArg(before)}",
                    "UI").ConfigureAwait(true);
                break;

            case SceneDropKind.Tray:
                if (!scene.InTray)
                    await _bus.ExecuteAsync($"aurora.scene.hide id={id} hidden=true", "UI").ConfigureAwait(true);
                break;

            case SceneDropKind.Delete:
                var derived = _scenes.Find(scene.Id)?.Source == SceneSource.Derived;
                var answer = await _dialogs.ShowAsync(new AuroraDialogRequest
                {
                    Kind = AuroraDialogKind.Confirm,
                    Title = "删除场景",
                    Body = derived
                        ? $"删除场景「{scene.Title}」？\n模块场景删掉后不再列出，模块重新装上也不会回来；要恢复，执行 aurora.scene.reset scene={scene.Id}。"
                        : $"删除场景「{scene.Title}」？\n它的布局一并丢弃，不能撤销。",
                    PrimaryText = "删除",
                    CancelText = "保留",
                    Danger = true,
                    DefaultCancel = true,
                }).ConfigureAwait(true);
                if (answer.Accepted)
                    await _bus.ExecuteAsync($"aurora.scene.delete id={id}", "UI").ConfigureAwait(true);
                break;
        }
    }
}
