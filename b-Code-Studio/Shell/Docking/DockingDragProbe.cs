using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Imaging;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Themes.VS2013.Themes;
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
    private const int WmLButtonUp = 0x0202;

    private static readonly (string Name, object Key)[] OverlayResourceKeys =
    [
        (nameof(ResourceKeys.DockingButtonBackgroundBrushKey), ResourceKeys.DockingButtonBackgroundBrushKey),
        (nameof(ResourceKeys.DockingButtonForegroundBrushKey), ResourceKeys.DockingButtonForegroundBrushKey),
        (nameof(ResourceKeys.DockingButtonForegroundArrowBrushKey), ResourceKeys.DockingButtonForegroundArrowBrushKey),
        (nameof(ResourceKeys.DockingButtonStarBorderBrushKey), ResourceKeys.DockingButtonStarBorderBrushKey),
        (nameof(ResourceKeys.DockingButtonStarBackgroundBrushKey), ResourceKeys.DockingButtonStarBackgroundBrushKey),
        (nameof(ResourceKeys.PreviewBoxBorderBrushKey), ResourceKeys.PreviewBoxBorderBrushKey),
        (nameof(ResourceKeys.PreviewBoxBackgroundBrushKey), ResourceKeys.PreviewBoxBackgroundBrushKey),
    ];

    private static readonly string[] TemplatePartNames =
    [
        "PART_DropTargetsContainer",
        "PART_PreviewBox",
        "PART_DockingManagerDropTargets",
        "PART_AnchorablePaneDropTargets",
        "PART_DocumentPaneDropTargets",
        "PART_DocumentPaneFullDropTargets",
    ];

    private readonly LayoutFloatingWindowControl _floating;
    private readonly DockingManager _manager;
    private readonly IShellLog _log;
    private readonly string _source;

    private HwndSource? _hwnd;
    private int _moving;
    private int _enterSizeMove;
    private int _exitSizeMove;
    private object? _drag;
    private bool _sawDragService;
    private int _maxAreas;
    private int _maxOverlayElements;
    private int _maxOverlayVisible;
    private int _maxNamedTargets;
    private int _maxBrushed;
    private string _sample = string.Empty;
    private string _overlayPaint = "未测";
    private int _maxImages;
    private int _maxImagesWithoutSource;
    private int _renders;
    private int _drawnPixels;
    private int _totalPixels;
    private string _dominant = "无";
    private int _overlayZ = -1;
    private int _mainZ = -1;
    private int _overlayDicts = -1;

    private int _overlayKeys = -1;

    private int _treeWalks;
    private string _dropTarget = "无";
    private string _exitWithButtonDown = string.Empty;
    private Rect _managerRect;
    private bool _rectCached;
    private bool _trueCursorInside;
    private double _minDistance = double.MaxValue;
    private Point _lastCursor;
    private bool _overlayEverNonNull;
    private bool _overlayEverVisible;
    private string _overlayState = "未观测";
    private string _hostsState = "未观测";
    private string _nullDiagnostic = string.Empty;
    private readonly HashSet<string> _sampledStages = new(StringComparer.Ordinal);
    private readonly List<string> _stageSummaries = [];
    private bool _overlayCreatedSampled;
    private bool _overlayVisibleSampled;
    private bool _dragEnterSampled;
    private bool _repairSampled;
    private bool _previewScaled;
    private string _repairState = "未执行";
    private string _templateParts = "未测";
    private string _resourceState = "未测";
    private string _pathState = "未测";
    private string _contentState = "未测";
    private int _bluePixels;
    private string _pixelBounds = "空";
    private string _transparentRatio = "未测";
    private int _pixelSamples;
    private bool _probeErrorLogged;
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

        // DragMove has returned, so this is the last read-only sample after the
        // system move loop. Keep it before removing the hook and before marking
        // the probe disposed; a late overlay must still be reported, never acted
        // on.
        SafeObserve("WM_EXITSIZEMOVE.after");
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
        _log.Info(
            _source,
            $"停靠探针：停靠区={_maxAreas} 覆盖窗元素={_maxOverlayElements} 其中可见={_maxOverlayVisible} " +
            $"具名投放件={_maxNamedTargets} 有画刷={_maxBrushed} 无画刷样本={_sample} " +
            $"最终投放目标={_dropTarget}");
        _log.Info(
            _source,
            $"停靠探针：覆盖窗实绘像素={_drawnPixels}/{_totalPixels} 主色={_dominant} " +
            $"蓝色像素={_bluePixels} 边界={_pixelBounds} 透明比例={_transparentRatio} " +
            $"位图样本={_pixelSamples} " +
            $"Z序 覆盖窗={_overlayZ} 主窗体={_mainZ} " +
            $"覆盖窗字典={_overlayDicts} 自有键={_overlayKeys} {_overlayPaint}");
        _log.Info(
            _source,
            $"停靠探针：资源={_resourceState} 修复={_repairState} 模板部件={_templateParts} " +
            $"路径={_pathState} 内容控件={_contentState}");
        if (_stageSummaries.Count > 0)
            _log.Info(_source, "停靠探针阶段：" + string.Join(" | ", _stageSummaries));
        _log.Info(
            _source,
            $"停靠探针：光标进过停靠区={(_trueCursorInside ? "是" : "否")} " +
            $"最近距离={(_minDistance is double.MaxValue ? "未测" : Math.Round(_minDistance).ToString())}px " +
            $"末位置=({Math.Round(_lastCursor.X)},{Math.Round(_lastCursor.Y)})");
        _log.Info(_source, $"停靠探针结论：{Conclude()}{_exitWithButtonDown}");
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmEnterSizeMove:
                _enterSizeMove++;
                SafeObserve("WM_ENTERSIZEMOVE");
                break;
            case WmExitSizeMove:
                _exitSizeMove++;
                SafeObserve("WM_EXITSIZEMOVE.before");
                // 移动循环结束时左键还按着 = 系统替你"松了手"，也就是提前投放。
                // 用户反馈"靠近主窗口就直接嵌入而不是合并"，若属实会在这里露出来。
                if (Mouse.LeftButton == MouseButtonState.Pressed)
                    _exitWithButtonDown = "（左键仍按下——移动循环被提前结束）";
                break;
            case WmLButtonUp:
                SafeObserve("WM_LBUTTONUP");
                break;
            case WmMoving:
                _moving++;
                // 全程观测，不做点采样。覆盖窗 Show() 之后 IsVisible 与尺寸要等一轮
                // 布局才更新，只看某一条 WM_MOVING 会把"还没显示"误报成"显示不了"——
                // 这跟早先 IsDragging 轮询踩的是同一个坑，那次也是采样点的问题。
                if (_moving == 1 || _moving == 2 || _moving % 10 == 0)
                    SafeObserve($"WM_MOVING#{_moving}");
                break;
        }

        return IntPtr.Zero;
    }

    private void SafeObserve(string stage)
    {
        if (_disposed)
            return;

        try
        {
            Observe(stage);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TargetInvocationException or MemberAccessException)
        {
            if (_probeErrorLogged)
                return;
            _probeErrorLogged = true;
            _log.Warn(_source, $"停靠探针取样失败（{stage}）：{ex.Message}");
        }
    }

    private void Observe(string stage)
    {
        TrackCursor();
        if (_drag == null && ReadField(_floating, "_dragService") is { } drag)
        {
            _drag = drag;
            _sawDragService = true;
            _hostsState = ReadField(drag, "_overlayWindowHosts") is System.Collections.ICollection hosts
                ? hosts.Count.ToString()
                : "读不到";
            AddStage(stage, $"DragService=已建 hosts={_hostsState}");
        }

        if (_drag is { } service)
        {
            if (ReadField(service, "_currentWindowAreas") is System.Collections.ICollection areas)
                _maxAreas = Math.Max(_maxAreas, areas.Count);
            if (ReadField(service, "_currentDropTarget") is { } target)
            {
                var nextDropTarget = target.GetType().Name;
                if (!string.Equals(nextDropTarget, _dropTarget, StringComparison.Ordinal))
                    AddStage("DragEnter", $"目标={nextDropTarget}");
                _dropTarget = nextDropTarget;
                if (!_dragEnterSampled && ReadField(_manager, "_overlayWindow") is Window)
                {
                    _dragEnterSampled = true;
                    SampleOverlay(stage, "DragEnter");
                }
            }
        }

        if (ReadField(_manager, "_overlayWindow") is not Window overlay)
        {
            if (_sawDragService && _nullDiagnostic.Length == 0)
                _nullDiagnostic = $"管理器屏幕矩形={DescribeManagerRect()}，光标={DescribeCursor()}";
            AddStage(stage, "OverlayWindow=null");
            return;
        }

        _overlayEverNonNull = true;
        if (!_overlayCreatedSampled)
        {
            _overlayCreatedSampled = true;
            SampleOverlay(stage, "创建");
        }

        if (!overlay.IsVisible)
        {
            AddStage(stage, DescribeOverlay(overlay, "不可见"));
            return;
        }

        _overlayEverVisible = true;
        if (!_overlayVisibleSampled)
        {
            _overlayVisibleSampled = true;
            SampleOverlay(stage, "可见");
        }
        else if (_moving % 40 == 0 || stage.Contains("EXITSIZEMOVE", StringComparison.Ordinal))
        {
            SampleOverlay(stage, "移动中");
        }
    }

    private void SampleOverlay(string sourceStage, string stage)
    {
        if (ReadField(_manager, "_overlayWindow") is not Window overlay)
        {
            AddStage(sourceStage + "/" + stage, "OverlayWindow=null");
            return;
        }

        _overlayDicts = overlay.Resources.MergedDictionaries.Count;
        _overlayKeys = overlay.Resources.Count;
        var repair = "未执行";
        if (!_repairSampled && overlay.IsVisible)
        {
            var before = DescribeResources(overlay);
            var result = DockingOverlayResourceRepair.Ensure(overlay);
            var after = DescribeResources(overlay);
            _repairSampled = true;
            repair = $"深色={result.DarkTheme} 改键={(result.ChangedKeys.Count == 0 ? "无" : string.Join(",", result.ChangedKeys))}";
            _repairState = $"{repair} 前[{before}] 后[{after}]";
        }

        var resourceState = DescribeResources(overlay);
        _resourceState = resourceState;
        var details = DescribeOverlay(overlay, stage);
        AddStage(sourceStage + "/" + stage, details + $" 资源={resourceState} 修复={repair}");

        if (!overlay.IsVisible)
            return;

        if (overlay is Control control)
        {
            try
            {
                control.ApplyTemplate();
                if (!_previewScaled)
                    _previewScaled = DockingOverlayResourceRepair.EnsurePreviewScale(overlay);
            }
            catch (InvalidOperationException)
            {
                // Template may be in the middle of a theme/layout swap. The
                // next WM_MOVING sample will retry without touching the drag.
            }
        }

        var tree = WalkOverlay(overlay);
        _templateParts = tree.TemplateParts;
        _pathState = tree.Paths;
        _contentState = tree.ContentControls;
        RenderOverlay(overlay, stage);
        MeasureZOrder(overlay);
        _overlayState = $"模板={(tree.HasTemplate ? "有" : "无")} " +
                        $"位置=({Math.Round(overlay.Left)},{Math.Round(overlay.Top)}) " +
                        $"尺寸={Math.Round(overlay.ActualWidth)}x{Math.Round(overlay.ActualHeight)}";
    }

    private void AddStage(string stage, string details)
    {
        // Keep the diagnostic bounded. A long drag can generate hundreds of
        // WM_MOVING messages; the first sample for each phase plus the explicit
        // moving checkpoints is enough to identify the broken ring.
        var key = stage.IndexOf('/') < 0 && stage.StartsWith("WM_MOVING", StringComparison.Ordinal)
            ? "WM_MOVING"
            : stage;
        if (!_sampledStages.Add(key) && !stage.Contains("EXITSIZEMOVE", StringComparison.Ordinal))
            return;

        var compact = details.Replace('\r', ' ').Replace('\n', ' ');
        if (compact.Length > 1200)
            compact = compact[..1200] + "...";
        _stageSummaries.Add($"{stage}=>{compact}");
        _log.Info(_source, $"停靠探针阶段 {stage}：{compact}");
    }

    private static string DescribeOverlay(Window overlay, string visibility)
    {
        var handle = new WindowInteropHelper(overlay).Handle;
        var template = overlay is Control control && control.Template != null ? "有" : "无";
        return $"句柄={handle} 可见={visibility} IsVisible={overlay.IsVisible} " +
               $"尺寸={Math.Round(overlay.ActualWidth)}x{Math.Round(overlay.ActualHeight)} " +
               $"布局尺寸={Math.Round(overlay.Width)}x{Math.Round(overlay.Height)} " +
               $"Opacity={overlay.Opacity:F2} AllowsTransparency={overlay.AllowsTransparency} 模板={template}";
    }

    private static string DescribeResources(FrameworkElement overlay)
    {
        var values = new List<string>(OverlayResourceKeys.Length);
        foreach (var (name, key) in OverlayResourceKeys)
        {
            object? value = null;
            try
            {
                value = overlay.TryFindResource(key);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                values.Add($"{name}=读取异常:{ex.GetType().Name}");
                continue;
            }

            values.Add($"{name}={DescribeBrush(value)}");
        }

        return string.Join(";", values);
    }

    private static string DescribeBrush(object? value)
    {
        if (value is not Brush brush)
            return value == null ? "null" : value.GetType().Name;
        if (brush is SolidColorBrush solid)
            return $"{solid.Color} alpha={solid.Color.A} opacity={brush.Opacity:F2}";
        return $"{brush.GetType().Name} opacity={brush.Opacity:F2}";
    }

    /// <summary>某个元素是否真的拿到了能画出东西的画刷。</summary>
    private static bool HasPaint(FrameworkElement element)
    {
        var brushes = element switch
        {
            Shape shape => new[] { shape.Fill, shape.Stroke },
            Border border => new[] { border.Background, border.BorderBrush },
            Control control => new[] { control.Background, control.BorderBrush },
            Panel panel => new[] { panel.Background },
            _ => Array.Empty<Brush>(),
        };

        foreach (var brush in brushes)
        {
            if (brush is null || brush.Opacity <= 0)
                continue;
            if (brush is SolidColorBrush { Color.A: 0 })
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 把覆盖窗渲染成位图，数非透明像素。
    ///
    /// 这是唯一不依赖"模板长什么样"的测法。元素数、可见数、图像数都只能证明东西被排进了
    /// 布局，证明不了屏幕上有像素——真机上 105 个可见元素、16 个具名投放件，用户截图里
    /// 却什么都没有。按 Image 数过一轮，门禁里覆盖窗图像=0，说明指示压根不是图片画的。
    /// 与其继续猜是 Path 还是 Rectangle、哪个画刷被主题改没了，不如直接问："画了几个像素"。
    ///
    /// 按 1/4 缩放渲染，够数像素又不至于在拖动中途分配大块位图。最多取几个阶段样本。
    /// </summary>
    private void RenderOverlay(Window overlay, string stage)
    {
        if (_renders >= 6)
            return;

        try
        {
            const double scale = 0.25;
            var width = (int)Math.Ceiling(overlay.ActualWidth * scale);
            var height = (int)Math.Ceiling(overlay.ActualHeight * scale);
            if (width <= 0 || height <= 0)
                return;

            _renders++;
            _overlayPaint =
                $"不透明度={overlay.Opacity} 底色={(overlay.Background is SolidColorBrush b ? b.Color.ToString() : overlay.Background?.ToString() ?? "空")} " +
                $"透明窗={overlay.AllowsTransparency}";
            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(overlay);

            var stride = width * 4;
            var buffer = new byte[stride * height];
            bitmap.CopyPixels(buffer, stride, 0);

            var drawn = 0;
            var blue = 0;
            var minX = width;
            var minY = height;
            var maxX = -1;
            var maxY = -1;
            var counts = new Dictionary<uint, int>();
            for (var offset = 0; offset + 3 < buffer.Length; offset += 4)
            {
                var alpha = buffer[offset + 3];
                if (alpha == 0)
                    continue;

                drawn++;
                var pixel = offset / 4;
                var x = pixel % width;
                var y = pixel / width;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);

                // Pbgra32 stores premultiplied BGRA. Comparing channels is
                // sufficient for a blue-vs-background diagnostic and avoids
                // falsely classifying a transparent blue brush as visible.
                var blueChannel = buffer[offset];
                var green = buffer[offset + 1];
                var red = buffer[offset + 2];
                if (alpha >= 16 && blueChannel > 32 && blueChannel > red * 1.15 && blueChannel > green * 1.05)
                    blue++;

                var key = ((uint)alpha << 24) | ((uint)red << 16) |
                          ((uint)green << 8) | blueChannel;
                counts.TryGetValue(key, out var seen);
                counts[key] = seen + 1;
            }

            // 总数无论如何都要记：画了 0 个像素时提前 return 会报出 "0/0"，
            // 分不清"渲染过但全透明"和"根本没渲染"。1.7.9 真机日志就是这么含糊的。
            _totalPixels = width * height;
            _bluePixels = Math.Max(_bluePixels, blue);
            _transparentRatio = $"{1d - (drawn / (double)Math.Max(1, _totalPixels)):P1}";
            _pixelBounds = maxX < 0 ? "空" : $"({minX},{minY})-({maxX},{maxY})";
            _pixelSamples = _renders;
            if (drawn > _drawnPixels)
                _drawnPixels = drawn;
            if (counts.Count > 0)
            {
                var top = counts.OrderByDescending(pair => pair.Value).First();
                _dominant = $"#{top.Key:X8}({top.Value})";
            }
            AddStage("像素/" + stage,
                $"实绘={drawn}/{_totalPixels} 蓝色={blue} 边界={_pixelBounds} 主色={_dominant}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException or OutOfMemoryException)
        {
            _log.Warn(_source, $"停靠探针渲染取样失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 覆盖窗在 Z 序里的位置。画出来了却被主窗体压在下面，看到的同样是"没有指示"。
    /// 序号越小越靠前。
    /// </summary>
    private void MeasureZOrder(Window overlay)
    {
        try
        {
            var overlayHandle = new WindowInteropHelper(overlay).Handle;
            var main = Window.GetWindow(_manager);
            var mainHandle = main == null ? IntPtr.Zero : new WindowInteropHelper(main).Handle;
            if (overlayHandle == IntPtr.Zero || mainHandle == IntPtr.Zero)
                return;

            var index = 0;
            for (var h = NativeMethods.GetTopWindow(IntPtr.Zero);
                 h != IntPtr.Zero && index < 4000;
                 h = NativeMethods.GetWindow(h, NativeMethods.GwHwndNext), index++)
            {
                if (h == overlayHandle)
                    _overlayZ = index;
                else if (h == mainHandle)
                    _mainZ = index;
            }
        }
        catch (InvalidOperationException)
        {
            // 窗口这一刻没有句柄，跳过。
        }
    }

    private static class NativeMethods
    {
        internal const uint GwHwndNext = 2;

        [DllImport("user32.dll")]
        internal static extern IntPtr GetTopWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    }

    /// <summary>
    /// 全程跟踪光标与停靠区的关系。
    ///
    /// 只在"发现覆盖窗为空"那一刻记一次坐标是不够的：浮窗起手时光标本来就在主窗体外，
    /// 那个读数只能证明起手在外面，证明不了后来有没有拖进去。必须记"**进没进过**"。
    ///
    /// 用 GetCursorPos 的真实光标，不用 WPF 口径（Mouse.GetPosition 换算到屏幕）。
    /// 实测：门禁那条能正常合并的链路上，两者偏差最大到 956px，WPF 口径全程没进过停靠区，
    /// 合并却照常成功——**AvalonDock 的命中测试用的是真实光标**。一度按 WPF 口径写了条
    /// "移动循环里 WPF 鼠标位置陈旧"的结论，正对照当场证伪，否则会引着去修一个不存在的病。
    /// </summary>
    private void TrackCursor()
    {
        if (!_rectCached)
        {
            _rectCached = true;
            _managerRect = ComputeManagerRect();
        }

        if (_managerRect.IsEmpty)
            return;

        var cursor = FloatingWindowGeometry.GetCursorPosition();
        _lastCursor = cursor;
        if (_managerRect.Contains(cursor))
            _trueCursorInside = true;
        _minDistance = Math.Min(_minDistance, DistanceToRect(_managerRect, cursor));

    }

    private Rect ComputeManagerRect()
    {
        try
        {
            if (!_manager.IsVisible)
                return Rect.Empty;

            var origin = _manager.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(_manager);
            return new Rect(
                origin,
                new Size(_manager.ActualWidth * dpi.DpiScaleX, _manager.ActualHeight * dpi.DpiScaleY));
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    private static double DistanceToRect(Rect rect, Point point)
    {
        var dx = Math.Max(Math.Max(rect.Left - point.X, 0), point.X - rect.Right);
        var dy = Math.Max(Math.Max(rect.Top - point.Y, 0), point.Y - rect.Bottom);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// 数覆盖窗里究竟渲染出了什么。
    ///
    /// 窗建起来、有模板、尺寸也对，仍不等于里面有东西——蓝色方位指示就是模板里那几个
    /// 具名元素。模板内容在可回收上下文里解析失败时，窗会照常显示成一片透明。
    ///
    /// 注意别按类型名找 "DropTarget"：AvalonDock 的 DocumentPaneDropTarget 之流是描述
    /// 投放区的普通类，根本不在可视树里；树里那些按钮是 Image/Grid，只有 x:Name 带这个词。
    /// 首版就是按类型名数的，在能正常合并的链路上数出 0，白误报一次。
    /// </summary>
    private OverlayTreeSnapshot WalkOverlay(Visual root)
    {
        _treeWalks++;
        var total = 0;
        var visible = 0;
        var named = 0;
        var images = 0;
        var imagesWithoutSource = 0;
        var brushed = 0;
        var names = new List<string>();
        var paths = new List<string>();
        var contentControls = new List<string>();
        var templateParts = new List<string>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            total++;
            if (node is FrameworkElement { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 } element)
            {
                visible++;
                if (element.Name.Contains("DropTarget", StringComparison.OrdinalIgnoreCase) ||
                    element.Name.Contains("Drop", StringComparison.OrdinalIgnoreCase))
                {
                    named++;
                }

                // 蓝色方位指示是图片。图片资源在可回收上下文里解析失败时，Image 照常
                // 参与布局、照常"可见"、尺寸也对，就是一个像素都不画。投放预览框是纯色
                // 边框、不吃图片资源，所以它能画出来——真机截图正是"白框在、箭头没有"。
                // 画刷到底解析到没有。元素排布正常而一个像素都不画，只可能是画刷为空
                // 或全透明——把"有几个元素真的拿到了不透明画刷"直接数出来，不再推断。
                if (HasPaint(element))
                    brushed++;
                else if (names.Count < 6 && element.ActualWidth >= 8 && element.ActualHeight >= 8)
                    names.Add(element.GetType().Name);

                if (element is Image image)
                {
                    images++;
                    if (image.Source == null)
                        imagesWithoutSource++;
                }

                if (element is Path path && paths.Count < 10)
                {
                    paths.Add($"{DisplayName(element)} fill={DescribeBrush(path.Fill)} stroke={DescribeBrush(path.Stroke)} " +
                              $"size={Math.Round(element.ActualWidth)}x{Math.Round(element.ActualHeight)}");
                }

                if (element is ContentControl content && contentControls.Count < 10)
                {
                    contentControls.Add($"{DisplayName(element)} content={content.Content?.GetType().Name ?? "null"} " +
                                         $"size={Math.Round(element.ActualWidth)}x{Math.Round(element.ActualHeight)} visible={element.IsVisible}");
                }
            }

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }

        _maxOverlayElements = Math.Max(_maxOverlayElements, total);
        _maxOverlayVisible = Math.Max(_maxOverlayVisible, visible);
        _maxNamedTargets = Math.Max(_maxNamedTargets, named);
        if (brushed > _maxBrushed || _sample.Length == 0)
        {
            _maxBrushed = Math.Max(_maxBrushed, brushed);
            if (names.Count > 0)
                _sample = string.Join("/", names);
        }

        _maxImages = Math.Max(_maxImages, images);
        _maxImagesWithoutSource = Math.Max(_maxImagesWithoutSource, imagesWithoutSource);

        var hasTemplate = false;
        if (root is Control control && control.Template != null)
        {
            hasTemplate = true;
            foreach (var partName in TemplatePartNames)
            {
                object? part = null;
                try
                {
                    part = control.Template.FindName(partName, control);
                }
                catch (InvalidOperationException)
                {
                    // A template can be replaced between ApplyTemplate and
                    // FindName. Keep the failed part explicit in the trace.
                }

                templateParts.Add($"{partName}={(part is DependencyObject dependency ? DescribeElement(dependency) : "缺失")}");
            }
        }

        return new OverlayTreeSnapshot(
            hasTemplate,
            string.Join(";", templateParts),
            paths.Count == 0 ? "无" : string.Join(" | ", paths),
            contentControls.Count == 0 ? "无" : string.Join(" | ", contentControls));
    }

    private static string DisplayName(FrameworkElement element)
        => string.IsNullOrWhiteSpace(element.Name) ? element.GetType().Name : element.Name;

    private static string DescribeElement(DependencyObject element)
    {
        if (element is not FrameworkElement framework)
            return element.GetType().Name;
        var common = $"{element.GetType().Name} {framework.Visibility} {Math.Round(framework.ActualWidth)}x{Math.Round(framework.ActualHeight)}";
        return element switch
        {
            Path path => common + $" fill={DescribeBrush(path.Fill)} stroke={DescribeBrush(path.Stroke)}",
            ContentControl content => common + $" content={content.Content?.GetType().Name ?? "null"}",
            Border border => common + $" bg={DescribeBrush(border.Background)} border={DescribeBrush(border.BorderBrush)}",
            Control control => common + $" bg={DescribeBrush(control.Background)} border={DescribeBrush(control.BorderBrush)}",
            _ => common,
        };
    }

    private readonly record struct OverlayTreeSnapshot(
        bool HasTemplate,
        string TemplateParts,
        string Paths,
        string ContentControls);

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
        if (!_overlayEverNonNull && !_trueCursorInside)
            return $"不是故障——整个拖动过程中光标就没进过停靠区（最近还差 {Math.Round(_minDistance)}px），" +
                   "指示本来就不该出现。请把浮窗拖进主窗体中央再试。";
        if (!_overlayEverNonNull)
            return "断在第 4 环——光标进过停靠区，覆盖窗仍未创建（命中测试的矩形算歪了，多半是 DPI）";
        if (!_overlayEverVisible)
            return "断在第 4 环——覆盖窗建了但全程没显示";
        if (_overlayState.Contains("尺寸=0x0", StringComparison.Ordinal))
            return "断在第 4 环——覆盖窗显示了但尺寸为 0，画不出东西";
        if (_overlayState.Contains("模板=无", StringComparison.Ordinal))
            return "断在第 4 环——覆盖窗显示了但没有模板（主题没下发到覆盖窗，多半是可回收上下文的资源解析）";
        if (_maxAreas == 0)
            return "断在第 5 环——覆盖窗好的，但一个停靠区都没算出来（GetDropAreas 返回空）";
        if (_maxOverlayVisible <= 1)
            return "断在第 5 环——停靠区算出来了，但覆盖窗里几乎没有可见元素（模板内容没渲染出来）";
        if (_renders > 0 && _drawnPixels == 0 && _overlayDicts <= 0)
            return "断在第 6 环——覆盖窗一个非透明像素都没画，且字典数为 0：主题字典没挂上去";
        if (_renders > 0 && _drawnPixels == 0 && _maxBrushed == 0)
            return $"断在第 6 环——字典有 {_overlayDicts} 份，但覆盖窗里没有任何一个元素拿到画刷：" +
                   "按键查画刷这一步失败了（模板部件/资源键详情见阶段日志）";
        if (_renders > 0 && _drawnPixels == 0 && _bluePixels == 0)
            return "断在第 6 环——覆盖窗有非透明样本但没有蓝色像素：候选区可能只有底色/边框，" +
                   "箭头或星形轮廓没有绘制";
        if (_renders > 0 && _drawnPixels == 0)
            return $"断在第 6 环——有 {_maxBrushed} 个元素拿到了画刷，渲染出来仍是 0 像素：" +
                   "画刷有了但没画上，看不透明度与透明窗设置";
        if (_drawnPixels > 0 && _overlayZ >= 0 && _mainZ >= 0 && _overlayZ > _mainZ)
            return $"断在第 6 环——覆盖窗画了 {_drawnPixels} 个像素，但 Z 序在主窗体之后" +
                   $"（{_overlayZ} > {_mainZ}），被主窗体压住了";
        if (_drawnPixels > 0 && _bluePixels == 0)
            return $"六环到达绘制阶段但颜色不对——覆盖窗画了 {_drawnPixels} 个像素，没有检测到蓝色候选控件";
        if (_drawnPixels > 0)
            return $"六环齐全——覆盖窗确实画了 {_drawnPixels} 个像素（蓝色 {_bluePixels}）且在主窗体之上";
        return "五环齐全但未取到渲染样本";
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
