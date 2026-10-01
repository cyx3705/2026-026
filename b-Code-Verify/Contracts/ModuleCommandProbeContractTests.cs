using HistoryAurora.Shell.Neutral.Logging;
using HistoryAurora.Shell.Components.Modules;
using HistoryVulcan.Core.Commands;
using Xunit;
using HistoryAurora.Shell.Neutral.CommandSurface;
using HistoryAurora.Shell.Neutral.Commands;

namespace HistoryAurora.Verify;

/// <summary>
/// 「哪些模块声明了某类契约」按总线目录判定（1.29.0：目录即宿主目录，不再另读一次远端补齐）。
/// </summary>
public sealed class ModuleCommandProbeContractTests
{
    [Fact]
    public async Task OwnersWithSuffix_ReadsTheBusCatalogAndSkipsAuroraItself()
    {
        var registry = new CommandTable();
        foreach (var name in new[] { "janus.ui.actions", "janus.ui.describe", "aurora.ui.actions", "minerva.ui.describe" })
            registry.Register(Probe(name));
        var bus = TestShell.Bus(registry);
        var log = new MemoryShellLog();

        var actions = await ModuleCommandProbe.OwnersWithSuffixAsync(
            bus, log, "test", ".ui.actions", CancellationToken.None);
        var pages = await ModuleCommandProbe.OwnersWithSuffixAsync(
            bus, log, "test", ".ui.describe", CancellationToken.None);

        Assert.Equal(["janus"], actions);
        Assert.Equal(["janus", "minerva"], pages);
        Assert.Empty(TestShell.Calls(bus).Calls);
    }

    private static CommandDescriptor Probe(string name) => new()
    {
        Name = name,
        Summary = name,
        RequiresUiThread = true,
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
    };
}
