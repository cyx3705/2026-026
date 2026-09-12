using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryAurora.Shell.Base.Docking;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace HistoryAurora.Shell.Base;

/// <summary>
/// 页面拖动的唯一入口（1.20.2 起；前身是顶栏协调器 <c>ShellTopBarCoordinator</c>）。
///
/// 顶栏已经整个删掉（REQ-UI-101）：窗格不再画页签行，按页签换页、双击页签专注、拖页签、
/// 拖页头移动窗口一并没有了。页面只剩两种拖法，走同一条 浮出 → 系统移动循环 → 蓝色停靠点 的路：
/// <list type="bullet">
///   <item>Ctrl 标签态（REQ-UI-097）：按住 Ctrl，每一格窗格盖上写着页名的标签，按住标签拖走整页；
///         浮窗里只有这一页时，拖的是那个浮窗本身；</item>
///   <item>右栏常用页面胶囊（REQ-UI-099）：按住拖出来。</item>
/// </list>
/// 拖出去没落到停靠点的页隐藏（REQ-UI-098）。
/// </summary>
internal sealed partial class PageDragCoordinator : IDisposable
{
    private const string ChromeLogSource = "shell.chrome";

    private readonly Window _window;
    private readonly DockingManager _manager;
    private readonly DockingHost _docking;
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly WindowDragDriver _windowDragDriver = new();

    private long _dragSequence;
    private DockingDragSession? _dragSession;
    private bool _disposed;

    public PageDragCoordinator(
        Window window,
        DockingManager manager,
        DockingHost docking,
        CommandBus bus,
        IShellLog log)
    {
        _window = window;
        _manager = manager;
        _docking = docking;
        _bus = bus;
        _log = log;
        _manager.LayoutFloatingWindowControlCreated += OnFloatingWindowCreated;
        _manager.AddHandler(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnDockPreviewMouseMove),
            handledEventsToo: true);
        _manager.AddHandler(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnDockPreviewMouseLeftButtonUp),
            handledEventsToo: true);
    }

    /// <summary>Ctrl 标签态（REQ-UI-097）。打开时，盖在窗格上的页名标签才起拖动会话。开关由装配根按 Ctrl 管。</summary>
    public bool LabelMode { get; set; }

    /// <summary>有一个拖动会话在途。装配根据此推迟退出标签态——松开 Ctrl 不该掐断正在拖的那一页。</summary>
    public bool IsDragging => _dragSession != null;

    /// <summary>拖动会话结束（完成或取消）。</summary>
    public event EventHandler? DragFinished;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        CancelDragSession("coordinator disposed");

        _manager.LayoutFloatingWindowControlCreated -= OnFloatingWindowCreated;
        _manager.RemoveHandler(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnDockPreviewMouseMove));
        _manager.RemoveHandler(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnDockPreviewMouseLeftButtonUp));

        foreach (var floating in _manager.FloatingWindows.OfType<LayoutFloatingWindowControl>())
            floating.StateChanged -= OnFloatingWindowStateChanged;
    }

    /// <summary>窗格上的按下：只认标签态下按在页名标签上的那一下，其余一律归页面内容自己。</summary>
    public void HandlePaneMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
    {
        if (!LabelMode ||
            e.ChangedButton != MouseButton.Left ||
            sender is not FrameworkElement pane ||
            FindAncestor<FrameworkElement>(e.OriginalSource as DependencyObject, IsPageLabelCover) is not { } cover ||
            !TryResolvePageId(cover, out var id))
        {
            return;
        }

        BeginLabelDrag(pane, cover, id, e);
    }

    public void HandlePaneMouseMove(object? sender, MouseEventArgs e)
        => ContinueHostWindowGesture(sender, e);

    public void HandlePaneMouseLeftButtonUp(object? sender, MouseButtonEventArgs e)
        => CompleteHostWindowGesture(sender);

    public void HandlePaneLostMouseCapture(object? sender, MouseEventArgs e)
        => ClearHostWindowGesture(sender);

    /// <summary>
    /// 从主窗体之外拖一页进来——右栏的常用页面胶囊（REQ-UI-099）。
    ///
    /// 调用方已经判过拖动阈值，会话直接从「过阈值」起步。<paramref name="anchor"/> 是指针
    /// 在新浮窗里的落点。
    /// </summary>
    public void BeginExternalPageDrag(string id, FrameworkElement surface, Point anchor)
    {
        if (_disposed)
            return;

        CancelDragSession("external page drag");
        var start = FloatingWindowGeometry.GetCursorPosition();
        var session = new DockingDragSession(
            ++_dragSequence,
            DockingDragKind.Tab,
            surface,
            start,
            anchor,
            id,
            null,
            false);
        _dragSession = session;
        if (!session.TryTransition(DockingDragState.ThresholdReached))
            return;
        session.LastScreenPoint = start;
        QueueRestoreAndFloat(session);
    }

    private static bool IsPageLabelCover(FrameworkElement element)
        => Equals(element.Tag, PageLabelMode.CoverTag);

    /// <summary>
    /// 标签态下按在页名标签上：整页拖走。浮窗里只有这一页时拖的是那个浮窗本身；
    /// 浮窗里有好几格时把这一格拆成独立浮窗。指针在新浮窗里的落点取它在原窗格里的位置。
    /// </summary>
    private void BeginLabelDrag(FrameworkElement pane, FrameworkElement cover, string id, MouseButtonEventArgs e)
    {
        var floating = FindFloatingWindow(id);
        if (floating != null)
        {
            if (CountFloatingPages(floating) > 1)
                StartPageSession(cover, id, GetScreenPoint(cover, e), e.GetPosition(pane), floating);
            else
                BeginHostWindowGesture(floating, pane, e);
            e.Handled = true;
            return;
        }

        StartPageSession(cover, id, GetScreenPoint(cover, e), e.GetPosition(pane), null);
        e.Handled = true;
    }

    /// <summary>
    /// 拖出去的页没有落在停靠点上（REQ-UI-098）：隐藏它。悬浮页很少单独用（用户拍板）。
    /// 放到 ContextIdle：AvalonDock 在系统移动循环收尾时才落停靠，等它落完再看页还在不在浮窗里。
    /// </summary>
    private void ParkIfStillFloating(string? id)
    {
        if (id == null || _disposed || _window.Dispatcher.HasShutdownStarted)
            return;

        _window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (_disposed || FindFloatingWindow(id) == null)
                return;
            try
            {
                _docking.ParkHidden(id);
                _log.Info(ChromeLogSource, $"页面 {id} 没有落在停靠点上，已隐藏；右栏常用页面里可以再拖回来");
            }
            catch (Exception ex)
            {
                _log.Error(ChromeLogSource, $"隐藏拖出的页面 {id} 失败：{ex.Message}");
            }
        });
    }

    internal static bool HasReachedDragThreshold(
        Point start,
        Point current,
        double horizontalThreshold,
        double verticalThreshold,
        double multiplier = 1)
        => Math.Abs(current.X - start.X) >= horizontalThreshold * multiplier ||
           Math.Abs(current.Y - start.Y) >= verticalThreshold * multiplier;

    internal CommandResult SetFloatingWindowState(string id, string state)
    {
        var floating = FindFloatingWindow(id);
        if (floating == null)
            return CommandResult.Fail($"页面 {id} 没有对应的独立浮窗宿主");

        var targetState = state.ToLowerInvariant() switch
        {
            "maximized" => (WindowState?)WindowState.Maximized,
            "normal" => WindowState.Normal,
            "toggle" => GetToggledWindowState(floating.WindowState),
            _ => null,
        };
        if (targetState == null)
            return CommandResult.Fail($"不支持的浮窗状态: {state}");
        if (targetState == WindowState.Maximized)
            SystemCommands.MaximizeWindow(floating);
        else
            SystemCommands.RestoreWindow(floating);
        var label = targetState == WindowState.Maximized ? "最大化" : "普通";
        return CommandResult.Ok($"{id} 独立浮窗已切换为{label}状态");
    }

    /// <summary>从窗格里任一点认出这一格的那一页（一格只有一页，就是窗格的选中页）。</summary>
    public bool TryResolvePageId(DependencyObject? source, out string id)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is LayoutAnchorablePaneControl anchorablePane &&
                TryResolveSelectedItem(anchorablePane.SelectedItem, out id))
            {
                return true;
            }

            if (current is LayoutDocumentPaneControl documentPane &&
                TryResolveSelectedItem(documentPane.SelectedItem, out id))
            {
                return true;
            }
        }

        id = string.Empty;
        return false;
    }

    private void StartPageSession(
        FrameworkElement surface,
        string id,
        Point start,
        Point anchor,
        LayoutFloatingWindowControl? floating)
    {
        CancelDragSession("new page press");
        var session = new DockingDragSession(
            ++_dragSequence,
            DockingDragKind.Tab,
            surface,
            start,
            anchor,
            id,
            floating,
            false)
        {
            IsFloatingTab = floating != null,
        };
        _dragSession = session;
        surface.CaptureMouse();
    }

    private async Task RestoreAndFloatAsync(DockingDragSession session)
    {
        if (!session.IsTab || session.PageId == null || !ReferenceEquals(_dragSession, session))
            return;

        var id = session.PageId;
        if (_docking.MaximizedId != null)
        {
            var restored = await _bus.ExecuteAsync("aurora.ui.restore", "UI").ConfigureAwait(true);
            if (!restored.Success)
            {
                CompleteDragSession(session, "restore command failed", cancelled: true);
                _log.Error(ChromeLogSource, $"恢复专注布局失败：{restored.Message}");
                return;
            }

            await _window.Dispatcher.InvokeAsync(
                _window.UpdateLayout,
                DispatcherPriority.Loaded);
        }

        var context = new FloatingDragContext(
            id,
            ResolveEmbeddedPaneSize(id),
            session.Anchor,
            ContinueWithDrag: true)
        {
            SessionId = session.Id,
            PointerPixels = FloatingWindowGeometry.GetCursorPosition(),
        };
        if (!session.TryTransition(DockingDragState.FloatRequested))
            return;

        // 浮出去会把这一页从可视树上摘掉。摘之前必须先放掉捕获:捕获还在的话,
        // 后续鼠标移动仍旧送到那个已经断开 PresentationSource 的元素上。
        // 拖动的接力从这里起就交给 WindowDragDriver(见 OnFloatingWindowCreated)。
        WindowDragDriver.ReleaseMouseCapture(session.Surface);
        session.LastScreenPoint = context.PointerPixels;
        ApplyFloatingModelGeometry(context, FindLayoutContent(context.PageId));
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Completion = completion;
        var floated = session.IsFloatingTab
            ? await DetachFloatingPageAsync(session).ConfigureAwait(true)
            : await _bus.ExecuteAsync(
                $"aurora.ui.float name={CommandParser.QuoteArg(id)}", "UI").ConfigureAwait(true);
        if (!floated.Success)
        {
            CompleteDragSession(session, "float command failed", cancelled: true);
            _log.Error(ChromeLogSource, $"拖出页面失败：{floated.Message}");
            return;
        }

        var completed = await Task.WhenAny(
            completion.Task,
            Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(true);
        if (!ReferenceEquals(completed, completion.Task))
        {
            CompleteDragSession(session, "floating host timeout", cancelled: true);
            _log.Error(ChromeLogSource, $"拖出页面失败：未创建页面 {id} 的浮窗宿主");
        }
    }

    private void OnFloatingWindowCreated(object? sender, LayoutFloatingWindowControlCreatedEventArgs e)
    {
        var created = e.LayoutFloatingWindowControl;
        created.StateChanged += OnFloatingWindowStateChanged;
        ApplyFloatingWindowStateChrome(created);

        if (_dragSession is not { IsTab: true, PageId: { } pageId } session ||
            session.State != DockingDragState.FloatRequested ||
            !ModelContainsPage(created.Model, pageId))
            return;

        session.TryTransition(DockingDragState.FloatingReady);
        session.Completion?.TrySetResult(true);
        session.Completion = null;
        var context = new FloatingDragContext(
            pageId,
            ResolveEmbeddedPaneSize(pageId),
            session.Anchor,
            ContinueWithDrag: true)
        {
            SessionId = session.Id,
            PointerPixels = session.LastScreenPoint == default
                ? FloatingWindowGeometry.GetCursorPosition()
                : session.LastScreenPoint,
        };
        ApplyFloatingWindowGeometry(created, context);
        if (session.IsLeftButtonDown && !session.ButtonReleased)
        {
            QueueWindowDrag(created, session);
        }
        else
        {
            CompleteDragSession(session, "button released before floating host was ready");
            ParkIfStillFloating(pageId);
        }
    }

    private LayoutFloatingWindowControl? FindFloatingWindow(string id)
        => _manager.FloatingWindows
            .OfType<LayoutFloatingWindowControl>()
            .FirstOrDefault(window => ModelContainsPage(window.Model, id));

    private static int CountFloatingPages(LayoutFloatingWindowControl floating)
        => floating.Model.Descendents().OfType<LayoutContent>().Count();

    /// <summary>浮窗里有好几格时，把被拖的这一格拆成独立浮窗。</summary>
    private Task<CommandResult> DetachFloatingPageAsync(DockingDragSession session)
    {
        if (session.PageId == null)
            return Task.FromResult(CommandResult.Fail("浮窗页缺少页面 ID"));

        try
        {
            var content = FindLayoutContent(session.PageId);
            if (content == null || content.Parent is not ILayoutContainer parent)
                return Task.FromResult(CommandResult.Fail($"页面 {session.PageId} 不在可拆分的浮窗中"));

            var size = NormalizeEmbeddedSize(ResolveEmbeddedPaneSize(session.PageId));
            parent.RemoveChild(content);
            content.IsSelected = true;
            content.FloatingWidth = size.Width;
            content.FloatingHeight = size.Height;
            _manager.CreateFloatingWindow(content, false);
            return Task.FromResult(CommandResult.Ok($"页面 {session.PageId} 已拆分为独立浮窗"));
        }
        catch (Exception ex)
        {
            _log.Error(ChromeLogSource, $"拆分浮窗页失败：{ex.Message}");
            return Task.FromResult(CommandResult.Fail(ex.Message));
        }
    }

    private void QueueWindowDrag(LayoutFloatingWindowControl floating, DockingDragSession session)
        => _window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!ReferenceEquals(_dragSession, session) || session.ButtonReleased)
            {
                CompleteDragSession(session, "session superseded before window drag");
                return;
            }

            if (!session.TryTransition(DockingDragState.WindowMoving))
                return;
            _windowDragDriver.Start(floating);
            CompleteDragSession(session, "floating window drag ended");
            ParkIfStillFloating(session.PageId);
        });

    private void OnFloatingWindowStateChanged(object? sender, EventArgs e)
    {
        if (sender is LayoutFloatingWindowControl floating)
            ApplyFloatingWindowStateChrome(floating);
    }

    private static void ApplyFloatingWindowStateChrome(LayoutFloatingWindowControl floating)
        => floating.Padding = floating.WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : default;

    /// <summary>单页浮窗的标签被按住：拖的是这个浮窗本身，过阈值才进系统移动循环。</summary>
    private void BeginHostWindowGesture(
        Window hostWindow,
        FrameworkElement surface,
        MouseButtonEventArgs e)
    {
        if (_dragSession is
            {
                Kind: DockingDragKind.Window,
                State: DockingDragState.Pressed,
                HostWindow: { } currentHost,
            } && ReferenceEquals(currentHost, hostWindow))
        {
            e.Handled = true;
            return;
        }

        CancelDragSession("new window press");
        var session = new DockingDragSession(
            ++_dragSequence,
            DockingDragKind.Window,
            surface,
            GetScreenPoint(surface, e),
            e.GetPosition(surface),
            null,
            hostWindow,
            hostWindow.WindowState == WindowState.Maximized);
        _dragSession = session;
        surface.CaptureMouse();
        e.Handled = true;
    }

    private void ContinueHostWindowGesture(object? sender, MouseEventArgs e)
    {
        if (_dragSession is not { Kind: DockingDragKind.Window, State: DockingDragState.Pressed } session ||
            !ReferenceEquals(sender, session.Surface))
        {
            return;
        }

        var current = FloatingWindowGeometry.GetCursorPosition();
        var multiplier = session.WasMaximized ? 2d : 1d;
        if (!HasReachedDragThreshold(
                session.Start,
                current,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance,
                multiplier))
        {
            return;
        }

        StartPendingHostDrag(session, current);
        e.Handled = true;
    }

    private void CompleteHostWindowGesture(object? sender)
    {
        if (_dragSession is { Kind: DockingDragKind.Window } session &&
            ReferenceEquals(sender, session.Surface))
        {
            session.MarkReleased(FloatingWindowGeometry.GetCursorPosition());
            if (session.State == DockingDragState.Pressed)
                CompleteDragSession(session, "button released before window threshold", cancelled: true);
        }
    }

    private void ClearHostWindowGesture(object? sender)
    {
        if (_dragSession is { Kind: DockingDragKind.Window } session &&
            ReferenceEquals(sender, session.Surface))
        {
            // 过阈值时会主动放掉捕获再进系统移动循环；那一下的 LostMouseCapture 不是取消。
            if (session.State == DockingDragState.Pressed)
                CompleteDragSession(session, "mouse capture lost", cancelled: true);
        }
    }

    private void StartPendingHostDrag(DockingDragSession session, Point pointerPixels)
    {
        if (!ReferenceEquals(_dragSession, session) || session.HostWindow == null)
            return;

        var hostWindow = session.HostWindow;
        if (session.State == DockingDragState.Pressed &&
            !session.TryTransition(DockingDragState.ThresholdReached))
        {
            return;
        }
        if (!session.TryTransition(DockingDragState.WindowMoving))
            return;
        session.LastScreenPoint = pointerPixels;
        WindowDragDriver.ReleaseMouseCapture(session.Surface);

        if (session.WasMaximized)
        {
            RestoreHostWindowUnderPointer(hostWindow, session.Surface, session.Anchor, pointerPixels);
            return;
        }

        _windowDragDriver.Start(hostWindow);
        CompleteDragSession(session, "window drag ended");
    }

    private void OnDockPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSession is not { IsTab: true, State: DockingDragState.Pressed } session)
            return;

        var current = FloatingWindowGeometry.GetCursorPosition();
        if (!HasReachedDragThreshold(
                session.Start,
                current,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        if (!session.TryTransition(DockingDragState.ThresholdReached))
            return;
        session.LastScreenPoint = current;
        WindowDragDriver.ReleaseMouseCapture(session.Surface);
        // 过阈值才浮出：浮窗有了真正的系统移动循环，蓝色停靠点才会出现，落点才确定。
        QueueRestoreAndFloat(session);
        e.Handled = true;
    }

    private void QueueRestoreAndFloat(DockingDragSession session)
    {
        // 让 AvalonDock 先走完这一次鼠标路由再改布局：在 PreviewMouseMove 里直接建浮窗
        // 会重入焦点钩子，钩子去查另一个调度器的可视元素，能把宿主整个带走。
        if (_disposed || _window.Dispatcher.HasShutdownStarted)
            return;

        _window.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => _ = RestoreAndFloatAsync(session)));
    }

    private void OnDockPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragSession is not { } session)
            return;

        session.MarkReleased(FloatingWindowGeometry.GetCursorPosition());
        if (session.State == DockingDragState.Pressed)
            CompleteDragSession(session, "button released before threshold", cancelled: true);
    }

    private static Point GetScreenPoint(FrameworkElement surface, MouseButtonEventArgs e)
        => surface.PointToScreen(e.GetPosition(surface));

    private void RestoreHostWindowUnderPointer(
        Window hostWindow,
        FrameworkElement surface,
        Point local,
        Point pointerPixels)
    {
        var restore = hostWindow.RestoreBounds;
        var width = double.IsFinite(restore.Width) && restore.Width > 0 ? restore.Width : hostWindow.Width;
        var height = double.IsFinite(restore.Height) && restore.Height > 0 ? restore.Height : hostWindow.Height;
        var fraction = surface.ActualWidth > 0
            ? Math.Clamp(local.X / surface.ActualWidth, 0, 1)
            : 0.5;

        try
        {
            FloatingWindowGeometry.PlaceWindow(
                hostWindow,
                pointerPixels,
                new Size(width, height),
                new Point(width * fraction, Math.Min(local.Y, 24)));
            hostWindow.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () =>
                {
                    if (_dragSession is { Kind: DockingDragKind.Window } current &&
                        ReferenceEquals(current.HostWindow, hostWindow) &&
                        current.IsLeftButtonDown)
                    {
                        _windowDragDriver.Start(hostWindow);
                        CompleteDragSession(current, "restored window drag ended");
                    }
                });
        }
        catch (InvalidOperationException ex)
        {
            _log.Warn(ChromeLogSource, $"浮窗最大化下拖恢复失败：{ex.Message}");
        }
    }

    internal static WindowState GetToggledWindowState(WindowState state)
        => state == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CancelDragSession(string reason)
    {
        if (_dragSession is not { } session)
            return;
        CompleteDragSession(session, reason, cancelled: true);
    }

    private void CompleteDragSession(
        DockingDragSession session,
        string reason,
        bool cancelled = false)
    {
        WindowDragDriver.ReleaseMouseCapture(session.Surface);
        session.TryTransition(cancelled ? DockingDragState.Cancelled : DockingDragState.Completed);
        session.Completion?.TrySetResult(!cancelled);
        session.Completion = null;
        if (ReferenceEquals(_dragSession, session))
        {
            _dragSession = null;
            DragFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>这一页此刻所在窗格的尺寸，浮出去的窗口按它开。</summary>
    private Size ResolveEmbeddedPaneSize(string id)
    {
        var pane = FindVisualDescendants<FrameworkElement>(_manager)
            .FirstOrDefault(element =>
                element is LayoutAnchorablePaneControl or LayoutDocumentPaneControl &&
                TryResolvePageId(element, out var candidate) &&
                candidate.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (pane == null ||
            !double.IsFinite(pane.ActualWidth) || pane.ActualWidth <= 0 ||
            !double.IsFinite(pane.ActualHeight) || pane.ActualHeight <= 0)
        {
            return default;
        }

        return new Size(pane.ActualWidth, pane.ActualHeight);
    }

    private static void ApplyFloatingModelGeometry(
        FloatingDragContext context,
        LayoutContent? content)
    {
        if (content == null)
            return;

        var size = NormalizeEmbeddedSize(context.EmbeddedSize);
        var pointer = context.PointerPixels == default
            ? FloatingWindowGeometry.GetCursorPosition()
            : context.PointerPixels;
        content.FloatingWidth = size.Width;
        content.FloatingHeight = size.Height;
        content.FloatingLeft = pointer.X - context.AnchorOffset.X;
        content.FloatingTop = pointer.Y - context.AnchorOffset.Y;
    }
}
