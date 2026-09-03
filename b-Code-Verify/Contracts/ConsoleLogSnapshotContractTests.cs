using System.Text.Json;
using HistoryAurora.Shell.Composition;
using HistoryAurora.Shell.Neutral.Logging;
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
    public void BufferAndSnapshotRedactCredentialsWithoutDamagingDiagnostics()
    {
        var log = new MemoryShellLog();
        log.Log(
            ShellLogLevel.Error,
            "shell.chrome",
            "请求失败 password=plain-secret Authorization: Bearer bearer-secret\n"
            + "System.InvalidOperationException: useful failure\n"
            + "   at HistoryAurora.Shell.ConsoleView.Run() in ConsoleView.cs:line 42");
        log.Log(ShellLogLevel.Info, "result", "operator@example.com----delimited-secret");
        log.Log(ShellLogLevel.Info, "result", "operator-name|||another-secret");
        log.Log(ShellLogLevel.Info, "result", "endpoint=https://operator:uri-secret@example.test/api");
        log.Log(ShellLogLevel.Info, "result", "api_key=sk-1234567890abcdefghijklmnop");
        log.Log(ShellLogLevel.Info, "result", "{\"token\":\"quoted-secret\",\"count\":2}");

        var buffered = log.Snapshot();
        var snapshot = log.ReadSnapshot(new ConsoleLogQuery(
            ShellLogLevel.Trace, null, null, null, null, 100));
        var exported = string.Join("\n", snapshot.Entries.Select(item => item.Message));

        Assert.All(buffered, entry => Assert.DoesNotContain("secret", entry.Message, StringComparison.Ordinal));
        Assert.DoesNotContain("operator@example.com", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("operator-name", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-1234567890abcdefghijklmnop", exported, StringComparison.Ordinal);
        Assert.Contains("password=[REDACTED]", exported, StringComparison.Ordinal);
        Assert.Contains("Authorization: [REDACTED]", exported, StringComparison.Ordinal);
        Assert.Contains("https://[REDACTED]:[REDACTED]@example.test/api", exported, StringComparison.Ordinal);
        Assert.Contains("\"token\":\"[REDACTED]\"", exported, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: useful failure", exported, StringComparison.Ordinal);
        Assert.Contains("at HistoryAurora.Shell.ConsoleView.Run() in ConsoleView.cs:line 42", exported, StringComparison.Ordinal);
        Assert.Contains("\nSystem.InvalidOperationException", snapshot.Entries.Single(item => item.Level == "Error").Message);
    }

    [Fact]
    public void SanitizerLeavesOrdinaryErrorTextAndSeparatorsIntact()
    {
        const string message =
            "Build failed: timeout after 30 seconds\n"
            + "--- End of inner exception stack trace ---\n"
            + "source=module-loader accountCount=4";

        Assert.Equal(message, ConsoleLogSanitizer.Redact(message));
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
