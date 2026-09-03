using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shapes;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Themes;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Components.Graph;

/// <summary>
/// 泳道图组件（REQ-UI-010）。吃一份 <see cref="SwimlaneDescription"/>，
/// 自己算布局（<see cref="SwimlaneLayout"/>）、自己画、自己管平移与视口裁剪。
///
/// 描述里没有任何坐标、颜色或尺寸——模块只说有哪些节点、谁是谁的父。
/// 节点点击落到**动作 id**（描述的 <c>selectAction</c>），以 <c>{node}</c> 供动作取被点节点，
/// 理由与面板按钮一致：写死指令名的话，模块改一次名这张图就点不动了。
/// </summary>
public sealed class AuroraSwimlane : UserControl
{
    /// <summary>视口外多画一圈，滚动时不会露出空白再补画。</summary>
    private const double CullPad = 96;

    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly ActionRegistry _actions;

    private readonly TextBlock _caption;
    private readonly TextBlock _message;
    private readonly ScrollViewer _viewport;
    private readonly Canvas _canvas;
    private readonly Canvas _gutter;
    private readonly TranslateTransform _gutterOffset = new();
    private readonly DoubleCollection _dash = new() { 4, 3 };

    private SwimlaneLayout.Result? _layout;
    private string? _selectAction;
    private bool _rendering;
    private bool _panning;
    private Point _panOrigin;
    private double _panOffsetX;
    private double _panOffsetY;

    public AuroraSwimlane(CommandBus bus, IShellLog log, ActionRegistry actions)
    {
        _bus = bus;
        _log = log;
        _actions = actions;
        Background = Brushes.Transparent;
        AuroraComponentResources.Ensure(this);

        _caption = new TextBlock { Margin = new Thickness(10, 8, 10, 6) };
        _caption.SetResourceReference(StyleProperty, "Aurora.Panel.Label");

        _message = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _message.SetResourceReference(StyleProperty, "Aurora.Table.Empty");

        _canvas = new Canvas { Background = Brushes.Transparent };
        _viewport = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _canvas,
        };
        _viewport.ScrollChanged += OnScrollChanged;
        _viewport.SizeChanged += (_, _) => RenderVisible();
        _viewport.PreviewMouseLeftButtonDown += OnViewportMouseDown;
        _viewport.PreviewMouseMove += OnViewportMouseMove;
        _viewport.PreviewMouseLeftButtonUp += (_, _) => EndPan();
        _viewport.LostMouseCapture += (_, _) => EndPan();

        _gutter = new Canvas
        {
            Width = SwimlaneLayout.LaneTitleWidth,
            Background = Brushes.Transparent,
            ClipToBounds = true,
            RenderTransform = _gutterOffset,
        };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(SwimlaneLayout.LaneTitleWidth),
        });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var gutterHost = new Grid { ClipToBounds = true, Children = { _gutter } };
        Grid.SetColumn(gutterHost, 0);
        Grid.SetColumn(_viewport, 1);
        body.Children.Add(gutterHost);
        body.Children.Add(_viewport);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_caption, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(_caption);
        root.Children.Add(body);

        var surface = new Border { Child = new Grid { Children = { root, _message } } };
        surface.SetResourceReference(StyleProperty, "Aurora.Table.Surface");
        Content = surface;
    }

    /// <summary>当前布局结果；无数据时为 null。供测试与状态栏读取。</summary>
    public SwimlaneLayout.Result? Layout => _layout;

    /// <summary>换一份描述。传 null 等同清空。</summary>
    public void SetDescription(SwimlaneDescription? description)
    {
        if (description == null || description.Nodes.Count == 0)
        {
            _layout = null;
            _selectAction = description?.SelectAction;
            _canvas.Children.Clear();
            _gutter.Children.Clear();
            _caption.Text = description?.Title ?? "";
            ShowMessage("暂无节点");
            return;
        }

        _selectAction = description.SelectAction;
        _layout = SwimlaneLayout.Arrange(description);
        _caption.Text = description.Title;
        _canvas.Width = Math.Max(1, _layout.Width);
        _canvas.Height = Math.Max(1, _layout.Height);
        _message.Visibility = Visibility.Collapsed;
        _viewport.Visibility = Visibility.Visible;

        RenderGutter();
        RenderVisible();
        ScrollToBranchHeads();
    }

    /// <summary>
    /// 新描述一律从**最右端**看起。
    ///
    /// 图是按时间从左往右长的，分支头（各泳道的最新节点）都在右边缘，
    /// 而那正是每次打开都想先看的东西。停在左端等于每次都要先手动拖一段。
    ///
    /// 排到 Render 优先级：此刻 ScrollViewer 才按新的 Canvas 尺寸算出 ScrollableWidth，
    /// 在 SetDescription 里直接滚会滚到上一份描述的范围上（多半是 0）。
    /// </summary>
    private void ScrollToBranchHeads()
        => Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                _viewport.UpdateLayout();
                _viewport.ScrollToHorizontalOffset(_viewport.ScrollableWidth);
            }));

    /// <summary>显示一句话代替图：取数失败、尚未选择对象等。</summary>
    public void ShowMessage(string message)
    {
        _message.Text = message;
        _message.Visibility = Visibility.Visible;
        _viewport.Visibility = _layout == null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- 绘制

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 泳道标题栏不随横向滚动移动，只跟着纵向走——否则一横拉标题就跑没了。
        _gutterOffset.Y = -_viewport.VerticalOffset;
        RenderVisible();
    }

    private void RenderGutter()
    {
        _gutter.Children.Clear();
        if (_layout == null)
            return;

        _gutter.Height = _layout.Height;
        foreach (var lane in _layout.Lanes)
        {
            var title = new TextBlock
            {
                Text = lane.Title,
                Width = SwimlaneLayout.LaneTitleWidth - 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = lane.Title,
            };
            title.SetResourceReference(
                StyleProperty,
                lane.Open ? "Aurora.Graph.LaneTitle" : "Aurora.Graph.LaneTitleClosed");
            Canvas.SetLeft(title, 8);
            Canvas.SetTop(title, SwimlaneLayout.LaneCenterY(lane.Index) - 9);
            _gutter.Children.Add(title);
        }
    }

    private void RenderVisible()
    {
        if (_rendering || _layout == null)
            return;
        _rendering = true;
        try
        {
            var viewWidth = _viewport.ViewportWidth > 0 ? _viewport.ViewportWidth : _layout.Width;
            var viewHeight = _viewport.ViewportHeight > 0 ? _viewport.ViewportHeight : _layout.Height;
            var viewX = _viewport.HorizontalOffset - CullPad;
            var viewY = _viewport.VerticalOffset - CullPad;
            viewWidth += CullPad * 2;
            viewHeight += CullPad * 2;

            _canvas.Children.Clear();

            foreach (var edge in _layout.Edges)
            {
                var x = Math.Min(edge.X1, edge.X2) - 2;
                var y = Math.Min(edge.Y1, edge.Y2) - 2;
                var w = Math.Abs(edge.X2 - edge.X1) + 4;
                var h = Math.Abs(edge.Y2 - edge.Y1) + 4;
                if (!SwimlaneLayout.Intersects(x, y, w, h, viewX, viewY, viewWidth, viewHeight))
                    continue;
                _canvas.Children.Add(CreateEdge(edge));
            }

            foreach (var node in _layout.Nodes)
            {
                if (!SwimlaneLayout.Intersects(
                        node.X, node.Y, SwimlaneLayout.NodeWidth, SwimlaneLayout.NodeHeight,
                        viewX, viewY, viewWidth, viewHeight))
                    continue;
                _canvas.Children.Add(CreateNode(node));
                if (node.IsTip)
                    _canvas.Children.Add(CreateTip(node));
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private Line CreateEdge(SwimlaneLayout.PlacedEdge edge)
    {
        var line = new Line
        {
            X1 = edge.X1,
            Y1 = edge.Y1,
            X2 = edge.X2,
            Y2 = edge.Y2,
            StrokeThickness = edge.Edge.Dashed ? 1.1 : 1.4,
            StrokeDashArray = edge.Edge.Dashed ? _dash : null,
            IsHitTestVisible = false,
        };
        line.SetResourceReference(
            Shape.StrokeProperty,
            edge.Edge.Dashed ? "Aurora.Brush.TextSecondary" : "Aurora.Brush.Accent");
        return line;
    }

    private Border CreateNode(SwimlaneLayout.PlacedNode placed)
    {
        var node = placed.Node;
        var tone = (node.Tone ?? "").ToLowerInvariant();
        var highlighted = placed.IsTip || tone is "accent" or "danger";

        var border = new Border
        {
            Width = SwimlaneLayout.NodeWidth,
            Height = SwimlaneLayout.NodeHeight,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(highlighted ? 2 : 1),
            Padding = new Thickness(6, 3, 6, 3),
            Cursor = Cursors.Hand,
            Tag = node.Id,
            ClipToBounds = true,
            ToolTip = string.IsNullOrWhiteSpace(node.Tooltip)
                ? node.Title + (string.IsNullOrWhiteSpace(node.Subtitle) ? "" : "\n" + node.Subtitle)
                : node.Tooltip,
        };
        border.SetResourceReference(
            Border.BackgroundProperty,
            tone == "danger" ? "Aurora.Brush.DangerSoft"
            : highlighted ? "Aurora.Brush.AccentSoft"
            : "Aurora.Brush.SurfaceAlt");
        border.SetResourceReference(
            Border.BorderBrushProperty,
            tone == "danger" ? "Aurora.Brush.Danger"
            : highlighted ? "Aurora.Brush.Accent"
            : "Aurora.Brush.ControlBorder");

        var title = new TextBlock { Text = node.Title };
        title.SetResourceReference(StyleProperty, "Aurora.Graph.NodeTitle");

        var stack = new StackPanel();
        stack.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(node.Subtitle))
        {
            var subtitle = new TextBlock { Text = node.Subtitle };
            subtitle.SetResourceReference(StyleProperty, "Aurora.Graph.NodeSubtitle");
            stack.Children.Add(subtitle);
        }

        border.Child = stack;
        border.MouseLeftButtonUp += OnNodeClick;
        Canvas.SetLeft(border, placed.X);
        Canvas.SetTop(border, placed.Y);
        return border;
    }

    private Ellipse CreateTip(SwimlaneLayout.PlacedNode placed)
    {
        const double size = 8;
        var tip = new Ellipse { Width = size, Height = size, IsHitTestVisible = false };
        tip.SetResourceReference(Shape.FillProperty, "Aurora.Brush.Accent");
        Canvas.SetLeft(tip, placed.X + SwimlaneLayout.NodeWidth + 4);
        Canvas.SetTop(tip, placed.Y + (SwimlaneLayout.NodeHeight / 2) - (size / 2));
        return tip;
    }

    // ---------------------------------------------------------------- 交互

    private void OnNodeClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: string id })
            return;
        if (string.IsNullOrWhiteSpace(_selectAction))
            return;

        var binding = _actions.Resolve(_selectAction);
        if (!binding.Ok)
        {
            _log.Error("graph", binding.Error!);
            return;
        }

        var text = ActionRegistry.BuildCommandText(
            binding.Action!,
            control => string.Equals(control, "node", StringComparison.OrdinalIgnoreCase) ? id : null,
            out var error);
        if (text == null)
        {
            _log.Error("graph", error);
            return;
        }

        _ = _bus.ExecuteAsync(text, "UI");
    }

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (HitNode(e.OriginalSource as DependencyObject))
            return;
        _panning = true;
        _panOrigin = e.GetPosition(_viewport);
        _panOffsetX = _viewport.HorizontalOffset;
        _panOffsetY = _viewport.VerticalOffset;
        _viewport.CaptureMouse();
        e.Handled = true;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning)
            return;
        var now = e.GetPosition(_viewport);
        _viewport.ScrollToHorizontalOffset(_panOffsetX - (now.X - _panOrigin.X));
        _viewport.ScrollToVerticalOffset(_panOffsetY - (now.Y - _panOrigin.Y));
        e.Handled = true;
    }

    private void EndPan()
    {
        if (!_panning)
            return;
        _panning = false;
        if (_viewport.IsMouseCaptured)
            _viewport.ReleaseMouseCapture();
    }

    /// <summary>节点带 Tag，命中它就不算拖画布——否则点节点会变成一次微小平移。</summary>
    private static bool HitNode(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement { Tag: string })
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
