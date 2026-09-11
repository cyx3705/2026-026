using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock;
using HistoryAurora.Shell.Base;

namespace HistoryAurora.Shell.Composition;

/// <summary>
/// Ctrl 标签态（REQ-UI-097）。
///
/// 页面拖动不再附着在页签上：单独按住 Ctrl 约 0.3 秒，每一格窗格的内容换成一块写着页名的
/// 大标签，按住标签（或页签）拖出去就是一个带蓝色停靠点的浮窗；松开 Ctrl 回到常态。
/// 平时按页签只换页。
///
/// 三条取舍：
/// <list type="bullet">
///   <item>按住 0.3 秒而不是一按就进：否则在控制台里按 Ctrl+C / Ctrl+V，整个界面都会闪成标签（用户拍板）。
///         中途按了别的键就不算。</item>
///   <item>按键走 <see cref="InputManager"/> 而不是窗体的键盘事件：浮窗是独立 Window，
///         焦点在浮窗里时主窗体收不到按键。</item>
///   <item>退出靠轮询系统按键状态：拖动时系统移动循环接管了消息，Ctrl 的抬起不一定送得到任何一个窗口。
///         有拖动会话在途时不退——松开 Ctrl 不该掐断正在拖的那一页。</item>
/// </list>
/// </summary>
internal partial class ShellWindow
{
    private static readonly TimeSpan LabelModeHold = TimeSpan.FromMilliseconds(300);

    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyMenu = 0x12;
    private const int VirtualKeyLeftWin = 0x5B;
    private const int VirtualKeyRightWin = 0x5C;

    private DispatcherTimer? _labelHold;
    private DispatcherTimer? _labelWatch;
    private bool _labelMode;

    /// <summary>标签态是按 Ctrl 进的：松开 Ctrl 就退。程序打开的（门禁用）不跟 Ctrl 走。</summary>
    private bool _labelFollowsCtrl;

    internal bool IsLabelMode => _labelMode;

    private void InitializeLabelMode()
    {
        _labelHold = new DispatcherTimer(DispatcherPriority.Input) { Interval = LabelModeHold };
        _labelHold.Tick += (_, _) =>
        {
            _labelHold.Stop();
            if (!_closing && IsOnlyCtrlDown())
                SetLabelMode(true, followCtrl: true);
        };

        _labelWatch = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(80) };
        _labelWatch.Tick += (_, _) =>
        {
            if (_labelFollowsCtrl && !IsKeyDown(VirtualKeyControl) && !_topBar.IsDragging)
                SetLabelMode(false);
        };

        _topBar.DragFinished += OnLabelDragFinished;
        DockManager.LayoutFloatingWindowControlCreated += OnLabelFloatingWindowCreated;
        InputManager.Current.PostProcessInput += OnLabelModeInput;
    }

    /// <summary>InputManager 是线程级的：窗口关了必须摘掉，否则热重载后旧窗口还挂在输入管线上。</summary>
    private void ShutdownLabelMode()
    {
        InputManager.Current.PostProcessInput -= OnLabelModeInput;
        DockManager.LayoutFloatingWindowControlCreated -= OnLabelFloatingWindowCreated;
        _topBar.DragFinished -= OnLabelDragFinished;
        _labelHold?.Stop();
        _labelWatch?.Stop();
    }

    private void OnLabelModeInput(object sender, ProcessInputEventArgs e)
    {
        if (_closing || _labelHold == null || e.StagingItem.Input is not KeyEventArgs key)
            return;

        var isCtrl = key.Key is Key.LeftCtrl or Key.RightCtrl;
        if (key.RoutedEvent == Keyboard.KeyDownEvent)
        {
            if (!isCtrl)
            {
                // 组合键（Ctrl+C 之类）不是标签态。
                _labelHold.Stop();
                return;
            }

            if (!key.IsRepeat && !_labelMode)
            {
                _labelHold.Stop();
                _labelHold.Start();
            }
        }
        else if (key.RoutedEvent == Keyboard.KeyUpEvent && isCtrl)
        {
            _labelHold.Stop();
        }
    }

    private void OnLabelDragFinished(object? sender, EventArgs e)
    {
        if (_labelMode && _labelFollowsCtrl && !IsKeyDown(VirtualKeyControl))
            SetLabelMode(false);
    }

    /// <summary>拖出来的新浮窗一出生就跟着当前的标签态——拖着走的就是一块标签。</summary>
    private void OnLabelFloatingWindowCreated(object? sender, LayoutFloatingWindowControlCreatedEventArgs e)
        => PageLabelMode.SetIsActive(e.LayoutFloatingWindowControl, _labelMode);

    internal void SetLabelMode(bool active, bool followCtrl = false)
    {
        _labelFollowsCtrl = active && followCtrl;
        if (_labelMode == active)
            return;

        _labelMode = active;
        _topBar.LabelMode = active;
        PageLabelMode.SetIsActive(this, active);
        ApplyLabelModeToFloatingWindows();
        if (active)
            _labelWatch?.Start();
        else
            _labelWatch?.Stop();
    }

    private void ApplyLabelModeToFloatingWindows()
    {
        foreach (var floating in DockManager.FloatingWindows)
        {
            if (PageLabelMode.GetIsActive(floating) != _labelMode)
                PageLabelMode.SetIsActive(floating, _labelMode);
        }
    }

    private static bool IsOnlyCtrlDown()
        => IsKeyDown(VirtualKeyControl)
           && !IsKeyDown(VirtualKeyShift)
           && !IsKeyDown(VirtualKeyMenu)
           && !IsKeyDown(VirtualKeyLeftWin)
           && !IsKeyDown(VirtualKeyRightWin);

    private static bool IsKeyDown(int virtualKey)
        => (LabelModeNative.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static class LabelModeNative
    {
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);
    }
}
