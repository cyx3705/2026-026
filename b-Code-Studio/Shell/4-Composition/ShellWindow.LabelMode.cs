using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using AvalonDock;
using HistoryAurora.Shell.Base;

namespace HistoryAurora.Shell.Composition;

/// <summary>
/// Ctrl 标签态（REQ-UI-097）。
///
/// 页面拖动不附着在页签上（1.20.2 起窗格根本没有页签行）：单独按下 Ctrl，每一格窗格的内容隐去，
/// 正中只剩一枚写着页名的标签，按住标签拖出去就是一个带蓝色停靠点的浮窗；松开 Ctrl 回到常态。
/// 1.20.2 删掉了按住 0.3 秒的延迟，按下当拍就进；随后按了别的键（Ctrl+C 之类）立刻退出。
///
/// 按键**全靠轮询系统按键状态**，不走 WPF 的键盘事件：WPF 只有在 Aurora 的窗口拿着键盘焦点时
/// 才收得到按键，而用户要拖页面时鼠标停在 Aurora 上、焦点多半还在别的程序里。轮询不挑焦点，
/// 只要 Aurora 的主窗体或浮窗在前台、或者鼠标正停在它们上面（<see cref="IsShellUnderUser"/>）。
/// 何时算「单独按下」交给 <see cref="LabelModeGate"/>。
///
/// 退出同样看轮询：拖动时系统移动循环接管了消息，Ctrl 的抬起不一定送得到任何一个窗口。
/// 有拖动会话在途时不退——松开 Ctrl 不该掐断正在拖的那一页。
/// </summary>
internal partial class ShellWindow
{
    private static readonly TimeSpan LabelModePollInterval = TimeSpan.FromMilliseconds(50);

    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyLeftControl = 0xA2;
    private const int VirtualKeyRightControl = 0xA3;

    private readonly LabelModeGate _labelGate = new();
    private DispatcherTimer? _labelPoll;
    private bool _labelMode;

    /// <summary>标签态是按 Ctrl 进的：松开 Ctrl 就退。程序打开的（门禁用）不跟 Ctrl 走。</summary>
    private bool _labelFollowsCtrl;

    internal bool IsLabelMode => _labelMode;

    private void InitializeLabelMode()
    {
        _labelPoll = new DispatcherTimer(DispatcherPriority.Input) { Interval = LabelModePollInterval };
        _labelPoll.Tick += (_, _) => PollLabelMode();
        Loaded += (_, _) => _labelPoll.Start();

        _pageDrag.DragFinished += OnLabelDragFinished;
        DockManager.LayoutFloatingWindowControlCreated += OnLabelFloatingWindowCreated;
    }

    private void ShutdownLabelMode()
    {
        _labelPoll?.Stop();
        DockManager.LayoutFloatingWindowControlCreated -= OnLabelFloatingWindowCreated;
        _pageDrag.DragFinished -= OnLabelDragFinished;
    }

    private void PollLabelMode()
    {
        if (_closing)
            return;

        var ctrl = IsKeyDown(VirtualKeyControl);
        if (_labelMode)
        {
            // 闸门也要看见这一拍：松开 Ctrl 才算这次按下结束，下次按下才能再进。
            var otherKey = ctrl && IsAnyOtherKeyDown(includeMouse: false);
            _labelGate.Update(ctrl, otherKey, shellInFront: false);
            if (_labelFollowsCtrl && (!ctrl || otherKey) && !_pageDrag.IsDragging)
                SetLabelMode(false);
            return;
        }

        // 只有 Ctrl 按着时才去扫别的键、问前台窗口：平时每一拍只读一个键。
        if (_labelGate.Update(ctrl, ctrl && IsAnyOtherKeyDown(includeMouse: true), ctrl && IsShellUnderUser()))
            SetLabelMode(true, followCtrl: true);
    }

    private void OnLabelDragFinished(object? sender, EventArgs e)
    {
        if (_labelMode && _labelFollowsCtrl && !IsKeyDown(VirtualKeyControl))
            SetLabelMode(false);
    }

    /// <summary>拖出来的新浮窗一出生就跟着当前的标签态——拖着走的就是一枚标签。</summary>
    private void OnLabelFloatingWindowCreated(object? sender, LayoutFloatingWindowControlCreatedEventArgs e)
        => PageLabelMode.SetIsActive(e.LayoutFloatingWindowControl, _labelMode);

    internal void SetLabelMode(bool active, bool followCtrl = false)
    {
        _labelFollowsCtrl = active && followCtrl;
        if (_labelMode == active)
            return;

        _labelMode = active;
        _pageDrag.LabelMode = active;
        PageLabelMode.SetIsActive(this, active);
        ApplyLabelModeToFloatingWindows();
    }

    private void ApplyLabelModeToFloatingWindows()
    {
        foreach (var floating in DockManager.FloatingWindows)
        {
            if (PageLabelMode.GetIsActive(floating) != _labelMode)
                PageLabelMode.SetIsActive(floating, _labelMode);
        }
    }

    /// <summary>
    /// Aurora 在用户眼前：前台窗口是主窗体或它的浮窗（浮窗由主窗体拥有，按根拥有者认），
    /// 或者鼠标此刻停在它们上面。
    /// </summary>
    private bool IsShellUnderUser()
    {
        var own = ShellWindowHandles();
        if (own.Count == 0)
            return false;

        var foreground = LabelModeNative.GetForegroundWindow();
        if (own.Contains(foreground) ||
            own.Contains(LabelModeNative.GetAncestor(foreground, LabelModeNative.RootOwner)))
        {
            return true;
        }

        if (!LabelModeNative.GetCursorPos(out var cursor))
            return false;
        var hit = LabelModeNative.WindowFromPoint(cursor);
        return hit != IntPtr.Zero &&
               (own.Contains(LabelModeNative.GetAncestor(hit, LabelModeNative.Root)) ||
                own.Contains(LabelModeNative.GetAncestor(hit, LabelModeNative.RootOwner)));
    }

    private HashSet<IntPtr> ShellWindowHandles()
    {
        var handles = new HashSet<IntPtr>();
        if (!IsVisible || WindowState == System.Windows.WindowState.Minimized)
            return handles;

        var main = new WindowInteropHelper(this).Handle;
        if (main != IntPtr.Zero)
            handles.Add(main);
        foreach (var floating in DockManager.FloatingWindows)
        {
            var handle = new WindowInteropHelper(floating).Handle;
            if (handle != IntPtr.Zero)
                handles.Add(handle);
        }

        return handles;
    }

    /// <summary>
    /// 除 Ctrl 以外的任何键此刻按着。<paramref name="includeMouse"/> 为 false 时不看鼠标键——
    /// 标签态里按住鼠标正是拖动本身，不能因此退出。
    /// </summary>
    private static bool IsAnyOtherKeyDown(bool includeMouse)
    {
        for (var key = 0x01; key <= 0xFE; key++)
        {
            if (key is VirtualKeyControl or VirtualKeyLeftControl or VirtualKeyRightControl)
                continue;
            if (!includeMouse && key is 0x01 or 0x02 or 0x04 or 0x05 or 0x06)
                continue;
            if (IsKeyDown(key))
                return true;
        }

        return false;
    }

    private static bool IsKeyDown(int virtualKey)
        => (LabelModeNative.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static class LabelModeNative
    {
        internal const uint Root = 2;
        internal const uint RootOwner = 3;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(Point point);
    }
}
