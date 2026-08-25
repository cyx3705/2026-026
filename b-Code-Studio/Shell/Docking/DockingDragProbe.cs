using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Controls;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Docking;

/// <summary>
/// 浮窗停靠链路的真机探针（只读）。
///
/// 那组蓝色方位指示要出现，四环缺一不可：
///   1 系统移动循环启动         —— 观测 WM_ENTERSIZEMOVE
///   2 循环持续发 WM_MOVING      —— 观测 WM_MOVING 计数
///   3 AvalonDock 建 DragService —— 观测 LayoutFloatingWindowControl._dragService
///   4 覆盖窗建起来并显示        —— 观测 DockingManager._overlayWindow
///
/// 加这个东西是有教训的：前两轮都是靠推断改的（先猜 CaptionHeight，再猜
/// WM_NCLBUTTONDOWN），两次都错，两次都白发一版。链路断点必须测出来，不能想出来。
/// 门禁里这条链路是通的（见 FloatingDockMouseSmoke），所以差异只可能在真机环境，
/// 而真机没法挂调试器——只能让它自己把四环报出来。
///
/// 拖动结束即摘钩；任何反射失败都只记一行，绝不让探针本身弄坏拖动。
/// </summary>
internal sealed class DockingDragProbe : IDisposable
{
    private const int WmMoving = 0x0216;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;

    private readonly LayoutFloatingWindowControl _floating;
    private readonly DockingManager _manager;
    private readonly IShellLog _log;
    private readonly string _source;

    private HwndSource? _hwnd;
    private int _moving;
    private int _enterSizeMove;
    private int _exitSizeMove;
    private bool _sawDragService;
    private bool _overlayEverNonNull;
    private bool _overlayEverVisible;
    private string _overlayState = "未观测";
    private string _hostsState = "未观测";
    private string _nullDiagnostic = string.Empty;
    private bool _disposed;

    public DockingDragProbe(
        LayoutFloatingWindowControl floating,
        DockingManager manager,
        IShellLog log,
        string source)
    {
        _floating = floating;
        _manager = manager;
        _log = log;
        _source = source;
    }

    public void Start()
    {
        try
        {
            var handle = new WindowInteropHelper(_floating).Handle;
            if (handle == IntPtr.Zero)
            {
                _log.Warn(_source, "停靠探针：浮窗还没有窗口句柄，本次不观测");
                return;
            }

            _hwnd = HwndSource.FromHwnd(handle);
            _hwnd?.AddHook(OnMessage);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _log.Warn(_source, $"停靠探针挂钩失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _hwnd?.RemoveHook(OnMessage);
        _hwnd = null;

        _log.Info(
            _source,
            $"停靠探针：ENTERSIZEMOVE={_enterSizeMove} WM_MOVING={_moving} EXITSIZEMOVE={_exitSizeMove}");
        var overlay = !_overlayEverNonNull
            ? $"从未创建（{_nullDiagnostic}）"
            : !_overlayEverVisible
                ? "创建过但全程未可见"
                : _overlayState;
        _log.Info(_source, $"停靠探针：DragService={(_sawDragService ? "已建" : "未建")} 覆盖窗宿主={_hostsState} 覆盖窗={overlay}");
        _log.Info(_source, $"停靠探针结论：{Conclude()}");
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmEnterSizeMove:
                _enterSizeMove++;
                break;
            case WmExitSizeMove:
                _exitSizeMove++;
                break;
            case WmMoving:
                _moving++;
                // 全程观测，不做点采样。覆盖窗 Show() 之后 IsVisible 与尺寸要等一轮
                // 布局才更新，只看某一条 WM_MOVING 会把"还没显示"误报成"显示不了"——
                // 这跟早先 IsDragging 轮询踩的是同一个坑，那次也是采样点的问题。
                if (_moving >= 2)
                    Observe();
                break;
        }

        return IntPtr.Zero;
    }

    private void Observe()
    {
        try
        {
            if (!_sawDragService && ReadField(_floating, "_dragService") is { } drag)
            {
                _sawDragService = true;
                _hostsState = ReadField(drag, "_overlayWindowHosts") is System.Collections.ICollection hosts
                    ? hosts.Count.ToString()
                    : "读不到";
            }

            if (ReadField(_manager, "_overlayWindow") is not Window overlay)
            {
                if (_sawDragService && _nullDiagnostic.Length == 0)
                    _nullDiagnostic = $"管理器屏幕矩形={DescribeManagerRect()}，光标={DescribeCursor()}";
                return;
            }

            _overlayEverNonNull = true;
            if (!overlay.IsVisible || _overlayEverVisible)
                return;

            // 第一次真正可见时定格：这是"指示到底画没画出来"的唯一可信证据。
            _overlayEverVisible = true;
            var template = overlay is Control { Template: not null } ? "有" : "无";
            _overlayState = $"模板={template} " +
                            $"位置=({Math.Round(overlay.Left)},{Math.Round(overlay.Top)}) " +
                            $"尺寸={Math.Round(overlay.ActualWidth)}x{Math.Round(overlay.ActualHeight)}";
        }
        catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException)
        {
            _log.Warn(_source, $"停靠探针取样失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 覆盖窗为 null 时最可能的原因是命中测试没落在管理器上。AvalonDock 用
    /// PointToScreenDPI 算这个矩形，多显示器 / 非 100% 缩放下会算歪——所以把
    /// 矩形和光标一起打出来，两者对不上就是这一档。
    /// </summary>
    private string DescribeManagerRect()
    {
        try
        {
            if (!_manager.IsVisible)
                return "管理器不可见";

            var origin = _manager.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(_manager);
            var width = _manager.ActualWidth * dpi.DpiScaleX;
            var height = _manager.ActualHeight * dpi.DpiScaleY;
            return $"({Math.Round(origin.X)},{Math.Round(origin.Y)})+{Math.Round(width)}x{Math.Round(height)}";
        }
        catch (InvalidOperationException)
        {
            return "读不到";
        }
    }

    private static string DescribeCursor()
    {
        var cursor = FloatingWindowGeometry.GetCursorPosition();
        return $"({Math.Round(cursor.X)},{Math.Round(cursor.Y)})";
    }

    private string Conclude()
    {
        if (_enterSizeMove == 0 && _moving == 0)
            return "断在第 1 环——系统移动循环没启动，DragMove 空转（捕获没放干净？）";
        if (_moving == 0)
            return "断在第 2 环——移动循环起来了，但一条 WM_MOVING 都没收到";
        if (!_sawDragService)
            return "断在第 3 环——收到 WM_MOVING，但 AvalonDock 没建 DragService（消息钩子没挂到这个浮窗上）";
        if (!_overlayEverNonNull)
            return "断在第 4 环——DragService 建了，覆盖窗没建（命中测试没落在停靠管理器上，比对上面的矩形与光标）";
        if (!_overlayEverVisible)
            return "断在第 4 环——覆盖窗建了但全程没显示";
        if (_overlayState.Contains("尺寸=0x0", StringComparison.Ordinal))
            return "断在第 4 环——覆盖窗显示了但尺寸为 0，画不出东西";
        if (_overlayState.Contains("模板=无", StringComparison.Ordinal))
            return "断在第 4 环——覆盖窗显示了但没有模板（主题没下发到覆盖窗，多半是可回收上下文的资源解析）";
        return "四环齐全——指示应当已经出现";
    }

    private static object? ReadField(object target, string name)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field != null)
                return field.GetValue(target);
        }

        return null;
    }
}
