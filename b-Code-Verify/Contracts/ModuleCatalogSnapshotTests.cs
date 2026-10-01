using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using System.Text.Json;
using System.Xml.Linq;
using HistoryAurora.Shell.HostedPages.Views;
using Xunit;
using HistoryAurora.Shell.Neutral.CommandSurface;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryAurora.Shell.Neutral.Commands;

namespace HistoryAurora.Verify.Contracts;

public sealed class ModuleCatalogSnapshotTests
{
    [Fact]
    public void SnapshotFiltersBySourceInsteadOfCommandPrefix()
    {
        var modules = new[] { Module("Math", 2) };
        var commands = new[]
        {
            Command("calc.add", "Math"),
            Command("calc.subtract", "Math"),
            new ModuleCommandInfo("Math.localOnly", "wrong source", "", "app", null),
        };

        Assert.True(ModuleCatalogSnapshot.TryCreate(
            modules, commands, out var snapshot, out var error), error);
        Assert.Equal(
            ["calc.add", "calc.subtract"],
            snapshot!.CommandsFor("math").Select(command => command.Name));
    }

    [Fact]
    public void SnapshotRejectsCrossGenerationCounts()
    {
        var modules = new[] { Module("Math", 2) };

        Assert.False(ModuleCatalogSnapshot.TryCreate(
            modules, [Command("calc.add", "Math")], out var snapshot, out var error));
        Assert.Null(snapshot);
        Assert.Contains("刷新期间发生变化", error);
    }

    // 1.29.0：模块清单经总线读 vulcan.module.list；指令表就是总线的目录（进程内即宿主目录），不再另执行 vulcan.command.list。
    [Fact]
    public async Task ReaderUsesTheModuleListAndTheBusCatalogTogether()
    {
        var table = new CommandTable();
        table.Register(Probe("calc.add"), "module:Math");
        var bus = TestShell.Bus(table, (text, _) => Task.FromResult(text == "vulcan.module.list"
            ? CommandResult.Ok("modules", new List<ModuleMeta> { Module("Math", 1) })
            : CommandResult.Fail("未知指令: " + text)));

        var result = await ModuleCatalogReader.LoadAsync(bus);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["vulcan.module.list"], TestShell.Calls(bus).Calls.Select(call => call.Text));
        Assert.Equal("calc.add", Assert.Single(result.Snapshot!.CommandsFor("Math")).Name);
    }

    [Fact]
    public async Task ReaderDeserializesTheJsonModuleListFromTheHost()
    {
        var modules = new List<ModuleMeta> { Module("Math", 1) };
        var table = new CommandTable();
        table.Register(Probe("calc.add"), "module:Math");
        var bus = TestShell.Bus(table, (text, _) => Task.FromResult(text == "vulcan.module.list"
            ? CommandResult.Ok("modules", JsonSerializer.SerializeToElement(modules, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            : CommandResult.Fail("未知指令: " + text)));

        var result = await ModuleCatalogReader.LoadAsync(bus);

        Assert.True(result.Success, result.Message);
        Assert.Equal("calc.add", Assert.Single(result.Snapshot!.CommandsFor("Math")).Name);
    }

    [Fact]
    public async Task ModulesViewReaderDoesNotRequireCommandCatalogConsistency()
    {
        var modules = new List<ModuleMeta> { Module("HistoryJanus", 31) };
        var bus = TestShell.Bus(new CommandTable(), (text, _) => Task.FromResult(text == "vulcan.module.list"
            ? CommandResult.Ok("modules", JsonSerializer.SerializeToElement(modules))
            : CommandResult.Fail("command catalog is intentionally unavailable")));

        var result = await ModuleCatalogReader.LoadModulesAsync(bus);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["vulcan.module.list"], TestShell.Calls(bus).Calls.Select(call => call.Text));
        Assert.Equal("HistoryJanus", Assert.Single(result.Snapshot!.Modules).ModuleName);
        Assert.Empty(result.Snapshot.Commands);
    }

    private static CommandDescriptor Probe(string name) => new()
    {
        Name = name,
        Summary = name,
        RequiresUiThread = true,
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
    };

    private static ModuleMeta Module(string name, int commandCount)
        => new(name, "", "", "1.0.0", false, name + ".dll", commandCount);

    private static ModuleCommandInfo Command(string name, string module)
        => new(name, name, "", "module", module);

    private sealed class TestLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }

        public event EventHandler<ShellLogEntry>? EntryAdded
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
