using System.Text.Json;
using HistoryAurora.Shell;
using HistoryAurora.Shell.Logging;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify.Contracts;

public sealed class ConsoleLogSnapshotContractTests
{
    [Fact]
    public void SnapshotFiltersOrdersAndKeepsMultilineEntriesWhole()
    {
        var log = new MemoryShellLog();
        log.Log(ShellLogLevel.Info, "shell.chrome", "info marker");
        log.Log(ShellLogLevel.Warn, "layout", "warn marker");
        log.Log(ShellLogLevel.Error, "shell.chrome", "error marker\nstack line");
        log.Log(ShellLogLevel.Fatal, "shell.chrome", "fatal marker");

        var recent = log.ReadSnapshot(new ConsoleLogQuery(
            ShellLogLevel.Error, "SHELL.CHROME", "marker", null, null, 100));

        Assert.Equal([4L, 3L], recent.Entries.Select(item => item.Sequence));
        Assert.Contains("\nstack line", recent.Entries[1].Message);
        Assert.Equal(recent.NewestSequence, recent.NextAfter);

        var incremental = log.ReadSnapshot(new ConsoleLogQuery(
            ShellLogLevel.Trace, null, null, null, 2, 100));
        Assert.Equal([3L, 4L], incremental.Entries.Select(item => item.Sequence));
        Assert.Equal(4, incremental.NextAfter);
    }

    [Fact]
    public void SnapshotHonorsTheByteLimitAtEntryBoundaries()
    {
        var log = new MemoryShellLog();
        for (var index = 0; index < 10; index++)
            log.Log(ShellLogLevel.Error, "oversized", new string((char)('a' + index), 80_000));

        var snapshot = log.ReadSnapshot(new ConsoleLogQuery(
            ShellLogLevel.Error, null, null, null, null, 500));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.True(bytes.Length <= MemoryShellLog.MaximumSnapshotBytes);
        Assert.True(snapshot.Truncated);
        Assert.InRange(snapshot.ReturnedCount, 1, 9);
        Assert.All(snapshot.Entries, entry => Assert.Equal(80_000, entry.Message.Length));
    }

    [Fact]
    public void ProviderCommandIsHiddenReadonly()
    {
        var descriptor = FrontendCommandCatalog.FrameworkSourceDescriptors
            .Single(item => item.Name == "aurora.log.snapshot");

        Assert.True(descriptor.Readonly);
        Assert.NotNull(descriptor.HiddenReason);
        Assert.False(descriptor.RequiresUiThread);
    }

    [Fact]
    public void NewLogInstanceGetsANewIdentity()
    {
        Assert.NotEqual(new MemoryShellLog().InstanceId, new MemoryShellLog().InstanceId);
    }
}
