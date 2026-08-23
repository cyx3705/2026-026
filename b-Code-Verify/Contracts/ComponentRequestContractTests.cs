using HistoryAurora.Shell.Pages;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 组件申请台账的契约。
///
/// 「模块不得自建组件」是硬禁令，所以模块被缺件卡住时必须有一条非自建的出口，
/// 否则禁令只会被绕过。这条出口要成立，三件事缺一不可：申请**落盘**（会话内的清单
/// 重启即失，没人能据以排期）、用出来的缺件**自动进账**（真实使用比设想可信）、
/// 已交付的**自动出账**（否则台账会越积越假）。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class ComponentRequestContractTests
{
    [Fact]
    public void Record_PersistsAcrossStoreInstances()
    {
        var settings = new MemorySettings();
        var log = new MemoryLog();

        new ComponentRequestStore(settings, log)
            .Record("segment.toggle", "HistoryJanus", "HistoryJanus/projops", "分段家族缺 Toggle 成员");

        // 换一个实例读——等价于重启后仍然在账上。
        var reopened = new ComponentRequestStore(settings, log).ListOpen();

        var request = Assert.Single(reopened);
        Assert.Equal("segment.toggle", request.Component);
        Assert.Equal("HistoryJanus", request.RequestedBy);
        Assert.Equal(["HistoryJanus/projops"], request.Pages);
        Assert.Contains("Toggle", request.Reason);
    }

    [Fact]
    public void Record_MergesRepeatsInsteadOfPilingUp()
    {
        var settings = new MemorySettings();
        var store = new ComponentRequestStore(settings, new MemoryLog());

        store.Record("segment.toggle", "HistoryJanus", "HistoryJanus/projops", null);
        store.Record("segment.toggle", "HistoryMercury", "HistoryMercury/mcp", null);
        store.Record("segment.toggle", "HistoryJanus", "HistoryJanus/projops", null);

        // 同一组件只占一条，但用到它的页面要累积——排期看的是波及面。
        var request = Assert.Single(store.ListOpen());
        Assert.Equal(["HistoryJanus/projops", "HistoryMercury/mcp"], request.Pages);

        // 首个提出方保留，不被后来者覆盖。
        Assert.Equal("HistoryJanus", request.RequestedBy);
    }

    [Fact]
    public void ListOpen_DropsRequestsTheRendererNowSupports()
    {
        var settings = new MemorySettings();
        var store = new ComponentRequestStore(settings, new MemoryLog());

        store.Record("segment.toggle", "HistoryJanus", null, null);
        // table 已在 V1 组件集内：它代表"已交付"的那一类。
        store.Record("table", "HistoryMercury", null, null);

        var open = store.ListOpen();

        Assert.Equal(["segment.toggle"], open.Select(r => r.Component));

        // 出账是持久的，不是每次列举时临时过滤。
        Assert.DoesNotContain("\"table\"", settings.Get(ComponentRequestStore.SettingKey));
    }

    [Fact]
    public void SupportedComponentsMatchWhatTheRendererActuallyBuilds()
    {
        UiTestHost.RunSta(() =>
        {
            // 台账把 SupportedComponents 当作"是否已交付"的唯一权威。这条断言防止它与
            // 渲染器的分派漂移——漂移的表现是：申请刚提就被自动出账，而页面上仍是占位。
            foreach (var component in PageRenderer.SupportedComponents)
            {
                var page = Page($$"""{ "type": "{{component}}" }""");
                var rendered = PageRenderer.Render(page, Context());
                Assert.True(
                    rendered.MissingComponents.Count == 0,
                    $"{component} 在受支持集合里，却仍渲染成占位");
            }
        });
    }

    [Fact]
    public void LoaderFilesRequestsForComponentsPagesActuallyUse()
    {
        UiTestHost.RunSta(() =>
        {
            var settings = new MemorySettings();
            var log = new MemoryLog();
            var registry = new CommandRegistry();
            registry.Register(new CommandDescriptor
            {
                Name = "demo" + ModulePageLoader.DescribeSuffix,
                Domain = "demo",
                CommandClass = "ui",
                Summary = "页面描述",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("""
                    {
                      "schemaVersion": 1,
                      "owner": "HistoryDemo",
                      "pages": [
                        { "id": "alpha", "title": "甲", "content": { "type": "toggleGroup" } }
                      ]
                    }
                    """)),
            });

            var store = new ComponentRequestStore(settings, log);
            var loader = new ModulePageLoader(
                new CommandBus(registry, log), new NullDocking(), log, store);

            loader.ReloadAsync().GetAwaiter().GetResult();

            // 页面用到了缺件 → 自动进账，无需模块另外提交申请。
            var request = Assert.Single(store.ListOpen());
            Assert.Equal("toggleGroup", request.Component);
            Assert.Equal("HistoryDemo", request.RequestedBy);
            Assert.Equal(["HistoryDemo/alpha"], request.Pages);
        });
    }

    private static PageDescription Page(string contentJson)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "owner": "HistoryDemo",
              "pages": [ { "id": "demo", "title": "演示", "content": {{contentJson}} } ]
            }
            """;
        var parsed = PageDescriptionReader.Read(json, "HistoryDemo");
        Assert.True(parsed.Ok, parsed.Error);
        return parsed.Value!.Pages[0];
    }

    private static PageRenderContext Context()
    {
        var log = new MemoryLog();
        return new PageRenderContext
        {
            Bus = new CommandBus(new CommandRegistry(), log),
            Log = log,
            Owner = "HistoryDemo",
        };
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
        {
            var entry = new ShellLogEntry(DateTime.Now, level, category, message);
            _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => _entries.ToList();
    }

    private sealed class NullDocking : IDockingService
    {
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

        public void RegisterWindow(ToolWindowDescriptor descriptor, string owner) { }

        public void UnregisterWindow(string id) { }

        public void UnregisterOwner(string owner) { }

        public void MaximizeWindow(string id) { }

        public void RestoreLayoutFromMaximized() { }

        public event EventHandler<ShellCommandEventArgs>? CommandGenerated { add { } remove { } }

        public event EventHandler? WindowsChanged { add { } remove { } }
    }
}
