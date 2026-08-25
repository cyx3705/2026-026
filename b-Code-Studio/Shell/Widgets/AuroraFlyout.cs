using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using HistoryAurora.Shell.Themes;

namespace HistoryAurora.Shell.Widgets;

/// <summary>
/// 弹出层（REQ-UI-012）：一个按钮，点开是一块浮起来的内容，点别处收回。
///
/// 为什么要有它：低频编辑器此前只能做成常驻面板，于是三五个输入框长期占着页面纵向空间，
/// 而它一周也用不到一次（见 b-Office/history/前端组件缺失需求书 C3）。
///
/// 内容**仍然是面板那三种小组件**，不另立一套声明——弹出层改的是"什么时候占版面"，
/// 不是"能放什么"。
/// </summary>
internal sealed class AuroraFlyout : UserControl
{
    private readonly Popup _popup;
    private readonly ToggleButton _button;

    public AuroraFlyout(string text, FrameworkElement content, bool accent = false)
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
    }

    public bool IsOpen => _popup.IsOpen;

    public void Close() => _button.IsChecked = false;
}
