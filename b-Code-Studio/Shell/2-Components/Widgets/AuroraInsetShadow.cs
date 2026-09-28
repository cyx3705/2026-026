using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HistoryAurora.Shell.Components.Widgets;

/// <summary>
/// 卡片内阴影（REQ-UI-131，1.27.1）。WPF 没有内阴影，这里的做法是：
/// 在卡片外缘画一圈不透明的环（环本身落在卡片外、被圆角裁掉），
/// 环的模糊投影往里渗进卡片边缘一圈。
///
/// 盖在卡片最上层、不接收输入（<see cref="UIElement.IsHitTestVisible"/> = false），
/// 按 <see cref="CornerRadius"/> 自己裁成圆角——WPF 的 Border.CornerRadius 不裁剪子元素，
/// 不自己裁的话四个角会露出环的直角投影。
///
/// 投影参数全在令牌 <c>Aurora.Shadow.CardInset</c>，是否显示看 <c>Aurora.Card.InsetVisibility</c>：
/// 浅色显示（卡片比底部深，外投影会变成发光感），深色收起（深色沿用外投影）。
/// 环的颜色无所谓——投影色由效果决定，环只提供形状——借卡片底色 Surface：
/// 万一环与裁剪对不齐，露出来的也只是卡片色，不会是一块黑（1.27.1 首版借了正文色，四角各露一块死黑）。
/// </summary>
internal sealed class AuroraInsetShadow : Decorator
{
    /// <summary>环的宽度：要比投影的模糊半径宽，否则投影外缘会被环的外沿截断。</summary>
    private const double RingThickness = 16;

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(AuroraInsetShadow),
        new FrameworkPropertyMetadata(default(CornerRadius), (d, _) => ((AuroraInsetShadow)d).Rebuild()));

    private readonly Border _ring = new();

    public AuroraInsetShadow()
    {
        IsHitTestVisible = false;
        Focusable = false;
        _ring.BorderThickness = new Thickness(RingThickness);
        _ring.Margin = new Thickness(-RingThickness);
        _ring.SetResourceReference(Border.BorderBrushProperty, "Aurora.Brush.Surface");
        _ring.SetResourceReference(EffectProperty, "Aurora.Shadow.CardInset");
        Child = _ring;
        SetResourceReference(VisibilityProperty, "Aurora.Card.InsetVisibility");
        SizeChanged += (_, _) => Rebuild();
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private void Rebuild()
    {
        var r = CornerRadius;
        // 环的内沿要与卡片外缘重合。注意 Border.CornerRadius 量的是**描边中线**:
        // 内沿圆角 = CornerRadius - 环宽/2,所以这里只加半个环宽。加整个环宽的话内沿圆角大出一圈,
        // 四个角上环的实心部分会侵入卡片(首版就是这样露出四块黑角)。
        var half = RingThickness / 2;
        _ring.CornerRadius = new CornerRadius(
            r.TopLeft + half, r.TopRight + half, r.BottomRight + half, r.BottomLeft + half);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        // 卡片四角同一个圆角(Aurora.Radius.Page),RectangleGeometry 只收一个半径,取左上即可
        Clip = new RectangleGeometry(new Rect(0, 0, w, h), r.TopLeft, r.TopLeft);
    }
}
