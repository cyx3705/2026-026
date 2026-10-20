using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Controls;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Panels;
using HistoryAurora.Shell.Components.Selection;
using HistoryAurora.Shell.Components.Widgets;
using HistoryAurora.Shell.Neutral.Commands;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Commands;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 选项条（REQ-UI-158）。
///
/// 起因是 Janus 的分支控制：分支要像控制面板一样一字排开、点一下看它的历史，右键对它做事。
/// 表格做得到右键，但它是一整块会滚动的版面；选项框摆得下，但右键已经被「列出全部候选」占了。
/// 于是补一个通用小组件，而不是给 Janus 开特例：取数与选择框同形，菜单项是声明的动作。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class PanelChoicesContractTests
{
    private const string Channel = "demo.branch";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 候选从指令来、text 列是显示文字，第一项默认选中并发上通道——
    /// 引用方（历史表）一打开就有值可取，不必等人先点一下。
    /// </summary>
    [Fact]
    public void ChoicesFillFromTheCommandAndPublishTheFirstOne()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions, channels, _) = Host();
            var view = new PanelView(Definition(), bus, log, actions, channels);
            var strip = Strip(view);

            Assert.True(UiTestHost.PumpUntil(() => strip.Chips.Count == 2), "候选没有落到选项条上");
            Assert.Equal(["main · 当前", "feature"], strip.Chips.Select(chip => chip.Content as string));
            Assert.Equal("main", strip.SelectedValue);
            Assert.True(strip.Chips[0].IsChecked);
            Assert.Equal("main", channels.Value(Channel, "value"));
            Assert.DoesNotContain(log.Snapshot(), entry => entry.Level >= ShellLogLevel.Warn);
        });
    }

    /// <summary>点选一项：选中态换过去、通道跟着变。程序回写（重取候选）不算点选。</summary>
    [Fact]
    public void ClickingAChoiceSelectsAndPublishesIt()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions, channels, _) = Host();
            var view = new PanelView(Definition(), bus, log, actions, channels);
            var strip = Strip(view);
            Assert.True(UiTestHost.PumpUntil(() => strip.Chips.Count == 2));

            strip.Chips[1].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiTestHost.Pump();

            Assert.Equal("feature", strip.SelectedValue);
            Assert.False(strip.Chips[0].IsChecked);
            Assert.True(strip.Chips[1].IsChecked);
            Assert.Equal("feature", channels.Value(Channel, "value"));
        });
    }

    /// <summary>
    /// 右键某一项：菜单按声明列出，{value} 取被右键的那一项，而不是当前选中的那一项。
    /// 动作成功后候选重取——删掉的那一项从条上消失，不必另外刷新。
    /// </summary>
    [Fact]
    public void TheMenuActsOnTheRightClickedChoiceAndRefetchesAfterwards()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions, channels, deleted) = Host();
            var view = new PanelView(Definition(), bus, log, actions, channels);
            var strip = Strip(view);
            Assert.True(UiTestHost.PumpUntil(() => strip.Chips.Count == 2));

            strip.RaiseContextFor("feature");
            var menu = Assert.IsType<ContextMenu>(view.ChoiceMenu);
            try
            {
                var item = Assert.Single(menu.Items.OfType<MenuItem>());
                Assert.Equal("删除分支", item.Header);
                Assert.True(item.IsEnabled);

                item.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                Assert.True(UiTestHost.PumpUntil(() => strip.Chips.Count == 1), "动作成功后候选没有重取");
                Assert.Equal(["feature"], deleted);
                Assert.Equal("main", strip.SelectedValue);
            }
            finally
            {
                // 合成的 Click 不会像真点击那样收起菜单；开着的菜单会抓住鼠标，
                // 同一 UI 线程上后面那些靠右键、弹出层的测试会因此挂死。
                menu.IsOpen = false;
                UiTestHost.Pump();
            }
        });
    }

    /// <summary>菜单只属于选项条；选项条不接受选择框以外的字段（写了就整份作废，不静默忽略）。</summary>
    [Fact]
    public void TheValidatorKeepsMenuOnChoicesOnly()
    {
        Assert.True(PanelDefinitionValidator.Validate(Definition()).Ok);
        Assert.False(PanelDefinitionValidator.Validate(Parse("""
            { "id": "p", "title": "p", "rows": [ { "widgets": [
              { "kind": "button", "action": "demo.x", "menu": [ { "action": "demo.y" } ] } ] } ] }
            """)).Ok);
        Assert.False(PanelDefinitionValidator.Validate(Parse("""
            { "id": "p", "title": "p", "rows": [ { "widgets": [
              { "kind": "choices", "id": "c" } ] } ] }
            """)).Ok);
        Assert.False(PanelDefinitionValidator.Validate(Parse("""
            { "id": "p", "title": "p", "rows": [ { "widgets": [
              { "kind": "choices", "id": "c", "options": [ "a" ], "menu": [ { "action": "" } ] } ] } ] }
            """)).Ok);
    }

    private static PanelDefinition Definition() => Parse("""
        {
          "id": "branches", "title": "分支",
          "rows": [ { "widgets": [
            { "kind": "choices", "id": "branch", "label": "分支", "flex": true, "channel": "demo.branch",
              "optionsSource": { "command": "demo.ui.data", "args": { "view": "branches" } },
              "menu": [ { "action": "demo.branch.delete", "title": "删除分支", "style": "danger" } ] }
          ] } ]
        }
        """);

    private static PanelDefinition Parse(string json)
        => JsonSerializer.Deserialize<PanelDefinition>(json, JsonOptions)!;

    private static AuroraChoiceStrip Strip(PanelView view)
    {
        var surface = Assert.IsType<System.Windows.Controls.Border>(view.Content);
        var board = Assert.IsType<AuroraPanelBoard>(surface.Child);
        return Assert.Single(board.Children.OfType<AuroraChoiceStrip>());
    }

    private static (ShellBus Bus, MemoryShellLog Log, ActionRegistry Actions, SelectionChannels Channels, List<string> Deleted) Host()
    {
        var branches = new List<string> { "main", "feature" };
        var deleted = new List<string>();
        var registry = new CommandTable();
        registry.Register(new CommandDescriptor
        {
            Name = "demo.ui.data",
            Domain = "demo",
            CommandClass = "ui",
            Summary = "候选取数",
            Readonly = true,
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(JsonSerializer.Serialize(
                branches.Select(name => new Dictionary<string, string>
                {
                    ["value"] = name,
                    ["text"] = name == "main" ? "main · 当前" : "",
                })))),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "demo.branch.delete",
            Domain = "demo",
            CommandClass = "branch",
            Summary = "删除分支",
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(context =>
            {
                var name = context.GetString("branch") ?? "";
                deleted.Add(name);
                branches.Remove(name);
                return CommandResult.Ok("已删除 " + name);
            }),
        });

        var log = new MemoryShellLog();
        var bus = TestShell.Bus(registry);
        var actions = new ActionRegistry(bus, log);
        actions.DeclareLocal("demo",
        [
            new ActionDeclaration
            {
                Id = "demo.branch.delete",
                Title = "删除",
                Command = "demo.branch.delete",
                Args = new Dictionary<string, string> { ["branch"] = "{value}" },
            },
        ]);
        return (bus, log, actions, new SelectionChannels(), deleted);
    }
}
