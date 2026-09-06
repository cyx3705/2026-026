using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryAurora.Shell.Base.Docking;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace HistoryAurora.Shell.Base;

/// <summary>
/// Keeps main-window chrome and page chrome on one input and command path.
/// Only AvalonDock public model and window APIs are used here.
/// </summary>
internal sealed partial class ShellTopBarCoordinator : IDisposable
{
    private const string ChromeLogSource = "shell.chrome";

    private readonly Window _window;
    private readonly DockingManager _manager;
    private readonly DockingHost _docking;
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly RoutedCommand _pageActionCommand;
    private readonly HashSet<LayoutDocumentPaneControl> _documentPanes = [];
    private readonly List<(UIElement Target, CommandBinding Binding)> _pageActionBindings = [];
    private readonly FastDoubleClickGesture _doubleClick = new(TimeSpan.FromMilliseconds(250));
    private readonly WindowDragDriver _windowDragDriver = new();
    private readonly bool _enableMaximizeOnDoubleClick;

    private long _dragSequence;
    private DockingDragSession? _dragSession;
    private bool _disposed;

    static ShellTopBarCoordinator()
    {
        // 页签左键由 Aurora 独占。只在隧道阶段判定 Handled 是不够的:冒泡阶段 WPF 仍会把
        // MouseDown 就地升发成 MouseLeftButtonDown,AvalonDock 的
        // LayoutDocumentTabItem.OnMouseLeftButtonDown 照跑不误——真机 2026-09-06 就是在那里
        // 对一个刚被换页回收掉的页签解引用 Model,抛 NullReferenceException,整条路由中断:
        // 页换不成,捕获又留在原地,于是「点页签变成拖窗口」。中央页与工具页两种页签都会中招。
        //
        // 类处理器按派生类优先调用,而 AvalonDock 那个实现是 UIElement 上注册的虚方法转发器
        // (handledEventsToo:false),所以在这两个类型上判定 Handled 就能整条断掉。
        foreach (var tabType in new[] { typeof(LayoutDocumentTabItem), typeof(LayoutAnchorableTabItem) })
        {
            EventManager.RegisterClassHandler(
                tabType,
                UIElement.MouseLeftButtonDownEvent,
                new MouseButtonEventHandler(static (_, e) => e.Handled = true));
        }
    }

    public ShellTopBarCoordinator(
        Window window,
        DockingManager manager,
        DockingHost docking,
        CommandBus bus,
        IShellLog log,
        RoutedCommand pageActionCommand,
        bool enableMaximizeOnDoubleClick = true)
    {
        _window = window;
        _manager = manager;
        _docking = docking;
        _bus = bus;
        _log = log;
        _pageActionCommand = pageActionCommand;
        _enableMaximizeOnDoubleClick = enableMaximizeOnDoubleClick;
        _manager.LayoutFloatingWindowControlCreated += OnFloatingWindowCreated;
        _manager.AddHandler(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnDockTabMouseLeftButtonDown),
            handledEventsToo: true);
        _manager.AddHandler(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnDockPreviewMouseMove),
            handledEventsToo: true);
        _manager.AddHandler(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnDockPreviewMouseLeftButtonUp),
            handledEventsToo: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        CancelDragSession("coordinator disposed");

        _manager.LayoutFloatingWindowControlCreated -= OnFloatingWindowCreated;
        _manager.RemoveHandler(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnDockTabMouseLeftButtonDown));
        _manager.RemoveHandler(
            UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnDockPreviewMouseMove));
        _manager.RemoveHandler(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnDockPreviewMouseLeftButtonUp));

        foreach (var floating in _manager.FloatingWindows.OfType<LayoutFloatingWindowControl>())
            floating.StateChanged -= OnFloatingWindowStateChanged;
        _documentPanes.Clear();
        foreach (var (target, binding) in _pageActionBindings)
            target.CommandBindings.Remove(binding);
        _pageActionBindings.Clear();
    }

    public void AttachPaneCommandBinding(UIElement pane)
    {
        AttachPageActionBinding(pane);
        if (pane is LayoutDocumentPaneControl documentPane)
        {
            _documentPanes.Add(documentPane);
            UpdateDocumentPaneChrome(documentPane);
        }
    }

    public void Refresh()
    {
        foreach (var pane in _documentPanes.ToArray())
        {
            if (!pane.IsLoaded)
            {
                _documentPanes.Remove(pane);
                continue;
            }
            UpdateDocumentPaneChrome(pane);
        }
    }

    public void HandlePaneMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var floating = FindAncestor<LayoutFloatingWindowControl>(source);
        var sourceIsTab = FindAncestor<DependencyObject>(source, item =>
            item is LayoutAnchorableTabItem or LayoutDocumentTabItem) != null;
        if (e.ChangedButton != MouseButton.Left ||
            sender is not FrameworkElement pane ||
            !IsPaneHeaderSource(source) ||
            !TryResolvePageId(pane, out var id))
        {
            return;
        }

        var floatingWindow = floating ?? FindFloatingWindow(id);
        var tab = FindAncestor<FrameworkElement>(source, IsRealPageTab);
        if (IsInteractiveInPaneHeader(source) &&
            !ShouldHandlePaneHeaderInput(
                floatingWindow != null,
                sourceIsTab,
                IsInteractiveCommandControl(source)))
        {
            return;
        }

        if (floatingWindow is not null)
        {
            if (sourceIsTab && tab != null && CountFloatingPages(floatingWindow) > 1)
                StartFloatingTabSession(tab, id, floatingWindow, e);
            else
                BeginHostWindowGesture(floatingWindow, pane, $"floating:{id}", e);
            return;
        }

        BeginHostWindowGesture(_window, pane, $"header:{id}", e);
    }

    public void HandlePaneMouseMove(object? sender, MouseEventArgs e)
        => ContinueHostWindowGesture(sender, e);

    public void HandlePaneMouseLeftButtonUp(object? sender, MouseButtonEventArgs e)
        => CompleteHostWindowGesture(sender);

    public void HandlePaneLostMouseCapture(object? sender, MouseEventArgs e)
        => ClearHostWindowGesture(sender);

    public void HandleMainMouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            IsInteractiveInPaneHeader(e.OriginalSource as DependencyObject) ||
            sender is not FrameworkElement surface ||
            !IsPaneHeaderSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var target = TryResolvePageId(surface, out var id) ? $"header:{id}" : "header:main";
        BeginHostWindowGesture(_window, surface, target, e);
    }

    public void HandleMainMouseMove(object? sender, MouseEventArgs e)
        => ContinueHostWindowGesture(sender, e);

    public void HandleMainMouseLeftButtonUp(object? sender, MouseButtonEventArgs e)
        => CompleteHostWindowGesture(sender);

    public void HandleMainLostMouseCapture(object? sender, MouseEventArgs e)
        => ClearHostWindowGesture(sender);

    public bool HandleDockTabMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (e.ChangedButton != MouseButton.Left ||
            IsInteractiveCommandControl(source) ||
            !TryResolveTabPageId(source, out var id) ||
            FindAncestor<FrameworkElement>(source, IsRealPageTab) is not { } tab)
        {
            if (e.ChangedButton == MouseButton.Left)
                CancelDragSession("non-tab press");
            return false;
        }

        // 页签的左键手势只能有一个主人,AvalonDock 那一路由类处理器断掉
        // (见 static 构造函数)。选中/激活本来由它顺手做,这里必须自己补上,
        // 否则点页签换不了页——但**必须排在抓取之后**:换页会让 TabControl 重建容器,
        // 被点的那个页签当场作废,先选后抓就抓在一个已经死掉的元素上。
        e.Handled = true;

        var floating = FindFloatingWindow(id);
        if (floating != null)
        {
            if (_dragSession is { IsTab: true, PageId: { } currentId } active &&
                ReferenceEquals(active.Surface, tab) &&
                currentId.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (CountFloatingPages(floating) > 1)
            {
                StartFloatingTabSession(tab, id, floating, e);
                ActivateTabPage(id);
                return false;
            }

            ActivateTabPage(id);
            return false;
        }

        var target = $"tab:{id}";
        var screenPoint = GetScreenPoint(tab, e);
        if (_enableMaximizeOnDoubleClick)
        {
            var range = GetSystemDoubleClickRange(_manager);
            if (_doubleClick.RegisterPress(
                    target,
                    Environment.TickCount64,
                    screenPoint,
                    range.Width,
                    range.Height))
            {
                CancelDragSession("double click");
                _ = _bus.ExecuteAsync(
                    _docking.MaximizedId?.Equals(id, StringComparison.OrdinalIgnoreCase) == true
                        ? "aurora.ui.restore"
                        : $"aurora.ui.max name={CommandParser.QuoteArg(id)}",
                    "UI");
                e.Handled = true;
                return true;
            }
        }

        StartTabSession(tab, id, screenPoint, e.GetPosition(tab));
        ActivateTabPage(id);
        return false;
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

    public bool TryResolvePageId(DependencyObject? source, out string id)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (TryGetTabModel(current, out var tabModel) && TryGetContentId(tabModel, out id))
                return true;

            if (current is TabItem { DataContext: LayoutContent outerModel } &&
                TryGetContentId(outerModel, out id))
            {
                return true;
            }

            if (current is AnchorablePaneTitle { Model: LayoutContent titleModel } &&
                TryGetContentId(titleModel, out id))
            {
                return true;
            }

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

    private void AttachPageActionBinding(UIElement target)
    {
        if (target.CommandBindings.OfType<CommandBinding>().Any(binding =>
                ReferenceEquals(binding.Command, _pageActionCommand)))
        {
            return;
        }

        var binding = CreatePageActionBinding();
        target.CommandBindings.Add(binding);
        _pageActionBindings.Add((target, binding));
    }

    private CommandBinding CreatePageActionBinding()
        => new(
            _pageActionCommand,
            OnPageActionExecuted,
            (_, e) => e.CanExecute = e.Parameter is string);

    private async void OnPageActionExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var action = e.Parameter as string;
        if (action == null)
            return;

        string command;
        if (action.Equals("restore", StringComparison.OrdinalIgnoreCase))
        {
            command = "aurora.ui.restore";
        }
        else
        {
            if (!TryResolvePageId(e.OriginalSource as DependencyObject, out var id) &&
                !TryResolvePageId(e.Source as DependencyObject, out id))
            {
                _log.Error(ChromeLogSource, $"页面动作 {action} 无法解析页面 ID");
                return;
            }

            var quotedId = CommandParser.QuoteArg(id);
            if (action.Equals("toggle-floating", StringComparison.OrdinalIgnoreCase))
                command = $"aurora.ui.floatstate name={quotedId} state=toggle";
            else
                command = action.ToLowerInvariant() switch
                {
                    "float" => $"aurora.ui.float name={quotedId}",
                    "hide" => $"aurora.ui.hide name={quotedId}",
                    "dock-document" => $"aurora.ui.dock name={quotedId} pos=center",
                    "autohide" => $"aurora.ui.autohide name={quotedId}",
                    _ => string.Empty,
                };
        }

        if (string.IsNullOrEmpty(command))
        {
            _log.Error(ChromeLogSource, $"未知页面动作：{action}");
            return;
        }

        var result = await _bus.ExecuteAsync(command, "UI").ConfigureAwait(true);
        if (!result.Success)
            _log.Error(ChromeLogSource, $"页面动作执行失败：{result.Message}");
        e.Handled = true;
    }

    private void OnDockTabMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => HandleDockTabMouseLeftButtonDown(e);

    private void ActivateTabPage(string id)
    {
        if (FindLayoutContent(id) is not { } content)
            return;
        content.IsSelected = true;
        content.IsActive = true;
    }

    private void StartTabSession(
        FrameworkElement tab,
        string id,
        Point start,
        Point anchor)
    {
        CancelDragSession("new tab press");
        var session = new DockingDragSession(
            ++_dragSequence,
            DockingDragKind.Tab,
            tab,
            start,
            anchor,
            id,
            null,
            $"tab:{id}",
        false,
        false);
        _dragSession = session;
        tab.CaptureMouse();
    }

    private void StartFloatingTabSession(
        FrameworkElement tab,
        string id,
        LayoutFloatingWindowControl floating,
        MouseButtonEventArgs e)
    {
        if (_dragSession is { IsTab: true, PageId: { } currentId } active &&
            ReferenceEquals(active.Surface, tab) &&
            currentId.Equals(id, StringComparison.OrdinalIgnoreCase))
            return;

        CancelDragSession("new floating tab press");
        var session = new DockingDragSession(
            ++_dragSequence,
            DockingDragKind.Tab,
            tab,
            GetScreenPoint(tab, e),
            e.GetPosition(tab),
            id,
            floating,
            $"floating-tab:{id}",
            false,
            false)
        {
            IsFloatingTab = true,
        };
        _dragSession = session;
        tab.CaptureMouse();
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

        // 浮出去会把这个页签从可视树上摘掉。摘之前必须先放掉捕获:捕获还在的话,
        // 后续鼠标移动仍旧送到那个已经断开 PresentationSource 的页签上。
        // 拖动的接力从这里起就交给 WindowDragDriver(见 OnFloatingWindowCreated),
        // 不再需要页签持有捕获。
        WindowDragDriver.ReleaseMouseCapture(session.Surface);
        session.LastScreenPoint = context.PointerPixels;
        ApplyFloatingModelGeometry(context, FindLayoutContent(context.PageId));
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Completion = completion;
        var floated = session.IsFloatingTab
            ? await DetachFloatingTabAsync(session).ConfigureAwait(true)
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
        _ = _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Refresh);

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
            QueueWindowDrag(created, session);
        else
            CompleteDragSession(session, "button released before floating host was ready");
    }

    private LayoutFloatingWindowControl? FindFloatingWindow(string id)
        => _manager.FloatingWindows
            .OfType<LayoutFloatingWindowControl>()
            .FirstOrDefault(window => ModelContainsPage(window.Model, id));

    private static int CountFloatingPages(LayoutFloatingWindowControl floating)
        => floating.Model.Descendents().OfType<LayoutContent>().Count();

    private Task<CommandResult> DetachFloatingTabAsync(DockingDragSession session)
    {
        if (session.PageId == null)
            return Task.FromResult(CommandResult.Fail("浮窗页签缺少页面 ID"));

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
            _manager.CreateFloatingWindow(content, content is LayoutDocument);
            return Task.FromResult(CommandResult.Ok($"页面 {session.PageId} 已拆分为独立浮窗"));
        }
        catch (Exception ex)
        {
            _log.Error(ChromeLogSource, $"拆分浮窗页签失败：{ex.Message}");
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
        });

    private void OnFloatingWindowStateChanged(object? sender, EventArgs e)
    {
        if (sender is LayoutFloatingWindowControl floating)
        {
            ApplyFloatingWindowStateChrome(floating);
            foreach (var pane in _documentPanes)
            {
                if (pane.Model is LayoutDocumentPane { SelectedContent: LayoutContent selected } &&
                    ModelContainsPage(floating.Model, selected.ContentId ?? string.Empty))
                {
                    UpdateDocumentPaneChrome(pane);
                }
            }
        }
    }

    private static void ApplyFloatingWindowStateChrome(LayoutFloatingWindowControl floating)
        => floating.Padding = floating.WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : default;

    private void BeginHostWindowGesture(
        Window hostWindow,
        FrameworkElement surface,
        string target,
        MouseButtonEventArgs e)
    {
        // The main chrome surface is nested in the pane template, so its
        // direct handler and the pane EventSetter can observe one press.
        // Keep the first window session and make the second route a no-op.
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
        var range = GetSystemDoubleClickRange(surface);
        if (_enableMaximizeOnDoubleClick && _doubleClick.RegisterPress(
                target,
                Environment.TickCount64,
                GetScreenPoint(surface, e),
                range.Width,
                range.Height))
        {
            if (ReferenceEquals(hostWindow, _window))
            {
                _ = _bus.ExecuteAsync("aurora.app.window state=toggle", "UI");
            }
            else if (target.StartsWith("floating:", StringComparison.Ordinal))
            {
                var id = target["floating:".Length..];
                _ = _bus.ExecuteAsync(
                    $"aurora.ui.floatstate name={CommandParser.QuoteArg(id)} state=toggle", "UI");
            }
            e.Handled = true;
            return;
        }

        var session = new DockingDragSession(
            ++_dragSequence,
            DockingDragKind.Window,
            surface,
            GetScreenPoint(surface, e),
            e.GetPosition(surface),
            null,
            hostWindow,
            target,
            hostWindow.WindowState == WindowState.Maximized,
            false);
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

        // AvalonDock may mark the original button-down handled before this
        // manager-level handler sees the first captured move. Keep the native
        // key-state check authoritative when available, but do not cancel a
        // still-pressed WPF move solely because the async Win32 sample raced it.
        var current = FloatingWindowGeometry.GetCursorPosition();
        var multiplier = session.WasMaximized ? 2d : 1d;
        var shouldStart = HasReachedDragThreshold(
            session.Start,
            current,
            SystemParameters.MinimumHorizontalDragDistance,
            SystemParameters.MinimumVerticalDragDistance,
            multiplier);
        if (shouldStart)
            _doubleClick.Cancel(session.Target);
        if (shouldStart)
        {
            StartPendingHostDrag(session, current);
            e.Handled = true;
        }
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
            // Threshold handling deliberately releases WPF/Win32 capture before
            // entering the native DragMove loop. That release raises LostMouseCapture
            // synchronously; it is not a cancellation while the session is moving.
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
        _doubleClick.Cancel(session.Target);
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

        _doubleClick.Cancel(session.Target);
        var context = new FloatingDragContext(
            session.PageId!,
            ResolveEmbeddedPaneSize(session.Surface),
            session.Anchor,
            ContinueWithDrag: true)
        {
            SessionId = session.Id,
            PointerPixels = FloatingWindowGeometry.GetCursorPosition(),
        };
        if (!session.TryTransition(DockingDragState.ThresholdReached))
            return;
        session.LastScreenPoint = context.PointerPixels;
        WindowDragDriver.ReleaseMouseCapture(session.Surface);
        // Do not rely on AvalonDock's tab template to start its internal drag
        // service. The Aurora tab template is intentionally replaced, so the
        // threshold crossing owns the transition to a floating host. That
        // gives the host a real DragMove loop, which is what creates the blue
        // docking overlay and makes drop/merge deterministic.
        QueueRestoreAndFloat(session);
        e.Handled = true;
    }

    private void QueueRestoreAndFloat(DockingDragSession session)
    {
        // Let AvalonDock finish the current mouse route before changing its
        // layout. Creating a floating window re-enters focus hooks; doing that
        // from PreviewMouseMove can make the hook inspect a visual owned by a
        // different dispatcher and terminate the host.
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

    private static Size GetSystemDoubleClickRange(Visual visual)
    {
        var dpi = (uint)Math.Round(96 * VisualTreeHelper.GetDpi(visual).DpiScaleX);
        return new Size(
            Math.Max(1, NativeMethods.GetSystemMetricsForDpi(36, dpi)),
            Math.Max(1, NativeMethods.GetSystemMetricsForDpi(37, dpi)));
    }

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
            _log.Warn(ChromeLogSource, $"窗口最大化下拖恢复失败：{ex.Message}");
        }
    }

    internal static WindowState GetToggledWindowState(WindowState state)
        => state == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    internal static bool ShouldDelayHostDrag(bool isMainWindow, WindowState state)
        => !isMainWindow || state == WindowState.Maximized;

    internal static bool ShouldAllowFloatingTabWindowDrag(
        bool isFloatingWindow,
        bool sourceIsTabItem)
        => isFloatingWindow && sourceIsTabItem;

    internal static bool ShouldHandlePaneHeaderInput(
        bool isFloatingWindow,
        bool sourceIsTab,
        bool sourceIsInteractiveControl)
        => !sourceIsInteractiveControl && (isFloatingWindow || !sourceIsTab);

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
            _dragSession = null;
    }

    private Size ResolveEmbeddedPaneSize(string id)
    {
        var tab = FindVisualDescendants<FrameworkElement>(_manager)
            .Where(IsRealPageTab)
            .FirstOrDefault(candidate =>
                TryResolveTabPageId(candidate, out var candidateId) &&
                candidateId.Equals(id, StringComparison.OrdinalIgnoreCase));
        return tab == null ? default : ResolveEmbeddedPaneSize(tab);
    }

    private static Size ResolveEmbeddedPaneSize(FrameworkElement tab)
    {
        var pane = FindAncestor<FrameworkElement>(tab, element =>
            element is LayoutAnchorablePaneControl or LayoutDocumentPaneControl);
        if (pane == null ||
            !double.IsFinite(pane.ActualWidth) || pane.ActualWidth <= 0 ||
            !double.IsFinite(pane.ActualHeight) || pane.ActualHeight <= 0)
        {
            return default;
        }

        return new Size(pane.ActualWidth, pane.ActualHeight);
    }

    private void ApplyFloatingModelGeometry(FloatingDragContext context, DependencyObject source)
    {
        if (!TryResolveTabPageId(source, out var id))
            id = context.PageId;
        ApplyFloatingModelGeometry(context, FindLayoutContent(id));
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

internal sealed class FastDoubleClickGesture(TimeSpan interval)
{
    private readonly long _intervalMilliseconds = checked((long)interval.TotalMilliseconds);
    private string? _target;
    private long _timestamp;
    private Point _position;

    public bool RegisterPress(
        string target,
        long timestamp,
        Point position,
        double horizontalRange,
        double verticalRange)
    {
        var matched = _target != null &&
                      string.Equals(_target, target, StringComparison.Ordinal) &&
                      timestamp >= _timestamp &&
                      timestamp - _timestamp <= _intervalMilliseconds &&
                      Math.Abs(position.X - _position.X) <= horizontalRange &&
                      Math.Abs(position.Y - _position.Y) <= verticalRange;

        if (matched)
        {
            Reset();
            return true;
        }

        _target = target;
        _timestamp = timestamp;
        _position = position;
        return false;
    }

    public void Cancel(string target)
    {
        if (string.Equals(_target, target, StringComparison.Ordinal))
            Reset();
    }

    private void Reset()
    {
        _target = null;
        _timestamp = 0;
        _position = default;
    }
}
