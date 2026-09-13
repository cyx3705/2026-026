using HistoryAurora.Shell.Base.Docking;
using HistoryAurora.Shell.Components.Pages;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// <c>aurora.ui.invalidate</c> 的契约。这里有两个身份，它们按设计**不相等**：
/// 撤旧页按模块名（<c>HistoryMercury</c>），拉描述按指令域（<c>mercury.ui.describe</c>，
/// 域去掉 History 前缀，宿主手册 §3.3.1）。
///
/// 1.6.0 把同一个字符串两处都用了，于是：
/// 传 <c>owner=HistoryMercury</c>（命令自己的示例就是这么写的）会去调不存在的
/// <c>HistoryMercury.ui.describe</c>；传 <c>owner=mercury</c> 拉得到描述，
/// 却在 owner 表里撤不掉旧页，随后同 id 重注册失败。
/// 也就是说**任何模块的这条上行通知都拉不到描述**，而全量拉取那条路探测的是注册表里的域，
/// 一直是好的——所以这个缺陷只在真机的这一条路径上现形（2026-08-25 Mercury 部署时实测）。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class PageInvalidateContractTests
{
    [Fact]
    public void ReloadOwner_LoadsActionsBeforeRenderingAndRemovesRetiredActions()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var log = new MemoryLog();
            var bus = new CommandBus(registry, log);
            var actions = new HistoryAurora.Shell.Components.Actions.ActionRegistry(bus, log);
            var actionId = "mercury.entry.refresh";
            registry.Register(new CommandDescriptor
            {
                Name = "mercury.ui.actions",
                Domain = "mercury",
                Summary = "actions",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    owner = "HistoryMercury",
                    actions = new[] { new { id = actionId, command = "mercury.ui.actions" } },
                }))),
            });
            registry.Register(new CommandDescriptor
            {
                Name = "mercury.ui.describe",
                Domain = "mercury",
                Summary = "page",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    owner = "HistoryMercury",
                    pages = new[] { new { id = "dock.manager", title = "dock", content = new
                    {
                        type = "panel", id = "dock.ops", rows = new[] { new { widgets = new[]
                        { new { kind = "button", action = actionId, text = "refresh" } } } },
                    } } },
                }))),
            });
            var loader = new ModulePageLoader(bus, new FakeDocking(), log, actions: actions);
            loader.ReloadOwnerAsync("HistoryMercury").GetAwaiter().GetResult();
            Assert.True(actions.Resolve(actionId).Ok);
            actionId = "mercury.entry.newrefresh";
            loader.ReloadOwnerAsync("HistoryMercury").GetAwaiter().GetResult();
            Assert.True(actions.Resolve(actionId).Ok);
            Assert.False(actions.Resolve("mercury.entry.refresh").Ok);
            Assert.DoesNotContain(log.Snapshot(), e => e.Message.Contains("未声明的动作"));
        });
    }

    [Fact]
    public void ReloadOwner_AcceptsTheModuleNameFromTheCommandExample()
    {
        // 渲染要建真的 WPF 控件，因此整条路径必须跑在 STA 上。
        UiTestHost.RunSta(() =>
        {
            var (loader, docking) = Loader();

            var report = loader.ReloadOwnerAsync("HistoryMercury").GetAwaiter().GetResult();

            Assert.Empty(report.Skipped);
            Assert.Equal(1, report.PagesRegistered);
            Assert.Equal("dock.manager", Assert.Single(docking.Registered));
        });
    }

    [Fact]
    public void ReloadOwner_AlsoAcceptsThePlainDomain()
    {
        UiTestHost.RunSta(() =>
        {
            var (loader, docking) = Loader();

            var report = loader.ReloadOwnerAsync("mercury").GetAwaiter().GetResult();

            Assert.Empty(report.Skipped);
            Assert.Equal(1, report.PagesRegistered);
            Assert.Single(docking.Registered);
        });
    }

    [Fact]
    public void ReloadOwner_DoesNotDropAllPagesBeforeReplacingContent()
    {
        UiTestHost.RunSta(() =>
        {
            var (loader, docking) = Loader();
            loader.ReloadOwnerAsync("mercury").GetAwaiter().GetResult();

            loader.ReloadOwnerAsync("HistoryMercury").GetAwaiter().GetResult();

            // 同 owner 重拉保留停靠节点；撤整组会破坏当前场景的手调布局。
            Assert.Empty(docking.Dropped);
            Assert.Equal(2, docking.Registered.Count);
        });
    }

    [Fact]
    public void ReloadOwner_IgnoresAnEmptyName()
    {
        UiTestHost.RunSta(() =>
        {
            var (loader, _) = Loader();

            var report = loader.ReloadOwnerAsync("   ").GetAwaiter().GetResult();

            Assert.Equal(0, report.ModulesAsked);
            Assert.Equal(0, report.PagesRegistered);
        });
    }

    private static (ModulePageLoader Loader, FakeDocking Docking) Loader()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "mercury" + ModulePageLoader.DescribeSuffix,
            Domain = "mercury",
            CommandClass = "ui",
            Summary = "页面描述",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("""
                {
                  "schemaVersion": 1,
                  "owner": "HistoryMercury",
                  "pages": [
                    {
                      "id": "dock.manager",
                      "title": "导航",
                      "content": { "type": "text", "text": "hi" }
                    }
                  ]
                }
                """)),
        });

        var log = new MemoryLog();
        var docking = new FakeDocking();
        return (new ModulePageLoader(new CommandBus(registry, log), docking, log), docking);
    }

    /// <summary>只记发生了什么的停靠桩：这条契约与真实布局无关。</summary>
    private sealed class FakeDocking : IDockingService
    {
        public List<string> Registered { get; } = [];

        public List<string> Dropped { get; } = [];

        public string? MaximizedId => null;

        public event EventHandler<ShellCommandEventArgs>? CommandGenerated;

        public event EventHandler? WindowsChanged;

        public void RegisterWindow(ToolWindowDescriptor descriptor, string owner)
        {
            Registered.Add(descriptor.Id);
            CommandGenerated?.Invoke(this, new ShellCommandEventArgs { CommandText = "", Source = owner });
            WindowsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UnregisterOwner(string owner) => Dropped.Add(owner);

        public IReadOnlyList<ToolWindowInfo> ListWindows() => [];

        public IReadOnlyList<string> ListLayouts() => [];

        public bool LoadLayout(string name) => false;

        public void Show(string id) { }

        public void Hide(string id) { }

        public void Float(string id) { }

        public void Dock(string id, DockSide side, double? ratio = null, string? targetId = null) { }

        public void SetRatio(string id, double ratio) { }

        public void ResetWindow(string id) { }

        public void ResetLayout() { }

        public void SaveLayout(string name) { }

        public void UnregisterWindow(string id) { }

        public void MaximizeWindow(string id) { }

        public void RestoreLayoutFromMaximized() { }
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
