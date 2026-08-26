using System.Windows;
using System.Text.Json;
using System.Windows.Controls;
using HistoryAurora.Shell.Pages;
using HistoryAurora.Shell.Table;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 页面注册协议（V1）的契约：解析要在出错时**明确失败**，渲染要在缺件时**看得见**，
/// 组件的动作要**真的落到指令总线上**。
///
/// 这三条对应改协议的三个动机——旧路径的失败形态分别是：类型被擦成 object 的运行期异常、
/// 找不到资源键时的静默退化、以及模块直接构造 WPF 对象带来的不可控耦合。
/// </summary>
public sealed class PageProtocolContractTests
{
    private const string MinimalPage = """
        {
          "schemaVersion": 1,
          "owner": "HistoryDemo",
          "pages": [
            { "id": "demo", "title": "演示", "content": { "type": "text", "text": "hi" } }
          ]
        }
        """;

    [Fact]
    public void Parse_AcceptsMinimalDescription()
    {
        var parsed = PageDescriptionReader.Read(MinimalPage, "HistoryDemo");

        Assert.True(parsed.Ok, parsed.Error);
        Assert.Equal("HistoryDemo", parsed.Value!.Owner);
        Assert.Equal("demo", Assert.Single(parsed.Value.Pages).Id);
    }

    [Fact]
    public void Parse_RejectsUnsupportedSchemaVersion()
    {
        var json = MinimalPage.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");

        var parsed = PageDescriptionReader.Read(json, "HistoryDemo");

        Assert.False(parsed.Ok);
        // 文本协议没有编译器保护，失败必须报出版本号，否则排查只能靠猜。
        Assert.Contains("schemaVersion=2", parsed.Error);
    }

    [Fact]
    public void Parse_RejectsOwnerMismatch()
    {
        // 防止一个模块借描述抢注另一个模块的页面。
        var parsed = PageDescriptionReader.Read(MinimalPage, "HistoryOther");

        Assert.False(parsed.Ok);
        Assert.Contains("HistoryOther", parsed.Error);
    }

    [Fact]
    public void Parse_RejectsDuplicatePageId()
    {
        var json = """
            {
              "schemaVersion": 1,
              "owner": "HistoryDemo",
              "pages": [
                { "id": "demo", "title": "一", "content": { "type": "text" } },
                { "id": "demo", "title": "二", "content": { "type": "text" } }
              ]
            }
            """;

        var parsed = PageDescriptionReader.Read(json, "HistoryDemo");

        Assert.False(parsed.Ok);
        Assert.Contains("重复", parsed.Error);
    }

    [Fact]
    public void Parse_RejectsPageWithoutContent()
    {
        var json = """
            {
              "schemaVersion": 1,
              "owner": "HistoryDemo",
              "pages": [ { "id": "demo", "title": "演示" } ]
            }
            """;

        var parsed = PageDescriptionReader.Read(json, "HistoryDemo");

        Assert.False(parsed.Ok);
        Assert.Contains("缺少内容", parsed.Error);
    }

    [Fact]
    public void Parse_RejectsMalformedJson()
    {
        var parsed = PageDescriptionReader.Read("{ not json", "HistoryDemo");

        Assert.False(parsed.Ok);
        Assert.Contains("合法 JSON", parsed.Error);
    }

    [Fact]
    public void Render_MapsKnownComponentsToAuroraControls()
    {
        UiTestHost.RunSta(() =>
        {
            var page = Page("""
                {
                  "type": "stack",
                  "children": [
                    { "type": "text", "text": "标题" },
                    { "type": "button", "text": "执行" },
                    { "type": "input", "text": "abc" },
                    { "type": "select" }
                  ]
                }
                """);

            var rendered = PageRenderer.Render(page, Context(out _, out _));

            var stack = Assert.IsType<StackPanel>(rendered.Root);
            Assert.IsType<TextBlock>(stack.Children[0]);
            Assert.IsType<Button>(stack.Children[1]);
            Assert.IsType<TextBox>(stack.Children[2]);
            Assert.IsType<ComboBox>(stack.Children[3]);
            Assert.Empty(rendered.MissingComponents);
        });
    }

    [Fact]
    public void Render_UnknownComponentBecomesVisiblePlaceholderAndIsReported()
    {
        UiTestHost.RunSta(() =>
        {
            // toggleGroup 是真实缺件：组件库有 Aurora.Segment.CheckBox，没有 Segment.Toggle。
            // 它同时是 DEC-005 解禁「模块不得自建组件」的前置。
            var page = Page("""{ "type": "toggleGroup", "id": "mode" }""");

            var rendered = PageRenderer.Render(page, Context(out _, out _));

            // 必须是看得见的一块，而不是空白——缺了一块用肉眼是看不出来的。
            var border = Assert.IsType<Border>(rendered.Root);
            var text = Assert.IsType<TextBlock>(border.Child);
            Assert.Contains("toggleGroup", text.Text);

            // 并且必须被报出来，才能变成可查询的事实而不是等人发现。
            Assert.Equal("toggleGroup", Assert.Single(rendered.MissingComponents));
        });
    }

    [Fact]
    public void Render_ButtonInvokeReachesTheCommandBus()
    {
        UiTestHost.RunSta(() =>
        {
            var page = Page("""
                {
                  "type": "button",
                  "text": "执行",
                  "invoke": { "command": "test.echo", "args": { "name": { "value": "带 空格" } } }
                }
                """);

            var context = Context(out var executed, out _);
            var rendered = PageRenderer.Render(page, context);

            var button = Assert.IsType<Button>(rendered.Root);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase_Click));
            UiTestHost.PumpUntil(() => executed.Count > 0);

            // 参数带空格：必须经过 QuoteArg，否则会被解析成两个参数。
            Assert.Equal("""test.echo name="带 空格" """.TrimEnd(), Assert.Single(executed));
        });
    }

    [Fact]
    public void Render_TableLoadsRowsThroughBusAndFeedsSelectionIntoInvoke()
    {
        UiTestHost.RunSta(() =>
        {
            var page = Page("""
                {
                  "type": "stack",
                  "children": [
                    {
                      "type": "table",
                      "id": "rows",
                      "dataSource": { "command": "test.rows" },
                      "columns": [ { "key": "name", "title": "名称" } ],
                      "view": { "selection": "single" }
                    },
                    {
                      "type": "button",
                      "text": "复制",
                      "enabledWhen": { "selected": "rows" },
                      "invoke": {
                        "command": "test.echo",
                        "args": { "name": { "from": "rows.selected.name" } }
                      }
                    }
                  ]
                }
                """);

            var context = Context(out var executed, out _);
            var rendered = PageRenderer.Render(page, context);

            var stack = Assert.IsType<StackPanel>(rendered.Root);
            // REQ-UI-007：table 节点渲染成 AuroraTable，不再是页面自拼的 ListView。
            var table = Assert.IsType<AuroraTable>(stack.Children[0]);
            var button = Assert.IsType<Button>(stack.Children[1]);

            // Render 只建组件，不在宿主建页路径上执行模块取数。
            Assert.Empty(executed);
            Assert.Equal(0, table.RowCount);

            table.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

            // 控件 Loaded 后才走总线，异步回填。
            Assert.True(UiTestHost.PumpUntil(() => table.RowCount == 2), "表格未从总线取到行");

            // 没有选中行时按钮应禁用——enabledWhen 是视图行为，不产生命令。
            Assert.False(button.IsEnabled);

            table.SelectedIndex = 1;
            UiTestHost.PumpUntil(() => button.IsEnabled);
            Assert.True(button.IsEnabled);

            executed.Clear();
            button.RaiseEvent(new RoutedEventArgs(ButtonBase_Click));
            UiTestHost.PumpUntil(() => executed.Count > 0);

            // 选中行的单元格值被取出并作为参数送上总线。
            Assert.Equal("test.echo name=beta", Assert.Single(executed));
        });
    }

    [Fact]
    public void Render_DanglingEnabledWhenDisablesButtonAndWarns()
    {
        UiTestHost.RunSta(() =>
        {
            var page = Page("""
                {
                  "type": "button",
                  "text": "执行",
                  "enabledWhen": { "selected": "nosuchnode" },
                  "invoke": { "command": "test.echo" }
                }
                """);

            var rendered = PageRenderer.Render(page, Context(out _, out var log));

            // 引用不存在的节点时按钮禁用并留日志，而不是当作永远可用。
            Assert.False(Assert.IsType<Button>(rendered.Root).IsEnabled);
            Assert.Contains(log.Snapshot(), e =>
                e.Level == ShellLogLevel.Warn && e.Message.Contains("nosuchnode"));
        });
    }

    private static readonly RoutedEvent ButtonBase_Click =
        System.Windows.Controls.Primitives.ButtonBase.ClickEvent;

    private static PageDescription Page(string contentJson)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "owner": "HistoryDemo",
              "pages": [ { "id": "demo", "title": "演示", "content": {{contentJson}} } ]
            }
            """;
        var parsed = PageDescriptionReader.Read(json, "HistoryDemo");
        Assert.True(parsed.Ok, parsed.Error);
        return parsed.Value!.Pages[0];
    }

    private static PageRenderContext Context(out List<string> executed, out MemoryLog log)
    {
        var captured = new List<string>();
        var memory = new MemoryLog();
        var registry = new CommandRegistry();

        registry.Register(new CommandDescriptor
        {
            Name = "test.echo",
            Domain = "test",
            CommandClass = "core",
            Summary = "测试回显",
            Readonly = true,
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "test.rows",
            Domain = "test",
            CommandClass = "core",
            Summary = "测试行集",
            Readonly = true,
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(_ =>
                CommandResult.Ok("暂无数据", JsonDocument.Parse("""[{"name":"alpha"},{"name":"beta"}]""").RootElement.Clone())),
        });

        var bus = new CommandBus(registry, memory);
        bus.Executed += (text, _, _) => captured.Add(text);

        executed = captured;
        log = memory;
        return new PageRenderContext { Bus = bus, Log = memory, Owner = "HistoryDemo" };
    }

    private sealed class MemoryLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = [];

        public void Log(ShellLogLevel level, string category, string message)
        {
            var entry = new ShellLogEntry(DateTime.Now, level, category, message);
            _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => _entries.ToList();
    }
}
