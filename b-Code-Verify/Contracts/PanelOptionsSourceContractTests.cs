using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.Logging;
using HistoryAurora.Shell.Panels;
using HistoryAurora.Shell.Selection;
using HistoryAurora.Shell.Widgets;
using HistoryVulcan.Core.Commands;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 选择框的动态候选与两级联动（REQ-UI-059）。
///
/// **这条能力是被一页真实的界面逼出来的。** 命令集的「域」「类」是两级联动下拉——
/// 域选了才能选类，类的取值范围随域收敛（DEC-021）。1.8.18 把那一页改成描述式时
/// 两个下拉整个消失了，记在案的理由是「联动下拉的候选是动态的，而控制面板的选项框
/// 只收静态候选」。那句话是实话：<c>options</c> 是写死在描述里的常量数组。
///
/// 于是这一版补的是**组件能力**，不是给命令集开后门：形状与表格取数是同一套——
/// 一条只读指令加固定参数，参数值里可以写 <c>{selection.&lt;通道&gt;.&lt;列&gt;}</c>，
/// 通道一变就重取。别的模块拿到的是同一件东西。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class PanelOptionsSourceContractTests
{
    private const string DomainChannel = "demo.domain";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 候选真的从指令里来，而不是停在一个空下拉上。
    ///
    /// 判据是**框里的候选项**，不是「取数指令被调用过」：调用到了但结果没落到控件上，
    /// 在界面上与根本没调用完全一样。
    /// </summary>
    [Fact]
    public void ASelectBoxFillsItsOptionsFromTheDeclaredCommand()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions, channels) = Host();
            var view = new PanelView(TwoLevel(), bus, log, actions, channels);

            var domain = Boxes(view)[0];
            Assert.True(
                UiTestHost.PumpUntil(() => Options(domain).Count > 0),
                "域的候选一直是空的——取数没落到控件上");

            Assert.Equal(["全部", "aurora", "demo"], Options(domain));

            // 取完候选要选中一项并**把它发上通道**：引用方（这里是「类」那一级）
            // 只在通道有值时才知道该取哪一批候选。
            Assert.Equal("全部", domain.SelectedItem as string);
            Assert.Equal("全部", channels.Value(DomainChannel, "value"));
        });
    }

    /// <summary>
    /// 上一级一变，这一级跟着换一批候选——两级联动就是这一条。
    ///
    /// 少了它的话「类」里留着的是上一个域的类名：选出来的结果没有人能解释，
    /// 而下拉本身看上去完全正常。
    /// </summary>
    [Fact]
    public void ChangingTheUpstreamChannelRefetchesTheDownstreamOptions()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions, channels) = Host();
            var view = new PanelView(TwoLevel(), bus, log, actions, channels);

            var boxes = Boxes(view);
            var domain = boxes[0];
            var commandClass = boxes[1];

            Assert.True(
                UiTestHost.PumpUntil(() => Options(domain).Count > 0 && Options(commandClass).Count > 0),
                "两级候选没有都取回来");

            // 域为「全部」时不列类（DEC-021）：跨域列类名会把不同域里同名的类归成一条。
            Assert.Equal(["全部"], Options(commandClass));

            domain.SelectedItem = "demo";
            Assert.True(
                UiTestHost.PumpUntil(() => Options(commandClass).Count > 1),
                "换了域之后，类那一级没有重新取候选");

            Assert.Equal(["全部", "branch", "repo", "ui"], Options(commandClass));

            // 再换一次：候选必须整体换掉，不是往上追加。
            domain.SelectedItem = "aurora";
            Assert.True(
                UiTestHost.PumpUntil(() => Options(commandClass).SequenceEqual(new[] { "全部", "ui" })),
                "换域之后旧候选没被换掉: " + string.Join("/", Options(commandClass)));
        });
    }

    /// <summary>
    /// 换一批候选之后，选中项**还在就留着，不在就退回第一项**。
    ///
    /// 每次都跳回第一项的话，人只是想换个域看看，却要把类再点一遍；
    /// 而留一个已经不在候选里的值，会让下一次取数带上一个这个域根本没有的类。
    /// 两条是同一条规则的两面，必须一起验。
    /// </summary>
    [Fact]
    public void RefetchingKeepsTheChoiceThatSurvivesAndDropsTheOneThatDoesNot()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions, channels) = Host();
            var view = new PanelView(TwoLevel(), bus, log, actions, channels);

            var boxes = Boxes(view);
            var domain = boxes[0];
            var commandClass = boxes[1];

            // 先等第一批候选回来。**不等的话选不中**：候选还没填上时，
            // 给 Selector 的 SelectedItem 赋一个不在集合里的值会被丢掉——
            // 这正是动态候选与静态候选真正的区别，测试自己也得守。
            Assert.True(UiTestHost.PumpUntil(() => Options(domain).Count > 0));

            domain.SelectedItem = "demo";
            Assert.True(UiTestHost.PumpUntil(() => Options(commandClass).Count > 2));

            // repo 只有 demo 有：换到 aurora 之后它必须消失，不能留在框里。
            commandClass.SelectedItem = "repo";
            UiTestHost.Pump();

            domain.SelectedItem = "aurora";
            Assert.True(UiTestHost.PumpUntil(() => Options(commandClass).SequenceEqual(new[] { "全部", "ui" })));
            Assert.Equal("全部", commandClass.SelectedItem as string);

            // ui 两个域都有：换回 demo 之后它必须还在原处。
            commandClass.SelectedItem = "ui";
            UiTestHost.Pump();

            domain.SelectedItem = "demo";
            Assert.True(UiTestHost.PumpUntil(() => Options(commandClass).Count > 2));
            Assert.Equal("ui", commandClass.SelectedItem as string);
        });
    }

    /// <summary>
    /// 取数失败不静默：候选留在原样，日志里必须留下一条说得清是哪条指令的话。
    /// 空下拉与「这一级本来就没有候选」在界面上分不出来，只能靠日志。
    /// </summary>
    [Fact]
    public void AFailingOptionsCommandIsReportedInsteadOfSilentlyEmptying()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions, channels) = Host();
            var view = new PanelView(Parse("""
                {
                  "id": "demo", "title": "演示",
                  "rows": [ { "widgets": [
                    { "kind": "textbox", "id": "pick", "label": "选一个", "mode": "select",
                      "optionsSource": { "command": "demo.ui.nosuchdata" } }
                  ] } ]
                }
                """), bus, log, actions, channels);

            Assert.True(
                UiTestHost.PumpUntil(() => log.Snapshot().Any(entry =>
                    entry.Message.Contains("demo.ui.nosuchdata", StringComparison.Ordinal))),
                "取候选失败了，日志里却一个字都没有");

            Assert.Empty(Options(Boxes(view)[0]));
        });
    }

    // ---------------------------------------------------------------- 装配

    private static PanelDefinition TwoLevel() => Parse("""
        {
          "id": "filter", "title": "筛选",
          "rows": [ { "widgets": [
            { "kind": "textbox", "id": "domain", "label": "域", "mode": "select",
              "channel": "demo.domain",
              "optionsSource": { "command": "demo.ui.data", "args": { "view": "domains" } } },
            { "kind": "textbox", "id": "class", "label": "类", "mode": "select",
              "optionsSource": {
                "command": "demo.ui.data",
                "args": { "view": "classes", "domain": "{selection.demo.domain.value}" }
              } }
          ] } ]
        }
        """);

    private static PanelDefinition Parse(string json)
        => JsonSerializer.Deserialize<PanelDefinition>(json, JsonOptions)!;

    private static List<AuroraOptionBox> Boxes(PanelView view)
    {
        var surface = Assert.IsType<Border>(view.Content);
        var board = Assert.IsType<AuroraPanelBoard>(surface.Child);
        return board.Children.OfType<AuroraOptionBox>().ToList();
    }

    private static List<string> Options(AuroraOptionBox box)
        => box.Items.OfType<string>().ToList();

    private static (CommandBus Bus, MemoryShellLog Log, ActionRegistry Actions, SelectionChannels Channels) Host()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "demo.ui.data",
            Domain = "demo",
            CommandClass = "ui",
            Summary = "候选取数",
            Readonly = true,
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(context =>
            {
                var view = context.GetString("view") ?? "";
                var domain = (context.GetString("domain") ?? "").Trim();
                var values = view switch
                {
                    "domains" => new[] { "全部", "aurora", "demo" },
                    "classes" => domain switch
                    {
                        // ui 两个域都有：候选换掉之后它还在，「选中项留在原处」才验得到。
                        "demo" => ["全部", "branch", "repo", "ui"],
                        "aurora" => ["全部", "ui"],
                        // 域为「全部」或留空时不列类（DEC-021）。
                        _ => new[] { "全部" },
                    },
                    _ => [],
                };

                return CommandResult.Ok(JsonSerializer.Serialize(
                    values.Select(value => new Dictionary<string, string> { ["value"] = value })));
            }),
        });

        var log = new MemoryShellLog();
        var bus = new CommandBus(registry, log);
        return (bus, log, new ActionRegistry(bus, log), new SelectionChannels());
    }
}
