using System.Windows.Controls;
using AvalonDock;
using HistoryAurora.Shell.Base.Docking;
using HistoryAurora.Shell.Components.Scenes;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 场景（REQ-UI-084 ～ 086、094，见 b-Office/current/场景与导航方案.md）。
///
/// 夹具照着真实的两个极端搭：Janus 三页协同（中央 + 左侧 + 并入控制台的标签组），
/// Minerva 只有一页。命令集与控制台是常驻页。
///
/// 1.20.0 起场景不拥有页面：场景之间的区别只是显隐，页面集合、增补剔除与断链账整套退役。
/// 顶栏只留一页（REQ-UI-096），所以命令集虽是常驻页，进了有自己中央页的场景就被顶掉。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class SceneContractTests
{
    /// <summary>REQ-UI-084 / 094：每个模块自动得到一个场景，第一次进入露出它自己的页与常驻页。</summary>
    [Fact]
    public void EachModuleGetsASceneWhoseFirstVisitShowsItsOwnPages() => RunScene((host, scenes, _) =>
    {
        var list = scenes.List();
        Assert.Equal(SceneSource.Derived, list.Single(s => s.Id == "HistoryJanus").Source);
        Assert.Equal(SceneSource.Derived, list.Single(s => s.Id == "HistoryMinerva").Source);
        Assert.Equal(SceneSource.All, list.Single(s => s.Id == SceneManager.AllId).Source);
        Assert.Equal(SceneManager.AllId, scenes.ActiveId);

        // 命令集是常驻页，但顶栏只留一页：留的是场景自己的中央页。
        Assert.True(scenes.Go("Minerva").Ok);
        AssertVisible(host, "console", "mapping");

        // 每格只留一页（REQ-UI-100）：「图」并在控制台那一格，进 Janus 留本模块的「图」，控制台藏着。
        Assert.True(scenes.Go("janus").Ok);
        AssertVisible(host, "overview", "projops", "graph");
        Assert.Equal("HistoryJanus", scenes.ActiveId);
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
        AssertVisible(host, "overview", "projops", "graph");
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

    /// <summary>
    /// REQ-UI-094：场景之间的区别只是显隐。在一个场景里打开、隐藏的页，只记在这个场景的布局里；
    /// 回到别的场景是它自己离开时的样子。显隐走的就是停靠层的 Show / Hide，没有另一套增删。
    /// </summary>
    [Fact]
    public void ASceneRemembersWhatWasShownAndHiddenInIt() => RunScene((host, scenes, _) =>
    {
        // 每格只留一页（REQ-UI-100）：「图」并在控制台那一格，打开它就顶掉控制台。
        scenes.Go("Minerva");
        host.Show("graph");
        AssertVisible(host, "mapping", "graph");

        scenes.Go("Janus");
        host.Hide("projops");
        AssertVisible(host, "overview", "graph");

        scenes.Go("Minerva");
        AssertVisible(host, "mapping", "graph");

        scenes.Go("Janus");
        AssertVisible(host, "overview", "graph");
    });

    /// <summary>
    /// 在 Minerva 场景里 Janus 热重载一次，它新登记的页不得挤进来；Minerva 自己的新页照常露面。
    /// 1.20.1 起两页落进右栏同一格：Janus 的页先占了位子、Minerva 的页登记时不抢位；
    /// 场景把 Janus 的页藏掉之后，Minerva 的页回到这个位子（REQ-UI-100 的等位）。
    /// </summary>
    [Fact]
    public void PagesRegisteredWhileInAnotherSceneStayOutOfSight() => RunScene((host, scenes, _) =>
    {
        scenes.Go("Minerva");
        host.RegisterWindow(Tool("late", DockSide.Right), "HistoryJanus");
        host.RegisterWindow(Tool("options", DockSide.Right), "HistoryMinerva");
        scenes.OnWindowsChanged();

        AssertVisible(host, "console", "mapping", "options");

        // 第一次进 Janus：初值规则照样认得它后来登记的页。
        scenes.Go("Janus");
        Assert.Contains("late", Visible(host));
    });

    /// <summary>重置回到第一次进入时的样子：在场景里打开的别处的页收回去。</summary>
    [Fact]
    public void ResetBringsTheSceneBackToItsFirstVisit() => RunScene((host, scenes, _) =>
    {
        scenes.Go("Minerva");
        host.Show("graph");
        Assert.True(scenes.Reset().Ok);

        AssertVisible(host, "console", "mapping");
    });

    /// <summary>
    /// 另存场景就是一份命名布局。它记着的页后来没了，照样能切——没有断链账，缺的页不在树里而已。
    /// 另存场景没有初值可回，不是当前场景时拒绝重置。
    /// </summary>
    [Fact]
    public void SavedSceneIsJustANamedLayout() => RunScene((host, scenes, _) =>
    {
        scenes.Go("Minerva");
        host.Show("projops");
        Assert.True(scenes.Save("drawing", "出图").Ok);
        Assert.False(scenes.Save("HistoryJanus", null).Ok);
        Assert.Equal(SceneSource.User, scenes.Find("出图")!.Source);

        scenes.Go("Janus");
        Assert.False(scenes.Reset("出图").Ok);
        Assert.True(scenes.Go("drawing").Ok);
        AssertVisible(host, "console", "mapping", "projops");

        host.UnregisterOwner("HistoryMinerva");
        Assert.True(scenes.Go("Janus").Ok);
        Assert.True(scenes.Go("drawing").Ok);
        AssertVisible(host, "console", "projops");
    });

    /// <summary>打开一页就在当前场景里打开，不切场景；落在中央区的页顶掉顶栏原来那一页。</summary>
    [Fact]
    public void OpeningAPageShowsItInTheCurrentScene() => RunScene((host, scenes, _) =>
    {
        scenes.Go("Minerva");
        Assert.True(scenes.Open("overview").Ok);

        Assert.Equal("HistoryMinerva", scenes.ActiveId);
        AssertVisible(host, "console", "overview");
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

    /// <summary>1.19.0 的场景设置照样读得进来：有页面集的记录还是另存场景，增补剔除丢掉不报错。</summary>
    [Fact]
    public void LegacySceneSettingsKeepSavedScenes() => RunScene((host, _, settings) =>
    {
        settings.Set(SceneManager.SettingsKey,
            """
            {"schemaVersion":1,"active":"drawing","scenes":{
              "drawing":{"title":"出图","pages":["HistoryJanus/projops"],"added":[],"removed":[]},
              "HistoryMinerva":{"added":["HistoryJanus/graph"],"removed":[],"resetPending":false}}}
            """);

        var legacy = new SceneManager(host, settings, new UsageLedger(settings, new NullLog()), new NullLog());
        Assert.Equal("drawing", legacy.ActiveId);
        Assert.Equal(SceneSource.User, legacy.Find("出图")!.Source);
        Assert.Equal(SceneSource.Derived, legacy.Find("Minerva")!.Source);
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
