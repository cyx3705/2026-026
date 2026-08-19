using System.IO;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryAurora.Shell;
using Xunit;

namespace HistoryAurora.Verify.Contracts;

/// <summary>
/// 控制台命令历史的脱敏契约（自宿主 FreezeBlockerTests 迁入，REQ-A8）。
///
/// 迁移理由：它构造 <c>ConsoleView</c> 并断言历史文件里不得留下明文令牌——
/// 被测对象随前端整体搬到 Aurora，测试必须跟着走，否则宿主删掉前端后这条覆盖就凭空消失。
/// </summary>
public sealed class ConsoleHistoryRedactionTests
{
    [Fact]
    public void CommandHistoryMigratesLegacySecretsAndStoresOnlyRedactedManualEchoes()
    {
        var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan-history-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var historyPath = Path.Combine(root, "history.txt");
        try
        {
            File.WriteAllText(historyPath, "vulcan.web.token legacy-history-secret\n");
            var history = new CommandHistory(historyPath);
            Assert.Empty(history.Snapshot());
            Assert.DoesNotContain(
                "legacy-history-secret", File.ReadAllText(historyPath), StringComparison.Ordinal);

            Exception? failure = null;
            using var finished = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try
                {
                    var registry = new CommandRegistry();
                    registry.Register(SecretDescriptor("vulcan.web.token", "value", position: 0));
                    registry.Register(SecretDescriptor("secure.position", "clientSecret", position: 0));
                    registry.Register(new CommandDescriptor
                    {
                        Name = "vulcan.app.set",
                        Summary = "set",
                        Parameters =
                        [
                            new ParameterSpec
                            {
                                Name = "key",
                                Description = "key",
                                Required = true,
                                Position = 0,
                            },
                            new ParameterSpec
                            {
                                Name = "value",
                                Description = "value",
                                Required = true,
                                Position = 1,
                            },
                        ],
                        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("set")),
                    });
                    registry.Register(new CommandDescriptor
                    {
                        Name = "safe.read",
                        Summary = "safe",
                        Parameters =
                        [
                            new ParameterSpec
                            {
                                Name = "value",
                                Description = "value",
                                Required = true,
                                Position = 0,
                            },
                        ],
                        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("safe")),
                    });
                    var log = new MemoryLog();
                    var bus = new CommandBus(registry, log);
                    _ = new HistoryAurora.Shell.Console.ConsoleView(
                        log,
                        bus,
                        history,
                        new HistoryAurora.Shell.CommandSurface.DeferredCommandCatalogSession());

                    bus.ExecuteAsync("vulcan.web.token console-history-secret", "手动").GetAwaiter().GetResult();
                    bus.ExecuteAsync("vulcan.app.set mcp.token setting-history-secret", "手动")
                        .GetAwaiter().GetResult();
                    bus.ExecuteAsync("vulcan.web.token \"malformed-token-history-secret", "手动")
                        .GetAwaiter().GetResult();
                    bus.ExecuteAsync("vulcan.app.set mcp.token \"malformed-setting-history-secret", "手动")
                        .GetAwaiter().GetResult();
                    bus.ExecuteAsync("secure.position \"derived-history-secret", "手动")
                        .GetAwaiter().GetResult();
                    bus.ExecuteAsync("safe.read visible-value", "手动").GetAwaiter().GetResult();
                    history.Save();

                    var historyRegistry = new CommandRegistry();
                    var historyBus = new CommandBus(historyRegistry, new MemoryLog());
                    BuiltinCommands.Register(historyRegistry, new ShellCommandServices
                    {
                        Window = null!,
                        Docking = null!,
                        Console = null!,
                        History = history,
                        Settings = new MemorySettings(),
                        Log = new MemoryLog(),
                        Bus = historyBus,
                        DataDirectory = root,
                    });
                    var historyResult = historyBus.ExecuteAsync("aurora.command.history count=20", "Web:read")
                        .GetAwaiter().GetResult();
                    Assert.True(historyResult.Success, historyResult.Message);
                    Assert.DoesNotContain("console-history-secret", historyResult.Message, StringComparison.Ordinal);
                    Assert.DoesNotContain("setting-history-secret", historyResult.Message, StringComparison.Ordinal);
                    Assert.DoesNotContain("malformed-token-history-secret", historyResult.Message, StringComparison.Ordinal);
                    Assert.DoesNotContain("malformed-setting-history-secret", historyResult.Message, StringComparison.Ordinal);
                    Assert.DoesNotContain("derived-history-secret", historyResult.Message, StringComparison.Ordinal);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    finished.Set();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
            thread.Join();
            if (failure != null)
                throw failure;

            Assert.Equal(
                [
                    "vulcan.web.token [REDACTED]",
                    "vulcan.app.set mcp.token [REDACTED]",
                    "vulcan.web.token [REDACTED]",
                    "vulcan.app.set [REDACTED]",
                    "secure.position [REDACTED]",
                    "safe.read visible-value",
                ],
                history.Snapshot());
            var persisted = File.ReadAllText(historyPath);
            Assert.Contains("# HistoryVulcan.CommandHistory.v2:redacted", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("console-history-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("setting-history-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("malformed-token-history-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("malformed-setting-history-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("derived-history-secret", persisted, StringComparison.Ordinal);
            Assert.Contains("visible-value", persisted, StringComparison.Ordinal);
            Assert.Equal(history.Snapshot(), new CommandHistory(historyPath).Snapshot());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    /// <summary>
    /// 必须真正触发 <c>EntryAdded</c>：ConsoleView 靠订阅该事件把命令回显写进历史。
    /// 空实现的事件（<c>{ add { } remove { } }</c>）会让控制台永远收不到条目，
    /// 症状是历史快照为空——而这正是本用例要断言的东西，等于测试静默失效。
    /// </summary>
    private sealed class MemoryLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = [];

        public void Log(ShellLogLevel level, string category, string message)
        {
            var entry = new ShellLogEntry(DateTime.UtcNow, level, category, message);
            lock (_entries)
                _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot()
        {
            lock (_entries)
                return _entries.ToList();
        }
    }

    private static CommandDescriptor SecretDescriptor(string name, string parameter, int? position)
        => new()
        {
            Name = name,
            Summary = "secret",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = parameter,
                    Description = parameter,
                    Required = true,
                    Position = position,
                },
            ],
            Handler = CommandDescriptor.Sync(context =>
            {
                var value = context.RequireString(parameter);
                return CommandResult.Ok($"set {value}", new { value });
            }),
        };
}
