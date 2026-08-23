using HistoryAurora.Shell.Pages;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Docking;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 页面拉取的契约。三条是这套机制存在的理由，缺一条它就不比旧路径强：
/// <list type="number">
///   <item>只问声明了页面的模块——不靠 manifest 字段，注册表本身就是权威；</item>
///   <item>一个模块的描述坏掉只跳过它，不牵连其余（协议 §1.5）；</item>
///   <item>重拉先撤后建——注册是派生状态，不能像 web.frontendcatalog 那样越攒越多。</item>
/// </list>
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class ModulePageLoaderContractTests
{
    [Fact]
    public void Reload_AsksOnlyModulesThatDeclarePages()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var asked = new List<string>();
            Describe(registry, "demo", asked, OnePage("HistoryDemo", "alpha"));
            // 一条与页面无关的模块命令：不得因为它去问 other.ui.describe。
            registry.Register(Simple("other.core.status"));

            var (loader, docking, _) = Loader(registry);
            var report = loader.ReloadAsync().GetAwaiter().GetResult();

            Assert.Equal(1, report.ModulesAsked);
            Assert.Equal(["demo"], asked);
            Assert.Equal(["alpha"], docking.Registered.Select(r => r.Descriptor.Id));
        });
    }

    [Fact]
    public void Reload_SkipsBadDescriptionWithoutAffectingOtherModules()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var asked = new List<string>();
            Describe(registry, "broken", asked, "{ not json");
            Describe(registry, "demo", asked, OnePage("HistoryDemo", "alpha"));

            var (loader, docking, log) = Loader(registry);
            var report = loader.ReloadAsync().GetAwaiter().GetResult();

            Assert.Equal(2, report.ModulesAsked);
            Assert.Equal(1, report.PagesRegistered);
            Assert.Equal(["broken"], report.Skipped);
            Assert.Equal(["alpha"], docking.Registered.Select(r => r.Descriptor.Id));
            Assert.Contains(log.Snapshot(), e => e.Level == ShellLogLevel.Warn && e.Message.Contains("broken"));
        });
    }

    [Fact]
    public void Reload_DropsPreviousPagesInsteadOfAccumulating()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var asked = new List<string>();
            Describe(registry, "demo", asked, OnePage("HistoryDemo", "alpha"));

            var (loader, docking, _) = Loader(registry);
            loader.ReloadAsync().GetAwaiter().GetResult();
            loader.ReloadAsync().GetAwaiter().GetResult();

            // 第二轮必须先撤掉 HistoryDemo 的旧页面，否则重复注册会堆成幽灵条目——
            // 宿主侧 web.frontendcatalog 正是因为不回收，前端改名后留下了永久幻影。
            Assert.Equal(["HistoryDemo"], docking.Dropped);
            Assert.Equal(2, docking.Registered.Count);
        });
    }

    [Fact]
    public void Reload_RejectsDescriptionClaimingAnotherOwner()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var asked = new List<string>();
            // demo 域的模块声称自己是 HistoryMercury：不接受，否则可借描述抢注别人的页面。
            Describe(registry, "demo", asked, OnePage("HistoryMercury", "alpha"));

            var (loader, docking, _) = Loader(registry);
            var report = loader.ReloadAsync().GetAwaiter().GetResult();

            Assert.Equal(["demo"], report.Skipped);
            Assert.Empty(docking.Registered);
        });
    }

    [Fact]
    public void Reload_ReportsMissingComponentsForQuery()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var asked = new List<string>();
            Describe(registry, "demo", asked, """
                {
                  "schemaVersion": 1,
                  "owner": "HistoryDemo",
                  "pages": [
                    {
                      "id": "alpha", "title": "甲",
                      "content": { "type": "toggleGroup" }
                    }
                  ]
                }
                """);

            var (loader, _, _) = Loader(registry);
            loader.ReloadAsync().GetAwaiter().GetResult();

            var missing = Assert.Single(loader.Missing);
            Assert.Equal("HistoryDemo", missing.Owner);
            Assert.Equal("alpha", missing.PageId);
            Assert.Equal("toggleGroup", missing.Component);
        });
    }

    [Fact]
    public void ReloadOwner_TouchesOnlyThatModule()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var asked = new List<string>();
            Describe(registry, "demo", asked, OnePage("HistoryDemo", "alpha"));

            var (loader, docking, _) = Loader(registry);
            loader.ReloadAsync().GetAwaiter().GetResult();
            asked.Clear();
            docking.Registered.Clear();

            loader.ReloadOwnerAsync("demo").GetAwaiter().GetResult();

            Assert.Equal(["demo"], asked);
            Assert.Equal(["alpha"], docking.Registered.Select(r => r.Descriptor.Id));
        });
    }

    [Fact]
    public void Reload_CoalescesOverlappingRequestsIntoOneExtraPass()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var gate = new TaskCompletionSource();
            var calls = 0;

            registry.Register(new CommandDescriptor
            {
                Name = "demo" + ModulePageLoader.DescribeSuffix,
                Domain = "demo",
                CommandClass = "ui",
                Summary = "页面描述",
                Readonly = true,
                Handler = async _ =>
                {
                    calls++;
                    await gate.Task.ConfigureAwait(true);
                    return CommandResult.Ok(OnePage("HistoryDemo", "alpha"));
                },
            });

            var (loader, _, _) = Loader(registry);

            var first = loader.ReloadAsync();
            var second = loader.ReloadAsync();
            var third = loader.ReloadAsync();

            // 在途时重入只置脏标记，返回同一个任务。
            Assert.Same(first, second);
            Assert.Same(first, third);

            gate.SetResult();
            Assert.True(UiTestHost.PumpUntil(() => first.IsCompleted), "拉取未完成");

            // 两次重入合并成一次补跑，而不是各跑一遍：启动期实测会连打三遍。
            Assert.Equal(2, calls);
        });
    }

    [Fact]
    public void Reload_StaysUsableAfterAFailedPass()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var asked = new List<string>();
            Describe(registry, "demo", asked, OnePage("HistoryDemo", "alpha"));

            var (loader, docking, _) = Loader(registry);

            // 第一轮走通；闸门若在任何路径上没解开，第二轮会永久挂住。
            loader.ReloadAsync().GetAwaiter().GetResult();
            docking.Registered.Clear();
            loader.ReloadAsync().GetAwaiter().GetResult();

            Assert.Equal(["alpha"], docking.Registered.Select(r => r.Descriptor.Id));
        });
    }

    private static string OnePage(string owner, string id) => $$"""
        {
          "schemaVersion": 1,
          "owner": "{{owner}}",
          "pages": [
            { "id": "{{id}}", "title": "页", "content": { "type": "text", "text": "hi" } }
          ]
        }
        """;

    private static void Describe(CommandRegistry registry, string domain, List<string> asked, string payload)
        => registry.Register(new CommandDescriptor
        {
            Name = domain + ModulePageLoader.DescribeSuffix,
            Domain = domain,
            CommandClass = "ui",
            Summary = "页面描述",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                asked.Add(domain);
                return CommandResult.Ok(payload);
            }),
        });

    private static CommandDescriptor Simple(string name) => new()
    {
        Name = name,
        Domain = name.Split('.')[0],
        CommandClass = "core",
        Summary = "无关命令",
        Readonly = true,
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
    };

    private static (ModulePageLoader Loader, RecordingDocking Docking, MemoryLog Log) Loader(CommandRegistry registry)
    {
        var log = new MemoryLog();
        var docking = new RecordingDocking();
        return (new ModulePageLoader(new CommandBus(registry, log), docking, log), docking, log);
    }

    private sealed class RecordingDocking : IDockingService
    {
        public List<(ToolWindowDescriptor Descriptor, string Owner)> Registered { get; } = [];

        public List<string> Dropped { get; } = [];

        public void RegisterWindow(ToolWindowDescriptor descriptor, string owner)
            => Registered.Add((descriptor, owner));

        public void UnregisterOwner(string owner) => Dropped.Add(owner);

        public string? MaximizedId => null;

        public IReadOnlyList<ToolWindowInfo> ListWindows() => [];

        public void Show(string id) { }

        public void Hide(string id) { }

        public void Float(string id) { }

        public void Dock(string id, DockSide side, double? ratio = null, string? targetId = null) { }

        public void SetRatio(string id, double ratio) { }

        public void ResetWindow(string id) { }

        public void ResetLayout() { }

        public void SaveLayout(string name) { }

        public bool LoadLayout(string name) => false;

        public IReadOnlyList<string> ListLayouts() => [];

        public void UnregisterWindow(string id) { }

        public void MaximizeWindow(string id) { }

        public void RestoreLayoutFromMaximized() { }

        public event EventHandler<ShellCommandEventArgs>? CommandGenerated { add { } remove { } }

        public event EventHandler? WindowsChanged { add { } remove { } }
    }

    private sealed class MemoryLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = [];

        public void Log(ShellLogLevel level, string category, string message)
        {
            var entry = new ShellLogEntry(DateTime.Now, level, category, message);
            _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => _entries.ToList();
    }
}
