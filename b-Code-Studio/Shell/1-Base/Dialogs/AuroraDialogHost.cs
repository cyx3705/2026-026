using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace HistoryAurora.Shell.Base.Dialogs;

/// <summary>
/// 主窗体的弹窗层（REQ-UI-136，1.30.0）：所有 Aurora 弹窗都以 <see cref="AuroraDialogView"/> 卡片的形式
/// 嵌在主窗体里，盖一层半透明的画布色遮住后面的页面，卡片居中。
///
/// 不再开独立窗口，于是：主题切换、令牌、圆角与页面卡片天然一致；弹窗不会跑到别的显示器、
/// 不会被别的程序压在下面；也不再有「Owner 为空时居中到屏幕」这类分支。
///
/// 两种等法：
/// <list type="bullet">
///   <item><see cref="ShowAsync"/>：界面自己的流程（拖出窗口删场景、<c>aurora.ui.dialog</c>）用它，不阻塞界面线程。</item>
///   <item><see cref="Show"/>：宿主确认 <c>IFrontend.Confirm</c> 是同步接口，只能等。别的线程调用时编组过来；
///         在界面线程上则推一层消息循环（与 <c>ShowDialog</c> 同一个办法）等结果。</item>
/// </list>
/// 同时有几张时后来的叠在上面，各自一层遮罩；只有最上面那张收得到鼠标。
/// </summary>
public sealed class AuroraDialogHost
{
    /// <summary>卡片离窗体四边至少留这么多：再小的窗体也看得见遮罩，知道后面还有东西。</summary>
    private const double Margin = 24;

    private readonly Window _owner;
    private readonly Grid _layer;
    private readonly List<AuroraDialogView> _open = [];

    public AuroraDialogHost(Window owner, Grid layer)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(layer);
        _owner = owner;
        _layer = layer;
        _layer.Visibility = Visibility.Collapsed;
        _layer.SizeChanged += (_, _) =>
        {
            foreach (var view in _open)
                Clamp(view);
        };
    }

    /// <summary>此刻摆着的弹窗数。</summary>
    public int OpenCount => _open.Count;

    /// <summary>最上面那张。测试与键盘路径用。</summary>
    internal AuroraDialogView? Top => _open.Count == 0 ? null : _open[^1];

    /// <summary>弹出一张卡片，人做了决定后完成。任意线程可调。</summary>
    public Task<AuroraDialogResult> ShowAsync(AuroraDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_owner.Dispatcher.CheckAccess())
            return _owner.Dispatcher.InvokeAsync(() => ShowAsync(request)).Task.Unwrap();

        var completion = new TaskCompletionSource<AuroraDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = Open(request);
        view.Completed += (_, result) => completion.TrySetResult(result);
        return completion.Task;
    }

    /// <summary>同步等结果：宿主确认这类只能阻塞的调用方用。任意线程可调。</summary>
    public AuroraDialogResult Show(AuroraDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_owner.Dispatcher.CheckAccess())
            return _owner.Dispatcher.Invoke(() => Show(request));

        var outcome = AuroraDialogResult.Rejected;
        var frame = new DispatcherFrame();
        var view = Open(request);
        view.Completed += (_, result) =>
        {
            outcome = result;
            frame.Continue = false;
        };
        Dispatcher.PushFrame(frame);
        return outcome;
    }

    /// <summary>窗体要关了：摆着的弹窗一律按拒绝收场，同步等着的调用方因此能返回。</summary>
    public void CancelAll()
    {
        foreach (var view in _open.ToList())
            view.Cancel();
    }

    private AuroraDialogView Open(AuroraDialogRequest request)
    {
        BringOwnerForward();

        var previousFocus = Keyboard.FocusedElement as IInputElement;
        var view = new AuroraDialogView(request)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        view.Tag = new NaturalSize(view.MaxWidth, view.MaxHeight);

        // 遮罩吃掉落在卡片之外的点击：弹窗是模态的，后面的页面这时不该被点到。
        var scrim = new Border();
        scrim.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Scrim");
        scrim.MouseDown += (_, e) => e.Handled = true;

        var frame = new Grid();
        frame.Children.Add(scrim);
        frame.Children.Add(view);
        _layer.Children.Add(frame);
        _layer.Visibility = Visibility.Visible;
        _open.Add(view);
        Clamp(view);

        view.Completed += (_, _) =>
        {
            _open.Remove(view);
            _layer.Children.Remove(frame);
            if (_layer.Children.Count == 0)
                _layer.Visibility = Visibility.Collapsed;

            if (Top is { } below)
                below.FocusInitial();
            else if (previousFocus is UIElement { IsVisible: true } element)
                element.Focus();
        };

        view.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_open.Contains(view))
                view.FocusInitial();
        });
        return view;
    }

    /// <summary>卡片的上限取「自己声明的」与「窗体放得下的」之小：内容弹窗 860 宽，窗体只有 700 时就缩到 652。</summary>
    private void Clamp(AuroraDialogView view)
    {
        var natural = view.Tag as NaturalSize ?? new NaturalSize(double.PositiveInfinity, double.PositiveInfinity);
        var width = _layer.ActualWidth > 0 ? Math.Max(0, _layer.ActualWidth - 2 * Margin) : double.PositiveInfinity;
        var height = _layer.ActualHeight > 0 ? Math.Max(0, _layer.ActualHeight - 2 * Margin) : double.PositiveInfinity;
        view.MaxWidth = Math.Min(natural.Width, width);
        view.MaxHeight = Math.Min(natural.Height, height);
    }

    /// <summary>
    /// 弹窗嵌在主窗体里，主窗体不在眼前就等于没弹：宿主确认可能在窗体隐藏或最小化时到来（MCP 远程执行写指令）。
    /// </summary>
    private void BringOwnerForward()
    {
        if (!_owner.IsVisible)
            _owner.Show();
        if (_owner.WindowState == WindowState.Minimized)
            _owner.WindowState = WindowState.Normal;
        WindowForegroundActivator.Activate(_owner);
    }

    private sealed record NaturalSize(double Width, double Height);
}
