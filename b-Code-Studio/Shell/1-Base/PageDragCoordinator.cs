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
///   <item>Ctrl 标签态（REQ-UI-097）：按住 Ctrl，每一格窗格盖上写着页名的标签，按住标签拖走整页；</item>
///   <item>右栏常用页面胶囊（REQ-UI-099）：按住拖出来。</item>
/// </list>
/// 拖出去没落到停靠点的页隐藏（REQ-UI-098）。
///
/// 1.22（REQ-UI-120）起没有独立浮窗：浮出只是拖动途中的载体，拖完要么落进一格、要么隐藏。
/// 「按住已经浮着的单页浮窗整窗移动」「多页浮窗拆出一页」「浮窗最大化」三条路因此一起删掉。
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
    }

    /// <summary>
    /// 窗格上的按下：只认标签态下按在页名标签上的那一下，其余一律归页面内容自己。
    /// 整页拖走，指针在浮出载体里的落点取它在原窗格里的位置。
    /// </summary>
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

        StartPageSession(cover, id, GetScreenPoint(cover, e), e.GetPosition(pane));
        e.Handled = true;
    }

    /// <summary>
    /// 从主窗体之外拖一页进来——右栏的常用页面胶囊（REQ-UI-099）。
    ///
    /// 调用方已经判过拖动阈值，会话直接从「过阈值」起步。<paramref name="anchor"/> 是指针
    /// 在浮出载体里的落点。
    /// </summary>
    public void BeginExternalPageDrag(string id, FrameworkElement surface, Point anchor)
    {
        if (_disposed)
            return;

        CancelDragSession("external page drag");
        var start = FloatingWindowGeometry.GetCursorPosition();
        var session = new DockingDragSession(++_dragSequence, surface, start, anchor, id);
        _dragSession = session;
        if (!session.TryTransition(DockingDragState.ThresholdReached))
            return;
        session.LastScreenPoint = start;
        QueueRestoreAndFloat(session);
    }

    private static bool IsPageLabelCover(FrameworkElement element)
        => Equals(element.Tag, PageLabelMode.CoverTag);

    /// <summary>
    /// 拖出去的页没有落在停靠点上（REQ-UI-098）：隐藏它。
    /// 放到 ContextIdle：AvalonDock 在系统移动循环收尾时才落停靠，等它落完再看页还在不在载体里。
    /// </summary>
    private void ParkIfStillFloating(string id)
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted)
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
        double verticalThreshold)
        => Math.Abs(current.X - start.X) >= horizontalThreshold ||
           Math.Abs(current.Y - start.Y) >= verticalThreshold;

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

    private void StartPageSession(FrameworkElement surface, string id, Point start, Point anchor)
    {
        CancelDragSession("new page press");
        var session = new DockingDragSession(++_dragSequence, surface, start, anchor, id);
        _dragSession = session;
        surface.CaptureMouse();
    }

    private async Task RestoreAndFloatAsync(DockingDragSession session)
    {
        if (!ReferenceEquals(_dragSession, session))
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
        try
        {
            // 浮出只作拖动载体（REQ-UI-120），直接调停靠层——aurora.ui.float 随独立浮窗删除。
            _docking.FloatForDrag(id);
        }
        catch (Exception ex)
        {
            CompleteDragSession(session, "float failed", cancelled: true);
            _log.Error(ChromeLogSource, $"拖出页面失败：{ex.Message}");
            return;
        }

        var completed = await Task.WhenAny(
            completion.Task,
            Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(true);
        if (!ReferenceEquals(completed, completion.Task))
        {
            CompleteDragSession(session, "floating host timeout", cancelled: true);
            _log.Error(ChromeLogSource, $"拖出页面失败：未创建页面 {id} 的浮出载体");
        }
    }

    private void OnFloatingWindowCreated(object? sender, LayoutFloatingWindowControlCreatedEventArgs e)
    {
        var created = e.LayoutFloatingWindowControl;
        if (_dragSession is not { } session ||
            session.State != DockingDragState.FloatRequested ||
            !ModelContainsPage(created.Model, session.PageId))
            return;

        var pageId = session.PageId;
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

    private void OnDockPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSession is not { State: DockingDragState.Pressed } session)
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
        // 过阈值才浮出：载体有了真正的系统移动循环，蓝色停靠点才会出现，落点才确定。
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

    /// <summary>这一页此刻所在窗格的尺寸，浮出去的载体按它开。</summary>
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
