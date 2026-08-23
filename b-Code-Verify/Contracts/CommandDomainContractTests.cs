using System.IO;
using HistoryAurora.Shell;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using Xunit;

namespace HistoryAurora.Verify.Contracts;

/// <summary>
/// Aurora 注册的命令必须落在 `aurora.*` 域（DEC-006 / 宿主 DEC-050：域跟随实现方）。
///
/// 取代原 <c>SkeletonContractTests</c>。那条用例的职责是「骨架阶段 Attach 不注册任何命令，
/// 第一组能力迁入时必须失败并被更新」——REQ-A7 把 36 条命令全部迁入后它已完成使命，
/// 换成守住迁移**结果**的用例。
///
/// 这条断言真正防的是回归：批量改名靠的是文本替换，而
/// <c>RegisterWindowVerb</c> 这类「Name 是变量」的注册器天然躲开文本替换——
/// A7 施工时就有 4 条命令因此处于「名是 aurora.*、域是 vulcan」的状态，
/// 且只有按运行时域计数核对才发现。源码替换数对不出这种错。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class CommandDomainContractTests
{
    /// <summary>
    /// 宿主按字面量中继的生命周期命令。这些名字是**宿主↔前端的协议**，不是实现名：
    /// 宿主 <c>ServiceCommands</c> 直接发 <c>vulcan.app.close</c>，
    /// 安装/移除运行包时也按名中继 <c>vulcan.module.*</c> 让前端卸下同名 UI 快照（DEC-043）。
    /// 改掉它们中继就断，因此 DEC-050 的「域跟随实现方」对这批不适用。
    /// </summary>
    private static readonly string[] RelayProtocolPrefixes =
    [
        "vulcan.app.",
        "vulcan.module.",
    ];

    [Fact]
    public void EveryFrontendCommandLivesInTheAuroraDomain()
    {
        RunShell(window =>
        {
            var offenders = AuroraRegistered(window)
                .Where(descriptor => !IsRelayProtocol(descriptor.Name))
                .Where(descriptor => !descriptor.Name.StartsWith("aurora.", StringComparison.Ordinal))
                .Select(descriptor => descriptor.Name)
                .ToList();

            Assert.True(
                offenders.Count == 0,
                $"以下命令由 Aurora 实现却不在 aurora 域: {string.Join(", ", offenders)}");
        });
    }

    /// <summary>
    /// 名与域必须一致。这是 A7 实际踩到的坑：`Name` 由变量传入的注册器会躲开按
    /// `Name = "aurora.*"` 做的批量 `Domain` 同步，产出「名对域错」的描述符——
    /// 它在 `command.list domain=vulcan` 里显示为一条 aurora 开头的命令，肉眼极易漏掉。
    /// </summary>
    [Fact]
    public void CommandNamePrefixAlwaysMatchesItsDeclaredDomain()
    {
        RunShell(window =>
        {
            var mismatched = AuroraRegistered(window)
                .Where(descriptor => !IsRelayProtocol(descriptor.Name))
                .Where(descriptor => !descriptor.Name.StartsWith(
                    window.Commands.Registry.GetDomain(descriptor.Name) + ".", StringComparison.Ordinal))
                .Select(descriptor =>
                    $"{descriptor.Name}(域={window.Commands.Registry.GetDomain(descriptor.Name)})")
                .ToList();

            Assert.True(
                mismatched.Count == 0,
                $"以下命令名与域不一致: {string.Join(", ", mismatched)}");
        });
    }

    /// <summary>
    /// 只取 Aurora 自己注册的命令。注册表里还有 <c>HistoryVulcan.Services</c> 提供的
    /// 目录查询命令（<c>vulcan.command.list/show/domains/manual</c>）——它们由宿主实现、
    /// 只是登记进了本进程的注册表，不受「Aurora 的命令必须在 aurora 域」约束。
    /// 按注册来源区分，而不是按名字猜。
    /// </summary>
    private static IEnumerable<CommandDescriptor> AuroraRegistered(ShellWindow window)
        => window.Commands.Registry.All()
            .Where(descriptor => string.Equals(
                window.Commands.Registry.GetSource(descriptor.Name),
                FrontendCommandCatalog.Source,
                StringComparison.Ordinal));

    private static bool IsRelayProtocol(string name)
        => RelayProtocolPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    private static void RunShell(Action<ShellWindow> assert)
    {
        UiTestHost.RunSta(() =>
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryAurora-domain-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            try
            {
                var window = new ShellWindow(
                    new ShellConfig { AppName = "HistoryAurora Domain Test", AppVersion = "1.0.0" },
                    new MemoryLayoutStore(),
                    new NullLog(),
                    new MemorySettings(),
                    dataDirectory)
                {
                    Width = 800,
                    Height = 600,
                    ShowInTaskbar = false,
                };
                // ShellWindow 不是 IDisposable：它是 WPF Window，靠 Close 释放。
                try { assert(window); }
                finally { window.Close(); }
            }
            finally
            {
                try { Directory.Delete(dataDirectory, recursive: true); }
                catch (IOException) { }
            }
        });
    }

    private sealed class MemoryLayoutStore : ILayoutStore
    {
        private string? _current;
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

        public string? ReadCurrent() => _current;
        public void WriteCurrent(string payload) => _current = payload;
        public void DeleteCurrent() => _current = null;
        public void ReplaceCurrent(string oldValue, string newValue)
            => _current = _current?.Replace(oldValue, newValue, StringComparison.Ordinal);
        public string? ReadNamed(string name) => _named.GetValueOrDefault(name);
        public void WriteNamed(string name, string payload) => _named[name] = payload;
        public IReadOnlyList<string> ListNamed() => _named.Keys.ToList();
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
