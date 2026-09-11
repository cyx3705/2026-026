using System.Windows;
using System.Windows.Shell;
using AvalonDock.Controls;
using HistoryAurora.Shell.Components.Themes;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 浮窗的窗口外观契约，以及它与"能不能叠回布局"的真实关系。
///
/// 这一条被改错过两轮，所以把结论写在这里：
/// <list type="number">
///   <item>AvalonDock 的 <c>LayoutFloatingWindowControl.FilterMessage</c> 只处理
///         <c>WM_SYSCOMMAND</c>（且仅最大化/还原）、<c>WM_LBUTTONUP</c>、
///         <c>WM_MOVING</c>、<c>WM_EXITSIZEMOVE</c>。**它不看任何非客户区消息。**
///         建 DragService（那组蓝色停靠指示）的是 <c>WM_MOVING → UpdateDragPosition</c>，
///         也就是"窗口正被系统的移动循环拖着"这件事本身。</item>
///   <item>因此 <c>WindowChrome.CaptionHeight</c> 与停靠无关，必须保持 0：
///         抬高它会把页头变成非客户区，WPF 收不到鼠标按下，
///         Aurora 那条会去调 <c>DragMove()</c> 的拖动手势就起不来了。</item>
/// </list>
/// 拖放本身在自动化里驱动不了（要真实的 Win32 移动循环），所以这里只钉外观前提。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class FloatingWindowChromeContractTests
{
    [Theory]
    [InlineData(typeof(LayoutAnchorableFloatingWindowControl))]
    [InlineData(typeof(LayoutDocumentFloatingWindowControl))]
    public void FloatingWindowKeepsItsHeaderInTheClientArea(Type floatingWindowType)
    {
        UiTestHost.RunSta(() =>
        {
            var style = Assert.IsType<Style>(DockingDictionary()[floatingWindowType]);
            var setter = Assert.Single(
                style.Setters.OfType<Setter>(),
                candidate => candidate.Property == WindowChrome.WindowChromeProperty);
            var chrome = Assert.IsType<WindowChrome>(setter.Value);

            Assert.Equal(0, chrome.CaptionHeight);
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
}
