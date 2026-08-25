using System.Windows;
using System.Windows.Shell;
using AvalonDock.Controls;
using HistoryAurora.Shell.Themes;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 浮窗必须留着一条**真正的**标题栏高度，否则拖出去就叠不回来。
///
/// AvalonDock 的 <c>LayoutFloatingWindowControl</c> 是在收到 <c>WM_NCLBUTTONDOWN</c>
/// 且命中 <c>HTCAPTION</c> 时才创建 DragService——显示停靠指示器、松手时把窗口叠回布局，
/// 全靠那个服务。<c>WindowChrome.CaptionHeight="0"</c> 会让整个窗口都算客户区，
/// 那条消息永远不会到达。
///
/// 症状因此是"只坏了一半"：拖**出**是在主窗体里由 DockingManager 发起的，走另一条路，
/// 照常能用；拖**回**没有任何反应。2026-08-25 真机报的正是这个。
///
/// 拖放本身在自动化里驱动不了（要真实的 Win32 移动循环），所以这条门禁只钉住前提条件。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class FloatingWindowChromeContractTests
{
    [Theory]
    [InlineData(typeof(LayoutAnchorableFloatingWindowControl))]
    [InlineData(typeof(LayoutDocumentFloatingWindowControl))]
    public void FloatingWindowKeepsACaptionAvalonDockCanSee(Type floatingWindowType)
    {
        UiTestHost.RunSta(() =>
        {
            var docking = DockingDictionary();

            var style = Assert.IsType<Style>(docking[floatingWindowType]);
            var setter = Assert.Single(
                style.Setters.OfType<Setter>(),
                candidate => candidate.Property == WindowChrome.WindowChromeProperty);
            var chrome = Assert.IsType<WindowChrome>(setter.Value);

            Assert.True(
                chrome.CaptionHeight > 0,
                $"{floatingWindowType.Name} 的 CaptionHeight 是 {chrome.CaptionHeight}，"
                + "浮窗将收不到 WM_NCLBUTTONDOWN/HTCAPTION，也就叠不回布局里");
        });
    }

    /// <summary>
    /// 按 URI 取字典要先有一次 WPF 资源上下文的初始化，否则测试进程里
    /// 第一次解析会报 NotSupportedException（"URI prefix is not recognized"）。
    /// 先建一个组件即可——这也正是真机上的顺序。
    /// </summary>
    private static ResourceDictionary DockingDictionary()
    {
        AuroraComponentResources.Ensure(new System.Windows.Controls.Border());
        return new ResourceDictionary
        {
            Source = new Uri(
                "/HistoryAurora;component/Themes/AuroraDocking.xaml",
                UriKind.Relative),
        };
    }

    [Fact]
    public void TabsAndPaneButtonsStayClickableInsideThatCaption()
    {
        UiTestHost.RunSta(() =>
        {
            var docking = DockingDictionary();

            // 标题栏高度一旦 > 0，落在里面的东西默认全变成"拖窗口"的手柄。
            // 页签与窗格动作按钮必须显式声明豁免，否则页签点不动、关闭按钮按不了。
            foreach (var key in new[]
                     {
                         "Aurora.Docking.DocumentTabItemStyle",
                         "Aurora.Docking.AnchorableTabItemStyle",
                         "Aurora.Docking.PaneActionButton",
                     })
            {
                var style = Assert.IsType<Style>(docking[key]);
                var setter = Assert.Single(
                    style.Setters.OfType<Setter>(),
                    candidate => candidate.Property == WindowChrome.IsHitTestVisibleInChromeProperty);
                Assert.Equal(true, setter.Value);
            }
        });
    }
}
