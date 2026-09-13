using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 表头最右侧那一格（2026-08-25 真机反馈：「模块页表格最右侧多注册了一列」）。
///
/// 那不是一列。<c>GridViewHeaderRowPresenter</c> 总会在末尾多渲染一个
/// <c>Role=Padding</c> 的表头去盖住"列宽之和"与"表宽"之间的余量，而它和真正的列
/// **共用同一个容器样式**：背景、悬停高亮、右缘那条发丝线一并套上去，看起来就是一列空的。
/// 星号列刻意留了 20px 滚动余量，那块占位因此一定显形。
///
/// 修法是给它单独一个模板（只剩背景）。这条门禁钉住这个修法本身——
/// 谁把触发器删掉，那"多出来的一列"就会原样回来，而它在浅色下几乎看不出。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class TableHeaderContractTests
{
    [Fact]
    public void GridHeaderStyle_GivesThePaddingHeaderItsOwnTemplate()
    {
        UiTestHost.RunSta(() =>
        {
            // 不自己按 URI 去加载字典：测试进程没有 Application，
            // pack://application 的 authority 会退到入口程序集（testhost.exe）而解析不到。
            // 走组件自己的那条路——AuroraComponentResources 把控件字典并进元素资源，
            // 这也正是真机上表头样式的取用方式。
            var table = new HistoryAurora.Shell.Components.Table.AuroraTable();
            var style = Assert.IsType<Style>(table.TryFindResource("Aurora.GridHeader"));
            var trigger = Assert.Single(
                style.Triggers.OfType<Trigger>(),
                candidate => candidate.Property == GridViewColumnHeader.RoleProperty);

            Assert.Equal(GridViewColumnHeaderRole.Padding, trigger.Value);

            // 换的必须是**模板**：只改背景的话，右缘那条分隔线还在，看起来仍是一列。
            var setter = Assert.Single(trigger.Setters.OfType<Setter>(), item => item.Property == Control.TemplateProperty);
            Assert.Contains(trigger.Setters.OfType<Setter>(), item => item.Property == UIElement.VisibilityProperty && Equals(item.Value, Visibility.Collapsed));
            Assert.Equal(Control.TemplateProperty, setter.Property);
        });
    }
}
