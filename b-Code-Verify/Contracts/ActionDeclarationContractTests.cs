using System.Collections.Generic;
using HistoryAurora.Shell.Actions;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 动作声明协议的契约（REQ-UI-009）。这套机制存在的理由只有一条：
/// **按钮不能记住指令名**。模块改一次指令名，写死名字的按钮就静默变哑——
/// 点了没反应、没有报错、也没有任何地方记下这件事。
///
/// 因此三件事必须成立：
/// <list type="number">
///   <item>声明冒名、版本不对、id 重复一律整份作废，不做部分接受；</item>
///   <item>解析不到动作时返回**原因**，调用方据此显示，而不是静默禁用；</item>
///   <item>声明了却指向不存在的指令要进"断链"账，在按钮被点之前就可查。</item>
/// </list>
/// </summary>
public sealed class ActionDeclarationContractTests
{
    private const string MinimalActions = """
        {
          "schemaVersion": 1,
          "owner": "HistoryDemo",
          "actions": [
            {
              "id": "demo.rename",
              "title": "重命名",
              "command": "demo.branch.rename",
              "args": { "to": "{target}" }
            }
          ]
        }
        """;

    [Fact]
    public void Parse_AcceptsMinimalDeclaration()
    {
        var parsed = ActionDeclarationReader.Read(MinimalActions, "HistoryDemo");

        Assert.True(parsed.Ok, parsed.Error);
        var action = Assert.Single(parsed.Value!.Actions);
        Assert.Equal("demo.rename", action.Id);
        Assert.Equal("demo.branch.rename", action.Command);
        // owner 由 Aurora 填入，不由模块在每条动作上自报。
        Assert.Equal("HistoryDemo", action.Owner);
    }

    [Fact]
    public void Parse_RejectsUnsupportedSchemaVersion()
    {
        var parsed = ActionDeclarationReader.Read(
            MinimalActions.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 9"),
            "HistoryDemo");

        Assert.False(parsed.Ok);
        Assert.Contains("schemaVersion=9", parsed.Error);
    }

    [Fact]
    public void Parse_RejectsOwnerMismatch()
    {
        // 否则一个模块可以借声明抢注别人的动作，而按钮那一侧完全看不出换了主人。
        var parsed = ActionDeclarationReader.Read(MinimalActions, "HistoryOther");

        Assert.False(parsed.Ok);
        Assert.Contains("HistoryOther", parsed.Error);
    }

    [Fact]
    public void Parse_RejectsActionWithoutCommand()
    {
        var parsed = ActionDeclarationReader.Read("""
            {
              "schemaVersion": 1,
              "owner": "HistoryDemo",
              "actions": [ { "id": "demo.noop", "title": "空" } ]
            }
            """, "HistoryDemo");

        Assert.False(parsed.Ok);
        Assert.Contains("demo.noop", parsed.Error);
    }

    [Fact]
    public void Parse_RejectsDuplicateIds()
    {
        var parsed = ActionDeclarationReader.Read("""
            {
              "schemaVersion": 1,
              "owner": "HistoryDemo",
              "actions": [
                { "id": "demo.a", "command": "demo.x" },
                { "id": "demo.a", "command": "demo.y" }
              ]
            }
            """, "HistoryDemo");

        Assert.False(parsed.Ok);
        Assert.Contains("demo.a", parsed.Error);
    }

    [Fact]
    public async Task Reload_CollectsDeclarationsAndFlagsBrokenCommands()
    {
        var registry = new CommandRegistry();
        Declare(registry, "demo", """
            {
              "schemaVersion": 1,
              "owner": "HistoryDemo",
              "actions": [
                { "id": "demo.ok", "command": "demo.exists" },
                { "id": "demo.gone", "command": "demo.renamed.away" }
              ]
            }
            """);
        registry.Register(Simple("demo.exists"));

        var log = new MemoryLog();
        var actions = new ActionRegistry(new CommandBus(registry, log), log);
        var report = await actions.ReloadAsync();

        Assert.Equal(1, report.ModulesAsked);
        Assert.Equal(2, report.ActionsDeclared);
        // 断链在按钮被点之前就已经是一条有名有姓的事实。
        Assert.Contains(report.Broken, item => item.Contains("demo.gone"));
        Assert.DoesNotContain(report.Broken, item => item.Contains("demo.ok"));
    }

    [Fact]
    public async Task Reload_SkipsBadDeclarationWithoutAffectingOthers()
    {
        var registry = new CommandRegistry();
        Declare(registry, "broken", "{ not json");
        Declare(registry, "demo", MinimalActions);

        var log = new MemoryLog();
        var actions = new ActionRegistry(new CommandBus(registry, log), log);
        var report = await actions.ReloadAsync();

        Assert.Equal(2, report.ModulesAsked);
        Assert.Equal("broken", Assert.Single(report.Skipped));
        Assert.Equal("demo.rename", Assert.Single(actions.Actions).Id);
    }

    [Fact]
    public void Resolve_ReturnsReasonWhenUndeclared()
    {
        var log = new MemoryLog();
        var actions = new ActionRegistry(new CommandBus(new CommandRegistry(), log), log);

        var binding = actions.Resolve("nobody.declared.this");

        Assert.False(binding.Ok);
        // 「点了没反应」的替代品必须是一句能显示出来的话，不是静默禁用。
        Assert.Contains("nobody.declared.this", binding.Error);
    }

    [Fact]
    public void BuildCommandText_SubstitutesPlaceholdersAndQuotes()
    {
        var parsed = ActionDeclarationReader.Read(MinimalActions, "HistoryDemo");
        var action = parsed.Value!.Actions[0];

        var text = ActionRegistry.BuildCommandText(
            action,
            control => control == "target" ? "ai/新 分支" : null,
            out var error);

        Assert.Equal("", error);
        Assert.Equal("demo.branch.rename to=\"ai/新 分支\"", text);
    }

    /// <summary>
    /// 控件 id 带连字符时占位符照样要被认出来。
    ///
    /// 原先的正则是 <c>\w+</c>，而 <c>\w</c> 不含连字符——<c>{project-name}</c>
    /// 于是根本不被视为占位符：既不替换，也不算「引用了不存在的控件」，
    /// 就这样原样上了总线，成为一条参数明显错误却「执行成功」的指令。
    /// 带连字符的控件 id 是常态（commit-message / page-option），因此这条必须钉住。
    /// </summary>
    [Fact]
    public void BuildCommandText_AcceptsHyphenatedAndDottedPlaceholderNames()
    {
        var action = new ActionDeclaration
        {
            Id = "demo.rename",
            Command = "demo.proj.rename",
            Args = new Dictionary<string, string>
            {
                ["name"] = "{selection.demo.project.name}",
                ["new"] = "{project-name}",
            },
        };

        var text = ActionRegistry.BuildCommandText(
            action,
            control => control switch
            {
                "selection.demo.project.name" => "旧名字",
                "project-name" => "新名字",
                _ => null,
            },
            out var error);

        Assert.Equal("", error);
        Assert.Equal("demo.proj.rename name=旧名字 new=新名字", text);
    }

    [Fact]
    public void BuildCommandText_RefusesUnknownPlaceholder()
    {
        var parsed = ActionDeclarationReader.Read(MinimalActions, "HistoryDemo");

        var text = ActionRegistry.BuildCommandText(
            parsed.Value!.Actions[0],
            _ => null,
            out var error);

        // 把 {target} 原样发上总线会变成一条参数明显错误、却"执行成功"的指令。
        Assert.Null(text);
        Assert.Contains("target", error);
    }

    [Fact]
    public void ExpandPlaceholders_SubstitutesSelectionChannel()
    {
        var unknown = new List<string>();
        var content = "SolidWorks .SLDASM → 属性整备（改名）";
        var expanded = ActionRegistry.ExpandPlaceholders(
            "minerva.ui.picksource content={selection.minerva.content.value}",
            key => key == "selection.minerva.content.value" ? content : null,
            unknown,
            quoteValues: true);

        Assert.Empty(unknown);
        Assert.Equal(
            "minerva.ui.picksource content=" + CommandParser.QuoteArg(content),
            expanded);
    }

    [Fact]
    public async Task Reload_DoesNotAskAuroraItself()
    {
        // Aurora 自己注册了一条 aurora.ui.actions——那是**查询**动作台账的命令，
        // 与协议槽位同名。不排除的话每一轮探测都会去调它自己那条查询命令，
        // 再拿一段人话去做 JSON 解析并失败；模块热重载的窗口期里更会留下一条
        // `✗ 未知指令: aurora.ui.actions`（2026-08-25 真机实测）。
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = HistoryAurora.Shell.Modules.ModuleCommandProbe.SelfDomain
                   + ActionRegistry.ActionsSuffix,
            Domain = HistoryAurora.Shell.Modules.ModuleCommandProbe.SelfDomain,
            CommandClass = "ui",
            Summary = "列出模块声明的动作",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("尚无模块声明动作")),
        });
        Declare(registry, "demo", MinimalActions);

        var log = new MemoryLog();
        var actions = new ActionRegistry(new CommandBus(registry, log), log);
        var report = await actions.ReloadAsync();

        Assert.Equal(1, report.ModulesAsked);
        Assert.Empty(report.Skipped);
        Assert.Equal("demo.rename", Assert.Single(actions.Actions).Id);
    }

    private static void Declare(CommandRegistry registry, string domain, string payload)
        => registry.Register(new CommandDescriptor
        {
            Name = domain + ActionRegistry.ActionsSuffix,
            Domain = domain,
            CommandClass = "ui",
            Summary = "动作声明",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(payload)),
        });

    private static CommandDescriptor Simple(string name) => new()
    {
        Name = name,
        Domain = name.Split('.')[0],
        CommandClass = "core",
        Summary = "测试指令",
        Readonly = true,
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
    };

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
