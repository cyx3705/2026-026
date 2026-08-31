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
    public void Column_ExposesOnlyDataShapeAndControlledCellAction()
    {
        var names = typeof(AuroraTableColumn)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name => name is not ("IsStar" or "FixedWidth"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["CellAction", "Key", "Title", "Width"], names);
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

    /// <summary>
    /// 没被声明成列的键**原样留在选中行里**（REQ-UI-058）。
    ///
    /// 表格只画声明过的列，但选中行与行操作拿到的是整行。命令集把完整指令名
    /// <c>name</c> 从列里去掉之后，右键菜单的四条动作、指令详情页与对外契约
    /// <c>IShellCommandWorkbenchHost.CommandSelection</c> 都靠这一条活着——
    /// 裁掉的话它们会一起断，而断法是「点了没反应」，不是报错。
    /// </summary>
    [Fact]
    public void SetData_KeepsKeysThatWereNotDeclaredAsColumns()
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable();
            table.SetData(AuroraTableData.Create(
                [new AuroraTableColumn("method", "方法")],
                [new Dictionary<string, string>
                {
                    ["method"] = "rename",
                    ["name"] = "demo.branch.rename",
                }]));

            // 画出来的只有声明的那一列。
            Assert.Equal(1, table.ColumnCount);

            table.SelectedIndex = 0;
            var row = table.SelectedRow;
            Assert.NotNull(row);
            Assert.Equal("demo.branch.rename", row!["name"]);
        });
    }

    /// <summary>
    /// 列拖得动，而占满剩余宽度的那一列钉在最右（REQ-UI-062）。
    ///
    /// 星号列是「吃掉剩余」的那一列，不在最后就没有「剩余」可占：
    /// 拖它、或者把别的列拖到它右边，都会让它当场被拨回最右。
    /// 一条规则盖住两种拖法——分开写的话总有一种拖法两边都没管到。
    /// </summary>
    [Fact]
    public void Columns_AreReorderableButTheStarColumnStaysLast()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryColumnOrder();
            var table = new AuroraTable();
            table.SetData(AuroraTableData.Create(
                [
                    new AuroraTableColumn("a", "甲", "80"),
                    new AuroraTableColumn("b", "乙", "80"),
                    new AuroraTableColumn("c", "丙", "*"),
                ],
                [new Dictionary<string, string> { ["a"] = "1", ["b"] = "2", ["c"] = "3" }]));
            table.UseColumnOrder(store, "demo/page/table");

            var view = GridView(table);
            Assert.True(view.AllowsColumnReorder, "列拖不动");

            // 把「乙」拖到最前：普通列之间随便换。
            view.Columns.Move(1, 0);
            UiTestHost.Pump();
            Assert.Equal(["乙", "甲", "丙"], Headers(view));
            Assert.Equal(["b", "a", "c"], store.Read("demo/page/table"));

            // 把星号列拖到最前：它会被拨回最右。
            view.Columns.Move(2, 0);
            UiTestHost.Pump();
            Assert.Equal(["乙", "甲", "丙"], Headers(view));
        });
    }

    /// <summary>
    /// 记住的列序在下次建表时被用上，而声明变了也不会把表弄坏（REQ-UI-062）。
    ///
    /// 记录里有、声明里没有的键丢掉；声明里有、记录里没有的列按声明顺序补在后面。
    /// 不做这两条对齐的话，改一次列声明就会让老用户的表少一列或者多一列空白。
    /// </summary>
    [Fact]
    public void Columns_RestoreTheRememberedOrderAndTolerateADeclarationChange()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryColumnOrder();
            store.Write("demo/page/table", ["c", "b", "gone"]);

            var table = new AuroraTable();
            table.UseColumnOrder(store, "demo/page/table");
            table.SetData(AuroraTableData.Create(
                [
                    new AuroraTableColumn("a", "甲"),
                    new AuroraTableColumn("b", "乙"),
                    new AuroraTableColumn("c", "丙"),
                ],
                []));

            // c、b 按记录排在前面；记录里没有的 a 按声明顺序补在后面；gone 直接丢掉。
            Assert.Equal(["丙", "乙", "甲"], Headers(GridView(table)));
        });
    }

    /// <summary>没有身份的表不记列序：拖完也认不出是哪一张，记下来只会张冠李戴。</summary>
    [Fact]
    public void Columns_AreNotRememberedForATableWithoutAnIdentity()
    {
        UiTestHost.RunSta(() =>
        {
            var store = new MemoryColumnOrder();
            var table = new AuroraTable();
            table.SetData(AuroraTableData.Create(
                [new AuroraTableColumn("a", "甲"), new AuroraTableColumn("b", "乙")],
                []));
            table.UseColumnOrder(store, null);

            GridView(table).Columns.Move(1, 0);
            UiTestHost.Pump();

            Assert.Empty(store.Entries);
        });
    }

    [Fact]
    public void SetData_WidthJitterAfterLayoutDoesNotHang()
    {
        UiTestHost.RunSta(() =>
        {
            var table = new AuroraTable { Width = 640, Height = 240 };
            var host = new System.Windows.Window
            {
                Content = table,
                Width = 680,
                Height = 320,
                ShowInTaskbar = false,
                WindowStyle = System.Windows.WindowStyle.None,
            };
            try
            {
                host.Show();
                table.SetData(AuroraTableData.Create(
                    [
                        new AuroraTableColumn("file", "文件", "220"),
                        new AuroraTableColumn("status", "状态", "90"),
                        new AuroraTableColumn("detail", "详情", "*"),
                    ],
                    Enumerable.Range(0, 24).Select(index => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
                    {
                        ["file"] = $"part-{index}.par",
                        ["status"] = "就绪",
                        ["detail"] = "ok",
                    }).ToList()));
                UiTestHost.Pump();

                for (var round = 0; round < 20; round++)
                {
                    host.Width = 680 + (round % 3);
                    UiTestHost.Pump();
                }

                Assert.Equal(24, table.RowCount);
            }
            finally
            {
                host.Close();
            }
        });
    }

    private static System.Windows.Controls.GridView GridView(AuroraTable table)
    {
        var surface = Assert.IsType<System.Windows.Controls.Border>(table.Content);
        var grid = Assert.IsType<System.Windows.Controls.Grid>(surface.Child);
        var list = grid.Children.OfType<System.Windows.Controls.ListView>().Single();
        return Assert.IsType<System.Windows.Controls.GridView>(list.View);
    }

    private static string[] Headers(System.Windows.Controls.GridView view)
        => view.Columns.Select(column => column.Header as string ?? "").ToArray();

    /// <summary>内存里的列序台账。落盘那一路归设置服务，这里只验规则。</summary>
    private sealed class MemoryColumnOrder : IColumnOrderStore
    {
        public Dictionary<string, List<string>> Entries { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Read(string key)
            => Entries.TryGetValue(key, out var order) ? order : [];

        public void Write(string key, IReadOnlyList<string> columnKeys)
            => Entries[key] = columnKeys.ToList();
    }
}
