using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryAurora.Shell.Composition;
using Xunit;

namespace HistoryAurora.Verify.Contracts;

/// <summary>
/// 设置值脱敏必须落在 <c>CommandResult.Message</c> 上，而不只是日志（自宿主迁入，REQ-A8）。
///
/// 迁移理由：本用例覆盖的是**前端** `app.get/set` 对任意配置键的脱敏。
/// 宿主侧同名命令由 ServiceComposer 提供，契约窄得多——只接受 `mcp.*` 键（REQ-MCP-002），
/// 查 web.token 会直接被拒。两者不是同一份实现，测试必须跟着被测对象走。
/// 宿主侧的脱敏另有用例覆盖。
/// </summary>
public sealed class SettingRedactionContractTests
{
    [Fact]
    public async Task SecretSettingValuesAreMaskedInCommandResultsNotOnlyInLogs()
    {
        // 回归 FZR-01 的读路径:vulcan.app.get 声明 Readonly,对 scope=read 的远程设备放行,
        // 并在默认 readonly 策略下作为 MCP 工具可见。结果对象会原样序列化进 HTTP 响应体
        // 与 tools/call 载荷,因此断言必须落在 CommandResult.Message 上,而不只是日志。
        var settings = new MemorySettings();
        settings.Set("web.token", "delta-secret");
        settings.Set("database.connectionString", "connection-secret");
        settings.Set("signing.private_key", "private-secret");
        settings.Set("code", "code-setting-secret");
        settings.Set("console.history", "500");
        var log = new MemoryLog();
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, log);
        BuiltinCommands.Register(registry, new ShellCommandServices
        {
            Window = null!,
            Docking = null!,
            Console = null!,
            History = null!,
            Settings = settings,
            Log = log,
            Bus = bus,
            DataDirectory = "",
        });

        var single = await bus.ExecuteAsync("vulcan.app.get key=web.token", "Test");
        var code = await bus.ExecuteAsync("vulcan.app.get key=code", "Test");
        var listing = await bus.ExecuteAsync("vulcan.app.get", "Test");
        var write = await bus.ExecuteAsync("vulcan.app.set key=web.token value=echo-secret", "Test");

        Assert.DoesNotContain("delta-secret", single.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("code-setting-secret", code.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("code-setting-secret", listing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("delta-secret", listing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("connection-secret", listing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", listing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("echo-secret", write.Message, StringComparison.Ordinal);
        Assert.Contains("(已配置)", single.Message, StringComparison.Ordinal);
        Assert.Contains("(已配置)", code.Message, StringComparison.Ordinal);

        // 非敏感键不受影响,vulcan.app.get 仍是可用的排查工具
        Assert.Contains("console.history = 500", listing.Message, StringComparison.Ordinal);

        var written = string.Join('\n', log.Snapshot().Select(entry => entry.Message));
        Assert.DoesNotContain("delta-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("connection-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("code-setting-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("echo-secret", written, StringComparison.Ordinal);
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class MemoryLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = [];
        public void Log(ShellLogLevel level, string category, string message)
            => _entries.Add(new ShellLogEntry(DateTime.Now, level, category, message));
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => _entries;
    }
}
