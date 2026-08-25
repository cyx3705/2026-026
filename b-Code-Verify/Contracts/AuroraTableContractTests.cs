using System.Reflection;
using HistoryAurora.Shell.Table;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 表格组件的契约（REQ-UI-007）。三条是这个组件存在的理由：
/// <list type="number">
///   <item>宿主**只能**提供数据与列宽——多一个外观入口，"只有这一页长得不一样"就会回来；</item>
///   <item>行数与列数**由数据决定**，不接受第二份计数；</item>
///   <item>行里缺的键补空串——不补的话字典索引器会抛，WPF 把它吞成一条看不见的绑定错误。</item>
/// </list>
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class AuroraTableContractTests
{
    /// <summary>
    /// 列声明的公开面就是"宿主能决定的全部外观"。这条断言把它钉死：
    /// 谁想加一个 <c>Background</c> 或 <c>FontSize</c>，必须先来改这个测试，
    /// 那时才会重新读一遍为什么当初不给。
    /// </summary>
    [Fact]
    public void Column_ExposesOnlyKeyTitleAndWidth()
    {
        var names = typeof(AuroraTableColumn)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name => name is not ("IsStar" or "FixedWidth"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Key", "Title", "Width"], names);
    }

    [Fact]
    public void FromRows_DerivesColumnsInFirstSeenOrder()
    {
        var data = AuroraTableData.FromRows([
            new Dictionary<string, string> { ["name"] = "alpha", ["kind"] = "a" },
            // 第二行多一个键：列取并集，且新键排在后面，不打乱已有列序。
            new Dictionary<string, string> { ["name"] = "beta", ["note"] = "n" },
        ]);

        Assert.Equal(["name", "kind", "note"], data.Columns.Select(column => column.Key));
        Assert.Equal(2, data.RowCount);
        Assert.Equal(3, data.ColumnCount);
    }

    [Fact]
    public void Create_FallsBackToDerivedColumnsWhenNoneDeclared()
    {
        var data = AuroraTableData.Create(null, [
            new Dictionary<string, string> { ["only"] = "x" },
        ]);

        Assert.Equal("only", Assert.Single(data.Columns).Key);
    }

    [Fact]
    public void FromItems_KeepsDeclaredOrderAndWidths()
    {
        var data = AuroraTableData.FromItems(
            new[] { ("a", 1), ("b", 2) },
            ("名称", "160", item => item.Item1),
            ("计数", AuroraTableColumn.Star, item => item.Item2.ToString()));

        Assert.Equal(["名称", "计数"], data.Columns.Select(column => column.Title));
        Assert.Equal(160, data.Columns[0].FixedWidth);
        Assert.True(data.Columns[1].IsStar);
        Assert.Null(data.Columns[1].FixedWidth);
        Assert.Equal("b", data.Rows[1][data.Columns[0].Key]);
    }

    [Fact]
    public void Width_FallsBackToAutoWhenUnparsable()
    {
        // 一列宽度写错不该让整张表消失，退回自适应即可。
        var column = new AuroraTableColumn("k", "标题", "很宽");

        Assert.Null(column.FixedWidth);
        Assert.False(column.IsStar);
    }

    [Fact]
    public void SetData_FillsMissingCellsAndReportsCounts()
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable();
            table.SetData(AuroraTableData.Create(
                [new AuroraTableColumn("name", "名称"), new AuroraTableColumn("note", "备注")],
                [new Dictionary<string, string> { ["name"] = "alpha" }]));

            Assert.Equal(1, table.RowCount);
            Assert.Equal(2, table.ColumnCount);

            table.SelectedIndex = 0;
            var row = table.SelectedRow;
            Assert.NotNull(row);
            Assert.Equal("alpha", row!["name"]);
            // 缺的键补成空串，而不是让绑定去撞 KeyNotFoundException。
            Assert.Equal("", row["note"]);
        });
    }

    [Fact]
    public void SetData_NullClearsWithoutThrowing()
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable();
            table.SetData(AuroraTableData.FromRows([new Dictionary<string, string> { ["k"] = "v" }]));
            table.SetData(null);

            Assert.Equal(0, table.RowCount);
            Assert.Null(table.SelectedRow);
        });
    }
}
