using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HistoryAurora.Shell.Components.Panels;

/// <summary>
/// 面板图标的宿主管控目录。几何来自 Lucide 的 refresh-cw 语义图标；
/// 模块只能交语义名，不能把任意 SVG 或路径数据注入界面。
/// </summary>
internal static class PanelIconCatalog
{
    private const string RefreshCw =
        "M21,12 A9,9 0 0 1 5.781,18.482 L3,16 "
        + "M3,21 V16 H8 "
        + "M3,12 A9,9 0 0 1 18.219,5.518 L21,8 "
        + "M21,3 V8 H16";

    public static bool Supports(string? name)
        => string.Equals(name, "refresh-cw", StringComparison.OrdinalIgnoreCase);

    public static FrameworkElement Create(string name, Button owner)
    {
        if (!Supports(name))
            throw new ArgumentOutOfRangeException(nameof(name), name, "未知面板图标");

        var path = new Path
        {
            Data = Geometry.Parse(RefreshCw),
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 6, 0),
            Stretch = Stretch.Uniform,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
        };
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = owner });
        return path;
    }
}
