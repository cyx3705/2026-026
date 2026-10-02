using System.IO;
using System.Windows;
using HistoryAurora.Shell.Composition;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Logging;
using Xunit;
using HistoryAurora.Shell.Neutral.Storage;
using HistoryAurora.Shell.Neutral.Logging;

namespace HistoryAurora.Verify;

/// <summary>
/// 进程内装载（1.29.0 起）：窗口先建、Aurora 自己的指令后登记进宿主。
/// 1.30.1 前这条用例寄放在专注态的 MaximizeContractTests 里，专注态删除时搬到这里。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class InProcessStartupContractTests
{
    /// <summary>
    /// 1.29.0 真机首装回归：进程内装载时窗口先建、自己的指令后登记进宿主。建窗时菜单校验若只认宿主目录，
    /// 构造函数就抛「菜单引用了无效指令」，界面起不来、一条指令都登记不上（契约测试的替身目录里本就有自己的表，测不出来）。
    /// </summary>
    [Fact]
    public void WindowBuildsBeforeItsOwnCommandsReachTheHostCatalog()
    {
        UiTestHost.RunSta(() =>
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryAurora-inproc-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            var hostTable = new HistoryAurora.Shell.Neutral.Commands.CommandTable();
            var bus = TestShell.InProcessBus(new NullLog(), hostTable, out var own);
            var window = new ShellWindow(
                new ShellConfig { AppName = "HistoryAurora InProc Test", AppVersion = "1.29.0" },
                new MemoryLayoutStore(),
                new NullLog(),
                new MemorySettings(),
                dataDirectory, bus, own);
            try
            {
                Assert.Empty(hostTable.All());
                Assert.True(own.TryGet("aurora.ui.show", out _));
                Assert.Null(bus.Validate("aurora.ui.show name=console"));
            }
            finally
            {
                window.Close();
                Directory.Delete(dataDirectory, recursive: true);
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
        public string? ReadNamed(string name) => _named.GetValueOrDefault(name);
        public void WriteNamed(string name, string payload) => _named[name] = payload;
        public IReadOnlyList<string> ListNamed() => _named.Keys.ToList();
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback)
            => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }
}
