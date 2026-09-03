using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Graph;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Components.Selection;
using HistoryAurora.Shell.Components.Table;
using HistoryAurora.Shell.HostedPages.Views;
using HistoryVulcan.Core.Commands;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 工具页不滚，只有组件内部滚（REQ-UI-050）。
///
/// **为什么这是对的**：页面级滚动和组件级滚动同时存在时，同一块版面有两套语义——
/// 鼠标停在控制面板上滚不动，往旁边挪两像素又能滚整页。实测下来「控制面板上滚不动」
/// 反而是唯一说得通的那一半：面板本来就不该滚（REQ-UI-034）。既然如此，
/// 该退场的是外面那一层，不是里面那一层。
///
/// 于是规则收成一条：**页面装不下就裁掉**，而表格、泳道这类自带视口的组件
/// 由顺序容器分给它们星号行（REQ-UI-042），在自己的框里滚。
///
/// 裁切是有意让「装不下」变成看得见的事故。滚动条会让一个排版错误一直活着，
/// 而且活得很舒服——没有人会去修一个「滚一下就能看到」的版面。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class PageScrollContractTests
{
    [Fact]
    public void HostedPagesHaveNoPageLevelScrollViewer()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var log = new MemoryShellLog();
            var bus = new CommandBus(registry, log);
            var actions = new ActionRegistry(bus, log);
            ComponentGalleryCommands.Register(registry);
            actions.DeclareLocal(ComponentGalleryCommands.Owner, ComponentGalleryCommands.Actions);

            var parsed = PageDescriptionReader.Read(
                HostedPageDescriptions.Json, ComponentGalleryCommands.Owner);
            Assert.True(parsed.Ok, parsed.Error);

            var offenders = new List<string>();

            foreach (var page in parsed.Value!.Pages)
            {
                var rendered = PageRenderer.Render(page, new PageRenderContext
                {
                    Bus = bus,
                    Log = log,
                    Owner = ComponentGalleryCommands.Owner,
                    Actions = actions,
                    Channels = new SelectionChannels(),
                });

                // 窗口必须真的显示：表格的 ScrollViewer 来自 ListView 模板，
                // 不套模板就一个都找不到——那样这条用例会以「全都合规」的形态空转。
                var host = new Window
                {
                    Width = 900,
                    Height = 700,
                    ShowActivated = false,
                    Content = PageRegistrar.Inset(rendered.Root),
                };
                host.Show();

                // 泵到模板套完为止。判据是「至少出现一个 ScrollViewer」——组件测试页里
                // 有表格，表格必然带一个；一个都等不到说明模板还没套，
                // 那时扫出来的「零违规」是假的。
                var templated = UiTestHost.PumpUntil(
                    () => Descendants<ScrollViewer>((Visual)host.Content).Any());
                Assert.True(templated, $"{page.Id}: 模板未套上，本轮扫描不作数");

                foreach (var scroll in Descendants<ScrollViewer>((Visual)host.Content))
                {
                    if (IsPageLevel(scroll))
                        offenders.Add($"{page.Id}: {Trail(scroll)}");
                }

                host.Close();
            }

            Assert.Empty(offenders);
        });
    }

    /// <summary>
    /// 包边这一层必须裁切。没有 <c>ClipToBounds</c> 的话，装不下的内容会画到窗格外面去，
    /// 症状比滚动条更糟——它会盖住相邻窗格，而且看不出是谁画的。
    /// </summary>
    [Fact]
    public void TheInsetClipsInsteadOfOverflowing()
    {
        UiTestHost.RunSta(() =>
        {
            var inset = PageRegistrar.Inset(new TextBlock { Text = "x" });
            Assert.True(inset.ClipToBounds);
            Assert.IsType<Border>(inset);
            Assert.Equal(new Thickness(12), ((Border)inset).Padding);
        });
    }

    /// <summary>
    /// 这个 ScrollViewer 是不是「页面级」的。
    ///
    /// 判据有两条，缺一条这条用例就没法用：
    ///
    /// <list type="number">
    ///   <item><b><c>TemplatedParent == null</c></b>。控件模板里的滚动视图不算页面级——
    ///     每个 <c>TextBox</c> 都自带一个 <c>PART_ContentHost</c>，下拉框的弹出层也有一个。
    ///     第一版按「祖先里有没有表格」判，这两类当场报成违规：**一个连输入框都容不下的
    ///     规则不会有人遵守**。而页面级滚动是有人写了一句 <c>new ScrollViewer</c> 挂进去的，
    ///     那种没有模板父级。</item>
    ///   <item>再排掉<b>自带视口的组件</b>。泳道图的视口正是代码 new 出来的
    ///     （<c>AuroraSwimlane._viewport</c>），只按第一条会把它误伤。
    ///     名单是白名单：新组件想滚就来登记一次，而不是悄悄多出一处页面级滚动。</item>
    /// </list>
    /// </summary>
    private static bool IsPageLevel(ScrollViewer scroll)
    {
        if (scroll.TemplatedParent != null)
            return false;

        for (var current = VisualTreeHelper.GetParent(scroll); current != null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is AuroraTable or AuroraSwimlane)
                return false;
        }

        return true;
    }

    private static string Trail(DependencyObject node)
    {
        var names = new List<string>();
        for (var current = node; current != null; current = VisualTreeHelper.GetParent(current))
            names.Add(current.GetType().Name);
        names.Reverse();
        return string.Join(" → ", names);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }
}
