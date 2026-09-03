using System.Windows.Controls;
using HistoryAurora.Shell.Components.Table;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 行操作的契约（REQ-UI-011）。这个组件存在的理由只有一条：
/// 1.6.0 的表格只能选行，行动作只好摆到表格下面排成一排，
/// 于是"看着哪行点哪行"退化成"先上去选行、再下来点按钮"（见前端组件缺失需求书 C1/C2）。
///
/// 因此三件事必须成立：
/// <list type="number">
///   <item>一份声明出**两个**出口——行内按钮与右键菜单，不是二选一；</item>
///   <item>触发时带的是**被操作的那一行**，不是"当前选中行"；</item>
///   <item>没有行操作时右键**不弹菜单**，而不是弹一个空的。</item>
/// </list>
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class RowActionContractTests
{
    [Fact]
    public void RowActions_AddOneTrailingColumnAndOneMenu()
    {
        UiTestHost.RunSta(() =>
        {
            var table = Filled();
            table.SetRowActions([
                new AuroraRowAction("pin", "固定"),
                new AuroraRowAction("drop", "移除", Danger: true),
                new AuroraRowAction("refresh", "刷新", Inline: false),
            ]);

            var (headers, menu) = table.Inspect();

            // 数据列不变，行内按钮只多出**一列**——不是每条动作一列。
            Assert.Equal(2, table.ColumnCount);
            Assert.Equal(["名称", "备注", "操作"], headers);

            // 只进菜单的那条不占行内位置，但菜单里三条都在。
            Assert.NotNull(menu);
            Assert.Equal(
                ["固定", "移除", "刷新"],
                menu!.Items.Cast<MenuItem>().Select(item => item.Header as string));
        });
    }

    [Fact]
    public void NoRowActions_MeansNoContextMenuAndNoExtraColumn()
    {
        UiTestHost.RunSta(() =>
        {
            var table = Filled();
            table.SetRowActions([new AuroraRowAction("pin", "固定")]);
            table.SetRowActions(null);

            var (headers, menu) = table.Inspect();

            // 空菜单会让右键"看起来有东西可点"，比不弹更费解。
            Assert.Null(menu);
            Assert.Equal(["名称", "备注"], headers);
        });
    }

    [Fact]
    public void Invoke_CarriesTheOperatedRowNotTheSelectedOne()
    {
        UiTestHost.RunSta(() =>
        {
            var table = Filled();
            table.SetRowActions([new AuroraRowAction("pin", "固定")]);

            AuroraRowActionEventArgs? captured = null;
            table.RowActionInvoked += (_, e) => captured = e;

            // 故意把选中放在第一行，再对第二行发起动作。
            table.SelectedIndex = 0;
            table.Fire("pin", new Dictionary<string, string> { ["名称"] = "beta" });

            Assert.NotNull(captured);
            Assert.Equal("pin", captured!.Action.Id);
            Assert.Equal("beta", captured.Row["名称"]);
        });
    }

    [Fact]
    public void Invoke_IgnoresIdsThatAreNotDeclared()
    {
        UiTestHost.RunSta(() =>
        {
            var table = Filled();
            table.SetRowActions([new AuroraRowAction("pin", "固定")]);

            var fired = 0;
            table.RowActionInvoked += (_, _) => fired++;
            table.Fire("gone", new Dictionary<string, string>());

            Assert.Equal(0, fired);
        });
    }

    private static AuroraTable Filled()
    {
        var table = new AuroraTable();
        table.SetData(AuroraTableData.Create(
            [new AuroraTableColumn("name", "名称"), new AuroraTableColumn("note", "备注")],
            [
                new Dictionary<string, string> { ["name"] = "alpha" },
                new Dictionary<string, string> { ["name"] = "beta" },
            ]));
        return table;
    }
}
