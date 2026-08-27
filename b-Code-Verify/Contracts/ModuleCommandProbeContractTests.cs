using HistoryAurora.Shell.Logging;
using HistoryAurora.Shell.Modules;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Commands;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 权威目录查询必须合并（REQ-UI-048）。动作拉取与页面拉取在同一轮发现里
/// 各问一次 <c>vulcan.command.list</c>，不缓存的话一次发现就是两遍全文。
/// </summary>
public sealed class ModuleCommandProbeContractTests
{
    [Fact]
    public async Task OwnersWithSuffix_SharesOneRemoteCatalogWithinTheCacheWindow()
    {
        ModuleCommandProbe.ResetRemoteListCache();
        var registry = new CommandRegistry();
        var log = new MemoryShellLog();
        var bus = new CommandBus(registry, log);
        var lists = 0;
        bus.RemoteExecutor = (_, _, _) =>
        {
            Interlocked.Increment(ref lists);
            IReadOnlyList<CommandCatalogRow> rows =
            [
                Row("janus.ui.actions"),
                Row("janus.ui.describe"),
            ];
            return Task.FromResult(CommandResult.Ok("ok", rows));
        };

        var actions = await ModuleCommandProbe.OwnersWithSuffixAsync(
            bus, log, "test", ".ui.actions", CancellationToken.None);
        var pages = await ModuleCommandProbe.OwnersWithSuffixAsync(
            bus, log, "test", ".ui.describe", CancellationToken.None);

        Assert.Equal(["janus"], actions);
        Assert.Equal(["janus"], pages);
        Assert.Equal(1, lists);
    }

    private static CommandCatalogRow Row(string name) => new(
        name,
        "janus",
        "summary",
        null,
        0,
        "module:HistoryJanus",
        null,
        false,
        false,
        true,
        "hidden")
    {
        CommandClass = "ui",
    };
}
