using System.Windows.Controls;
using AvalonDock;
using HistoryAurora.Shell.Base.Docking;
using HistoryAurora.Shell.Components.Scenes;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 场景（REQ-UI-084 ～ 087，见 b-Office/current/场景与导航方案.md）。
///
/// 夹具照着真实的两个极端搭：Janus 三页协同（中央 + 左侧 + 并入控制台的标签组），
/// Minerva 只有一页。命令集与控制台是常驻页。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class SceneContractTests
{
    /// <summary>REQ-UI-084 / 085：每个模块自动得到一个场景，切过去只剩它的页与常驻页。</summary>
    [Fact]
    public void EachModuleGetsASceneAndSwitchingKeepsOnlyItsPages() => RunScene((host, scenes, _) =>
    {
        var list = scenes.List();
        Assert.Equal(new[] { "overview", "projops", "graph" }, list.Single(s => s.Id == "HistoryJanus").Pages);
        Assert.Equal(new[] { "mapping" }, list.Single(s => s.Id == "HistoryMinerva").Pages);
        Assert.Equal(SceneSource.All, list.Single(s => s.Id == SceneManager.AllId).Source);
        Assert.Equal(SceneManager.AllId, scenes.ActiveId);

        Assert.True(scenes.Go("Minerva").Ok);
        AssertVisible(host, "mcp", "console", "mapping");

        Assert.True(scenes.Go("janus").Ok);
        AssertVisible(host, "mcp", "console", "overview", "projops", "graph");
        Assert.Equal("HistoryJanus", scenes.ActiveId);

        Assert.True(scenes.Go(SceneManager.AllId).Ok);
        AssertVisible(host, "mcp", "console", "overview", "projops", "graph", "mapping");
    });

    /// <summary>
    /// REQ-UI-086：切场景不重建页面视图。第二次回到 Minerva 走的是它存下的命名布局，
    /// 两条路（只做显隐 / 恢复快照）都要验到。
    /// </summary>
    [Fact]
    public void SwitchingScenesKeepsThePageViews() => RunScene((host, scenes, _) =>
    {
        var mapping = host.FindContent("mapping");
        var overview = host.FindContent("overview");
        Assert.NotNull(mapping);
        Assert.NotNull(overview);

        scenes.Go("Minerva");
        scenes.Go("Janus");
        scenes.Go("Minerva");
        scenes.Go("Janus");

        Assert.Same(mapping, host.FindContent("mapping"));
        Assert.Same(overview, host.FindContent("overview"));
        AssertVisible(host, "mcp", "console", "overview", "projops", "graph");
    });

    /// <summary>REQ-UI-085：场景的布局按场景 id 存成命名布局——模块场景就是模块名。</summary>
    [Fact]
    public void SceneLayoutsAreNamedLayoutsKeyedByModuleName() => RunScene((host, scenes, _) =>
    {
        scenes.Go("Minerva");
        scenes.Go("Janus");

        Assert.Contains("all", host.ListLayouts());
        Assert.Contains("HistoryMinerva", host.ListLayouts());
    });

    /// <summary>在 Minerva 场景里 Janus 热重载一次，它新登记的页不得挤进来；Minerva 自己的新页照常露面。</summary>
    [Fact]
    public void PagesRegisteredWhileInAnotherSceneStayOutOfSight() => RunScene((host, scenes, _) =>
    {
        scenes.Go("Minerva");
        host.RegisterWindow(Tool("late", DockSide.Right), "HistoryJanus");
        host.RegisterWindow(Tool("options", DockSide.Right), "HistoryMinerva");
        scenes.OnWindowsChanged();

        AssertVisible(host, "mcp", "console", "mapping", "options");
        Assert.Contains("late", scenes.Find("Janus")!.Pages);
    });

    /// <summary>增删只改指定的场景；常驻页与「全部」拒绝增删。</summary>
    [Fact]
    public void AddAndRemoveChangeOnlyThatScene() => RunScene((host, scenes, _) =>
    {
        scenes.Go("Minerva");
        Assert.True(scenes.Add("graph").Ok);
        AssertVisible(host, "mcp", "console", "mapping", "graph");

        scenes.Go("Janus");
        AssertVisible(host, "mcp", "console", "overview", "projops", "graph");
        Assert.True(scenes.Remove("projops").Ok);
        AssertVisible(host, "mcp", "console", "overview", "graph");

        Assert.Equal(new[] { "mapping", "graph" }, scenes.Find("Minerva")!.Pages);
        Assert.False(scenes.Remove("console").Ok);
        Assert.False(scenes.Add("mapping", SceneManager.AllId).Ok);
    });

    /// <summary>重置丢掉增补与剔除。</summary>
    [Fact]
    public void ResetDropsTheUsersChanges() => RunScene((host, scenes, _) =>
    {
        scenes.Go("Minerva");
        scenes.Add("graph");
        Assert.True(scenes.Reset().Ok);

        Assert.Equal(new[] { "mapping" }, scenes.Find("Minerva")!.Pages);
        AssertVisible(host, "mcp", "console", "mapping");
    });

    /// <summary>REQ-UI-087：另存场景引用的页没了，照样能切，缺的页有名有姓地报出来。</summary>
    [Fact]
    public void SavedSceneReportsPagesThatAreNoLongerRegistered() => RunScene((host, scenes, _) =>
    {
        scenes.Go("Minerva");
        Assert.True(scenes.Add("projops").Ok);
        Assert.True(scenes.Save("drawing", "出图").Ok);
        Assert.False(scenes.Save("HistoryJanus", null).Ok);

        host.UnregisterOwner("HistoryMinerva");
        var saved = scenes.Find("出图")!;
        Assert.Equal(SceneSource.User, saved.Source);
        Assert.Equal(new[] { "projops" }, saved.Pages);
        Assert.Equal(new[] { "HistoryMinerva/mapping" }, saved.Broken);

        var result = scenes.Go("drawing");
        Assert.True(result.Ok);
        Assert.Contains("未注册", result.Message);
    });

    /// <summary>打开一页：当前场景没有它，就切到含它的场景。</summary>
    [Fact]
    public void OpeningAPageOutsideTheSceneSwitchesToTheSceneThatHasIt() => RunScene((host, scenes, _) =>
    {
        scenes.Go("Minerva");
        Assert.True(scenes.Open("graph").Ok);

        Assert.Equal("HistoryJanus", scenes.ActiveId);
        Assert.Contains("graph", Visible(host));
    });

    /// <summary>当前场景与使用频次跨重启还在；列表按频次排。</summary>
    [Fact]
    public void ActiveSceneAndUsageSurviveARestart() => RunScene((host, scenes, settings) =>
    {
        scenes.Go("Janus");
        scenes.Go("Minerva");
        scenes.Go("Janus");

        var again = new SceneManager(host, settings, new UsageLedger(settings, new NullLog()), new NullLog());
        Assert.Equal("HistoryJanus", again.ActiveId);
        Assert.Equal(2, again.Find("Janus")!.Uses);
        var order = again.List().Select(s => s.Id).ToList();
        Assert.True(order.IndexOf("HistoryJanus") < order.IndexOf("HistoryMinerva"));
    });

    private static void RunScene(Action<DockingHost, SceneManager, MemorySettings> body)
        => UiTestHost.RunSta(() =>
        {
            var host = new DockingHost(
                new DockingManager(),
                [Tool(StandardWindowIds.Mcp, DockSide.Center), Tool(StandardWindowIds.Console, DockSide.Bottom)],
                new MemoryLayoutStore(),
                new NullLog());
            host.Initialize();
            host.RegisterWindow(Tool("overview", DockSide.Center), "HistoryJanus");
            host.RegisterWindow(Tool("projops", DockSide.Left), "HistoryJanus");
            host.RegisterWindow(Tool("graph", DockSide.Tab, StandardWindowIds.Console), "HistoryJanus");
            host.RegisterWindow(Tool("mapping", DockSide.Center), "HistoryMinerva");

            var settings = new MemorySettings();
            var scenes = new SceneManager(host, settings, new UsageLedger(settings, new NullLog()), new NullLog());
            body(host, scenes, settings);
        });

    private static ToolWindowDescriptor Tool(string id, DockSide side, string? tabTarget = null) => new()
    {
        Id = id,
        Title = id,
        DefaultSide = side,
        DefaultTabTarget = tabTarget,
        ContentFactory = () => new Border(),
    };

    private static HashSet<string> Visible(DockingHost host)
        => host.ListWindows().Where(w => w.IsVisible).Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void AssertVisible(DockingHost host, params string[] expected)
        => Assert.Equal(
            expected.OrderBy(id => id, StringComparer.Ordinal),
            Visible(host).OrderBy(id => id, StringComparer.Ordinal));

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

    private sealed class MemorySettings : HistoryVulcan.Core.Storage.ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class NullLog : HistoryVulcan.Core.Logging.IShellLog
    {
        public void Log(HistoryVulcan.Core.Logging.ShellLogLevel level, string category, string message) { }
        public event EventHandler<HistoryVulcan.Core.Logging.ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<HistoryVulcan.Core.Logging.ShellLogEntry> Snapshot() => [];
    }
}
