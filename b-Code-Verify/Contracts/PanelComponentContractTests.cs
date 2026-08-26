using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using HistoryAurora.Shell.Actions;
using HistoryAurora.Shell.Panels;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 面板组件的契约（REQ-UI-008）。V2 相对 V1 只改了两件事，但两件都是为了同一个失败形态：
/// <list type="number">
///   <item>小组件收成三种（文字 / 文本框 / 按钮），其余一律拒绝，不静默忽略；</item>
///   <item>按钮**只认动作 id**。V1 的按钮直接写指令模板，模块改一次指令名按钮就哑了；
///         现在指令名归模块自己的声明管，面板 JSON 一个字不动。</item>
/// </list>
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class PanelComponentContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Validate_RejectsButtonWithoutAction()
    {
        var parsed = PanelDefinitionValidator.Validate(Parse("""
            {
              "id": "demo", "title": "演示",
              "widgets": [ { "kind": "button", "text": "执行" } ]
            }
            """));

        Assert.False(parsed.Ok);
        // 报错要说清"为什么不能写指令名"，否则下一个作者只会把 action 填成指令名。
        Assert.Contains("action", parsed.Error);
    }

    [Fact]
    public void Validate_RejectsUnknownWidgetKind()
    {
        var parsed = PanelDefinitionValidator.Validate(Parse("""
            {
              "id": "demo", "title": "演示",
              "widgets": [ { "kind": "slider", "id": "speed" } ]
            }
            """));

        Assert.False(parsed.Ok);
        Assert.Contains("slider", parsed.Error);
    }

    [Fact]
    public void Validate_RejectsSelectWithoutOptions()
    {
        var parsed = PanelDefinitionValidator.Validate(Parse("""
            {
              "id": "demo", "title": "演示",
              "widgets": [ { "kind": "textbox", "id": "pick", "mode": "select" } ]
            }
            """));

        Assert.False(parsed.Ok);
        Assert.Contains("options", parsed.Error);
    }

    [Fact]
    public void Validate_RejectsDuplicateControlIds()
    {
        // 占位符按 id 取值，重名意味着按钮拿到的参数取决于构建顺序。
        var parsed = PanelDefinitionValidator.Validate(Parse("""
            {
              "id": "demo", "title": "演示",
              "widgets": [
                { "kind": "textbox", "id": "name" },
                { "kind": "textbox", "id": "name" }
              ]
            }
            """));

        Assert.False(parsed.Ok);
        Assert.Contains("name", parsed.Error);
    }

    [Fact]
    public void Button_RendersWarningBoxWhenActionUndeclared()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: false);
            var view = new PanelView(Valid(), bus, log, actions);

            var widgets = Widgets(view);
            // 按钮不渲染成按钮：看起来能点、其实不能，比点了没反应还难查。
            Assert.DoesNotContain(widgets, element => element is Button);
            Assert.Contains(widgets, element => element is Border);
            Assert.Contains(log.Snapshot(), entry =>
                entry.Level == ShellLogLevel.Error && entry.Message.Contains("demo.publish"));
        });
    }

    [Fact]
    public void Button_FiresDeclaredActionWithControlValues()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();

            var executed = new List<string>();
            bus.Executed += (text, _, _) => executed.Add(text);

            var view = new PanelView(Valid(), bus, log, actions);
            Assert.True(view.TrySetValue("note", "第一版"));

            var button = Assert.IsType<Button>(
                Assert.Single(Widgets(view), element => element is Button));
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiTestHost.PumpUntil(() => executed.Count > 0);

            // 面板里写的是动作 id；总线上落下的是声明里的指令名。
            Assert.Equal("demo.release.publish note=第一版 channel=stable", Assert.Single(executed));
        });
    }

    [Fact]
    public void Button_RefusesWhenRequiredTextBoxEmpty()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();

            var executed = new List<string>();
            bus.Executed += (text, _, _) => executed.Add(text);

            var view = new PanelView(Valid(), bus, log, actions);
            var button = Assert.IsType<Button>(
                Assert.Single(Widgets(view), element => element is Button));
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            UiTestHost.Pump();

            Assert.Empty(executed);
            Assert.Contains(log.Snapshot(), entry =>
                entry.Level == ShellLogLevel.Error && entry.Message.Contains("必填"));
        });
    }

    [Fact]
    public void PanelUsesOneSurfaceAndASeparatorBetweenEachWidget()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();
            var view = new PanelView(Valid(), bus, log, actions);

            var surface = Assert.IsType<System.Windows.Controls.Border>(view.Content);
            var scroll = Assert.IsType<System.Windows.Controls.ScrollViewer>(surface.Child);
            var grid = Assert.IsType<System.Windows.Controls.Grid>(scroll.Content);

            Assert.Equal(3, grid.Children.OfType<System.Windows.Controls.Border>().Count());
            Assert.All(
                grid.Children.OfType<System.Windows.Controls.Border>(),
                divider =>
                {
                    Assert.Equal(1, divider.Height);
                    Assert.NotNull(divider.OpacityMask);
                });
        });
    }

    [Fact]
    public void HorizontalPanelUsesVerticalFadeSeparatorsBetweenSiblingWidgets()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();
            var definition = Valid();
            definition.Orientation = "horizontal";

            var view = new PanelView(definition, bus, log, actions);
            var surface = Assert.IsType<Border>(view.Content);
            var scroll = Assert.IsType<ScrollViewer>(surface.Child);
            var stack = Assert.IsType<StackPanel>(scroll.Content);

            Assert.Equal(definition.Widgets.Count - 1, stack.Children.OfType<Border>().Count());
            Assert.All(stack.Children.OfType<Border>(), divider =>
            {
                Assert.Equal(1, divider.Width);
                Assert.NotNull(divider.OpacityMask);
            });
        });
    }

    /// <summary>
    /// 页面描述里的 orientation 必须落到面板上。
    ///
    /// 1.8.9 的实测故障：<c>BuildPanel</c> 装 PanelDefinition 时漏抄了这一项。
    /// 症状不是报错——面板照样画出来，只是横排变竖排，
    /// 控件之间那条竖向渐隐分隔线一并消失，看上去像"分隔线没做"。
    /// </summary>
    [Fact]
    public void PageLevelPanelKeepsTheDeclaredOrientationAndItsFadeSeparators()
    {
        UiTestHost.RunSta(() =>
        {
            var (bus, log, actions) = Host(declare: true);
            actions.ReloadAsync().GetAwaiter().GetResult();

            var rendered = HistoryAurora.Shell.Pages.PageRenderer.Render(
                PagePanel("horizontal"),
                new HistoryAurora.Shell.Pages.PageRenderContext
                {
                    Bus = bus,
                    Log = log,
                    Owner = "HistoryDemo",
                    Actions = actions,
                });

            var view = Assert.IsType<PanelView>(rendered.Root);
            var surface = Assert.IsType<Border>(view.Content);
            var scroll = Assert.IsType<ScrollViewer>(surface.Child);
            var stack = Assert.IsType<StackPanel>(scroll.Content);

            Assert.Equal(Orientation.Horizontal, stack.Orientation);
            var dividers = stack.Children.OfType<Border>().ToList();
            Assert.Equal(2, dividers.Count);
            Assert.All(dividers, divider =>
            {
                Assert.Equal(1, divider.Width);
                Assert.NotNull(divider.OpacityMask);
                // 高度为 0 的线等于没有线，必须有兜底下限。
                Assert.True(divider.MinHeight > 0, "竖向分隔线没有高度下限");
            });
        });
    }

    private static HistoryAurora.Shell.Pages.PageDescription PagePanel(string orientation)
    {
        var parsed = HistoryAurora.Shell.Pages.PageDescriptionReader.Read($$"""
            {
              "schemaVersion": 1,
              "owner": "HistoryDemo",
              "pages": [ {
                "id": "demo", "title": "演示",
                "content": {
                  "type": "panel",
                  "id": "demo-panel",
                  "orientation": "{{orientation}}",
                  "widgets": [
                    { "kind": "text", "text": "一" },
                    { "kind": "textbox", "id": "note", "label": "说明" },
                    { "kind": "text", "text": "三" }
                  ]
                }
              } ]
            }
            """, "HistoryDemo");
        Assert.True(parsed.Ok, parsed.Error);
        return parsed.Value!.Pages[0];
    }

    // ---------------------------------------------------------------- 装配

    private static PanelDefinition Valid() => Parse("""
        {
          "id": "release", "title": "发布",
          "widgets": [
            { "kind": "text", "text": "填写说明后发布" },
            { "kind": "textbox", "id": "note", "label": "说明", "required": true },
            { "kind": "textbox", "id": "channel", "label": "通道", "mode": "select",
              "options": [ "stable", "beta" ] },
            { "kind": "button", "action": "demo.publish", "text": "发布" }
          ]
        }
        """);

    private static PanelDefinition Parse(string json)
        => JsonSerializer.Deserialize<PanelDefinition>(json, JsonOptions)!;

    private static (CommandBus Bus, MemoryLog Log, ActionRegistry Actions) Host(bool declare)
    {
        var registry = new CommandRegistry();
        var log = new MemoryLog();

        registry.Register(new CommandDescriptor
        {
            Name = "demo.release.publish",
            Domain = "demo",
            CommandClass = "core",
            Summary = "测试发布",
            Readonly = true,
            AllowUnspecifiedParameters = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
        });

        if (declare)
        {
            registry.Register(new CommandDescriptor
            {
                Name = "demo" + ActionRegistry.ActionsSuffix,
                Domain = "demo",
                CommandClass = "ui",
                Summary = "动作声明",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("""
                    {
                      "schemaVersion": 1,
                      "owner": "HistoryDemo",
                      "actions": [
                        {
                          "id": "demo.publish",
                          "title": "发布",
                          "command": "demo.release.publish",
                          "args": { "note": "{note}", "channel": "{channel}" }
                        }
                      ]
                    }
                    """)),
            });
        }

        var bus = new CommandBus(registry, log);
        return (bus, log, new ActionRegistry(bus, log));
    }

    /// <summary>取面板栅格里的直接子元素（标签 + 控件）。</summary>
    private static List<FrameworkElement> Widgets(PanelView view)
    {
        var surface = Assert.IsType<Border>(view.Content);
        var scroll = Assert.IsType<ScrollViewer>(surface.Child);
        var grid = Assert.IsType<Grid>(scroll.Content);
        return grid.Children.OfType<FrameworkElement>().ToList();
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
