using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace HistoryAurora.Shell.Base;

/// <summary>
/// 页面浮窗（REQ-UI-138，1.30.1）：把一整页从停靠区搬进一张**置顶**的圆角卡片里，
/// 让人在操作别的程序（SolidWorks）时也够得着它。由 <c>aurora.ui.float</c> 浮出 / 还原。
///
/// 长相与嵌入弹窗同一张卡片（<c>Aurora.Dialog.Card</c>：Surface 底、页面圆角、阴影），没有标题栏与关闭键；
/// 按住卡片空白处拖动整窗（控件自己吃掉的按下不算空白），四边可拉伸。
///
/// 它**不是** AvalonDock 的浮窗：停靠层的浮窗只给页面拖动当载体（REQ-UI-120），
/// 这里不进布局树、不进场景快照，停靠区原位放一块「已浮出」占位，还原时页面对象原样搬回去。
/// 不设 Owner：主窗体最小化或被 SolidWorks 盖住时，浮窗照样在最上面。
/// </summary>
internal sealed class PageFloatWindow : Window
{
    /// <summary>卡片四周给阴影与拉伸边留的透明边。</summary>
    internal const double Gutter = 12;

    internal const double MinimumWidth = 360;

    internal const double MinimumHeight = 200;

    private readonly ContentPresenter _host = new();

    public PageFloatWindow(string pageId, string title, IEnumerable<ResourceDictionary> resources)
    {
        PageId = pageId;
        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Topmost = true;
        MinWidth = MinimumWidth;
        MinHeight = MinimumHeight;

        // 页面原先从主窗体与停靠管理器继承令牌和样式；搬出来以后资源链断了，把那两份并进来，
        // 换主题时它们原地换字典，这里跟着变（同 ShellWindow.SceneDrag 的拖动胶囊）。
        foreach (var dictionary in resources)
            Resources.MergedDictionaries.Add(dictionary);

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(Gutter / 2),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        var card = new Border { Child = _host, Margin = new Thickness(Gutter) };
        card.SetResourceReference(StyleProperty, "Aurora.Dialog.Card");
        // 页面自带 12 的内边距（PageRegistrar.Inset），卡片不再叠一层。
        card.Padding = new Thickness(0);

        // 透明像素在分层窗口里点不中：卡片下面垫一层几乎看不见的画布色，四周那圈才拖得动、拉得动。
        // 用令牌而不是字面颜色（ColorLiteralContractTests），透明度压在元素上。
        var hitArea = new Border { Opacity = 0.01 };
        hitArea.SetResourceReference(Border.BackgroundProperty, "Aurora.Brush.Canvas");
        Content = new Grid { Children = { hitArea, card } };

        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    public string PageId { get; }

    /// <summary>浮在窗里的那一页。换成 null 即把页面交还出去。</summary>
    public object? Page
    {
        get => _host.Content;
        set => _host.Content = value;
    }

    /// <summary>
    /// 冒泡到窗上的左键按下 = 没有控件要它 = 按在空白处，拖整窗。
    /// 按钮、输入框、表格行、滚动条都会先把它标成已处理，不会走到这里。
    /// </summary>
    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ButtonState != MouseButtonState.Pressed)
            return;
        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // 按下与 DragMove 之间键已抬起：这一下不拖。
        }
    }

    /// <summary>窗口外沿（含透明边）→ 卡片所在的矩形，与 <see cref="BoundsForCard"/> 互逆。</summary>
    public Rect CardBounds => new(Left + Gutter, Top + Gutter, Width - 2 * Gutter, Height - 2 * Gutter);

    /// <summary>卡片要落在 <paramref name="card"/>（DIP）时窗口该有的外沿。</summary>
    public static Rect BoundsForCard(Rect card)
        => new(card.Left - Gutter, card.Top - Gutter, card.Width + 2 * Gutter, card.Height + 2 * Gutter);
}
