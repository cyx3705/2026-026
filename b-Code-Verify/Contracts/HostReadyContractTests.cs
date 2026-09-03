using System.IO;
using HistoryAurora.Shell.Base.Docking;
using HistoryAurora.Shell.Composition;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// <c>aurora.host.ready</c> 的契约（宿主 DEC-057）。
///
/// 界面在自己的 <c>Attach</c> 里就开了 STA 线程去问各模块要页面，而那一刻宿主还在装别的
/// 模块。1.16.0 之前判断「什么时候可以问」靠的是「注册表安静 150ms」——装载期间注册表
/// 本来就安静得远超这个窗口，于是那一轮问到的是没长齐的目录，启动时有几率少几页。
/// 宿主 5.1.3 起给出确定的完成点并按命令名通知；本用例守的是那条命令确实在、
/// 确实能被宿主抄走、确实做的是整轮重拉。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class HostReadyContractTests
{
    [Fact]
    public void ReadyHookIsRegisteredWhereTheHostCanFindIt()
    {
        RunShell(window =>
        {
            Assert.True(
                window.Commands.Registry.TryGet("aurora.host.ready", out var descriptor),
                "aurora.host.ready 未注册：宿主按注册表里有没有这条决定要不要通知本模块。");

            // 来源必须是前端目录：AuroraShellHost.PublishShellCommands 只抄这一类，
            // 抄不到就等于宿主永远看不见这条命令，通知也就永远发不出来。
            Assert.Equal(
                FrontendCommandCatalog.Source,
                window.Commands.Registry.GetSource(descriptor.Name));
            Assert.Equal("aurora", descriptor.Domain);
            Assert.Equal("host", descriptor.CommandClass);

            // 这是宿主的生命周期回调，不是给人或远端按的按钮。
            Assert.False(string.IsNullOrWhiteSpace(descriptor.HiddenReason));
            Assert.True(descriptor.RequiresUiThread);
        });
    }

    [Fact]
    public void ReadyHookRunsAFullSweepAndReportsWhatItAsked()
    {
        RunShell(window =>
        {
            window.Commands.Registry.Register(
                DescribeCommand("zeta"),
                "module:HistoryZeta");

            var result = window.Commands.ExecuteAsync("aurora.host.ready", "test")
                .GetAwaiter().GetResult();

            Assert.True(result.Success, result.Message);
            // 整轮重拉，不是「知道了」：回执必须说得出问了谁、建了几页，
            // 否则真机上无从分辨钩子是跑了还是只是没报错。
            Assert.Contains("问了 1 个模块", result.Message, StringComparison.Ordinal);
            Assert.Contains("建了 1 页", result.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ReadyHookSurvivesAModuleThatAnswersNothing()
    {
        RunShell(window =>
        {
            window.Commands.Registry.Register(
                new CommandDescriptor
                {
                    Name = "zeta.ui.describe",
                    Domain = "zeta",
                    CommandClass = "ui",
                    Summary = "页面描述",
                    Readonly = true,
                    Handler = CommandDescriptor.Sync(_ => CommandResult.Fail("模块还没准备好")),
                },
                "module:HistoryZeta");

            var result = window.Commands.ExecuteAsync("aurora.host.ready", "test")
                .GetAwaiter().GetResult();

            // 一个模块答不上来不该让整轮就绪失败——那会把「界面没建页」升级成
            // 「宿主以为通知失败」，而两者的排查方向完全不同。
            Assert.True(result.Success, result.Message);
            Assert.Contains("跳过 zeta", result.Message, StringComparison.Ordinal);
        });
    }

    private static CommandDescriptor DescribeCommand(string domain) => new()
    {
        Name = domain + ".ui.describe",
        Domain = domain,
        CommandClass = "ui",
        Summary = "页面描述",
        Readonly = true,
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("""
            {
              "schemaVersion": 1,
              "owner": "HistoryZeta",
              "pages": [
                {
                  "id": "zeta.home",
                  "title": "Zeta",
                  "content": { "type": "text", "text": "hi" }
                }
              ]
            }
            """)),
    };

    private static void RunShell(Action<ShellWindow> assert)
    {
        UiTestHost.RunSta(() =>
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryAurora-ready-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            try
            {
                var window = new ShellWindow(
                    new ShellConfig { AppName = "HistoryAurora Ready Test", AppVersion = "1.0.0" },
                    new MemoryLayoutStore(),
                    new NullLog(),
                    new MemorySettings(),
                    dataDirectory)
                {
                    Width = 800,
                    Height = 600,
                    ShowInTaskbar = false,
                };
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

        public int GetInt(string key, int fallback)
            => _values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
                ? parsed
                : fallback;

        public void Set(string key, string value) => _values[key] = value;

        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message)
        {
        }

        public event EventHandler<ShellLogEntry>? EntryAdded
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
