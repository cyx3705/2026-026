using System.IO;
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

    /// <summary>
    /// DEC-042：控制台显示的是宿主那一份日志。补读的旧记录、界面写的、宿主总线写的各出现一次；
    /// 界面写的转进宿主且已脱敏；解除订阅后不再收到。
    /// </summary>
    [Fact]
    public void HostBackedLogWritesThroughAndShowsEveryHostEntryOnce()
    {
        var host = new FakeHostLog();
        host.Log(ShellLogLevel.Info, "cmd:UI", "minerva.conversion.run");
        using var log = new MemoryShellLog(host);
        var raised = new List<ShellLogEntry>();
        log.EntryAdded += (_, entry) => raised.Add(entry);

        log.Log(ShellLogLevel.Info, "aurora", "token=plain-secret window line");
        host.Log(ShellLogLevel.Info, "cmd:progress:apollo:chat", "#1 第 1 轮 要求搜索：「CDQ2B20-10D」");

        Assert.Equal(3, host.Entries.Count);
        Assert.DoesNotContain("plain-secret", host.Entries[1].Message, StringComparison.Ordinal);

        var shown = log.Snapshot();
        Assert.Equal(["cmd:UI", "aurora", "cmd:progress:apollo:chat"], shown.Select(entry => entry.Category));
        Assert.Contains("token=[REDACTED]", shown[1].Message, StringComparison.Ordinal);
        Assert.Equal(2, raised.Count);
        Assert.Equal(
            [1L, 2L, 3L],
            log.ReadSnapshot(new ConsoleLogQuery(ShellLogLevel.Trace, null, null, null, 0, 100))
                .Entries.Select(item => item.Sequence));

        log.Dispose();
        host.Log(ShellLogLevel.Info, "cmd:UI", "after dispose");
        Assert.Equal(3, log.Snapshot().Count);
    }

    /// <summary>DEC-042：进程内界面的控制台必须挂宿主日志，不得再自建独立日志。</summary>
    [Fact]
    public void InProcessConsoleShowsTheHostLogInsteadOfItsOwn()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "b-Code-Studio", "Module", "AuroraShellHost.cs"));

        Assert.Contains("new MemoryShellLog(context.Log)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new MemoryShellLog()", source, StringComparison.Ordinal);
        Assert.Contains("consoleLog?.Dispose()", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "project.manifest.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录");
    }

    /// <summary>行为与宿主 ShellLog 一致：先入缓冲，再在锁外派发事件。</summary>
    private sealed class FakeHostLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = [];

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public void Log(ShellLogLevel level, string category, string message)
        {
            var entry = new ShellLogEntry(DateTime.Now, level, category, message);
            lock (_entries)
                _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }

        public IReadOnlyList<ShellLogEntry> Snapshot() => Entries;
    }
}
