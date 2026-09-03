using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using HistoryAurora.Shell.Components.Themes;

namespace HistoryAurora.Shell.Components.Widgets;

/// <summary>
/// 弹出层（REQ-UI-012）：一块浮起来的内容，点别处收回。
///
/// 为什么要有它：低频编辑器此前只能做成常驻面板，于是三五个输入框长期占着页面纵向空间，
/// 而它一周也用不到一次（见 b-Office/history/前端组件缺失需求书 C3）。
///
/// 内容**仍然是面板那三种小组件**，不另立一套声明——弹出层改的是"什么时候占版面"，
/// 不是"能放什么"。
///
/// **打开它有两条路，浮层本体是同一个**（REQ-UI-056）：
/// <list type="bullet">
///   <item>自带一个按钮（缺省）。按钮就在版面上，能看见"这里还有东西"；</item>
///   <item>接到某个元素的右键上（<see cref="AttachContextTrigger"/>）。
///         连按钮都不占版面，代价是看不见——因此只适合"页面上本来就有别的东西可看"的场合。</item>
/// </list>
/// 两条路共用同一个 <see cref="Popup"/> 与同一份内容：右键那条**不是**另一个组件，
/// 只是换了个开关。再写一套的话，两份浮层的圆角、阴影和收起时机会各走各的，
/// 而那种差异浅色下几乎看不出来。
/// </summary>
internal sealed class AuroraFlyout : UserControl
{
    private readonly Popup _popup;
    private readonly ToggleButton _button;
    private FrameworkElement? _contextHost;

    /// <param name="contextTriggered">
    /// true 时不画按钮，也不占版面，等 <see cref="AttachContextTrigger"/> 把它接到某个元素的右键上。
    /// </param>
    public AuroraFlyout(string text, FrameworkElement content, bool accent = false, bool contextTriggered = false)
    {
        AuroraComponentResources.Ensure(this);

        _button = new ToggleButton { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        _button.SetResourceReference(
            StyleProperty,
            accent ? "Aurora.Button.Accent" : "Aurora.Button.Base");

        var surface = new Border
        {
            Child = new ScrollViewer
            {
                Content = content,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            MinWidth = 260,
        };
        surface.SetResourceReference(StyleProperty, "Aurora.Flyout.Surface");

        // 弹出层在自己的窗口里，逻辑父级链断得早：不自带控件字典的话 Aurora.* 解析不到，
        // 而 WPF 对解析不到的 DynamicResource 不抛异常——表现为"只有这一块长得不一样"。
        AuroraComponentResources.Ensure(surface);

        _popup = new Popup
        {
            PlacementTarget = _button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = surface,
        };

        // 收起时把按钮的按下态一并复原：Popup 是自己关的（点了别处），
        // ToggleButton 并不知道，不同步的话按钮会一直显示成"还开着"。
        _popup.Closed += (_, _) => _button.IsChecked = false;
        _button.Checked += (_, _) => _popup.IsOpen = true;
        _button.Unchecked += (_, _) => _popup.IsOpen = false;

        Content = new Grid { Children = { _button, _popup } };

        if (!contextTriggered)
            return;

        // 右键那条路上按钮没有意义，但**不能把它从树上摘掉**：开合仍然走
        // ToggleButton.IsChecked（同一条路径，因此收起时的复原逻辑只有一份）。
        _button.Visibility = Visibility.Collapsed;

        // **本控件自己不能收成 Collapsed**：WPF 的 Popup 在不可见的父级下**开不出来**，
        // 且不抛异常也不触发 Closed——IsOpen 写进去就是 false，症状是"右键完全没反应"
        // （门禁里实测：同一段代码，父级可见时 IsOpen=True，Collapsed 时 IsOpen=False）。
        // 因此改成靠左上对齐、按内容定尺寸：按钮已经 Collapsed，内容尺寸就是 0，
        // 既不占版面又留在可见的树上。不写对齐的话它会按 Stretch 铺满整格。
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;

        // 右键弹在指针处。贴着按钮弹没有意义——按钮已经不在了。
        _popup.Placement = PlacementMode.MousePoint;
    }

    public bool IsOpen => _popup.IsOpen;

    public void Close() => _button.IsChecked = false;

    /// <summary>
    /// 把本浮层接到 <paramref name="host"/> 的右键上。
    /// </summary>
    /// <remarks>
    /// 用**冒泡**的 <see cref="UIElement.MouseRightButtonUpEvent"/>，而不是 Preview：
    /// 自带右键菜单的子元素（表格的行操作菜单）在冒泡途中先把事件标成已处理，
    /// 本处理器因此不会跟着弹一个第二层浮层。反过来，表格在空白处主动放弃菜单
    /// （<c>AuroraTable.OnRowContextMenuOpening</c> 判无行即取消），事件继续往上冒，
    /// 于是"右键行 → 行菜单、右键空白 → 本浮层"是自然结果，不需要谁去协调谁。
    ///
    /// 宿主必须**命中得到**。<see cref="Panel"/> 的 <c>Background</c> 为 null 时空白处
    /// 不参与命中测试，右键落在那里什么都不会发生——而那正是这条路唯一会被右键到的地方。
    /// 调用方负责给宿主一块（哪怕是透明的）底色。
    /// </remarks>
    public void AttachContextTrigger(FrameworkElement host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (_contextHost != null)
            _contextHost.MouseRightButtonUp -= OnHostRightButtonUp;

        _contextHost = host;
        _popup.PlacementTarget = host;
        host.MouseRightButtonUp += OnHostRightButtonUp;
    }

    private void OnHostRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 已经开着时右键当作"换个地方再开一次"：先关再开，浮层因此落在新的指针位置。
        _button.IsChecked = false;
        _button.IsChecked = true;
        e.Handled = true;
    }
}
