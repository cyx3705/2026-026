using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Layout;
using HistoryAurora.Shell.Base.Docking;
using HistoryAurora.Shell.Components.Pages;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

[Collection(TestCollections.Ui)]
public sealed class SceneRegistrationContractTests
{
    [Fact]
    public void DeclarationCannotSeedAnotherModulesScene()
    {
        var result = PageDescriptionReader.Read("""
            {"schemaVersion":1,"owner":"HistoryJanus","pages":[
              {"id":"overview","title":"Janus","scene":"HistoryMinerva","content":{"type":"text"}}
            ]}
            """, "HistoryJanus");
        Assert.False(result.Ok);
    }

    [Fact]
    public void ReloadKeepsTheExactPaneAndHiddenState() => UiTestHost.RunSta(() =>
    {
        var manager = new DockingManager();
        var host = new DockingHost(manager, [], new Store(), new TestLog());
        host.Initialize();
        host.SetRegistrationScene("HistoryJanus");
        host.RegisterWindow(Page("overview", DockSide.Center), "HistoryJanus");
        host.RegisterWindow(Page("graph", DockSide.Left), "HistoryJanus");
        host.Hide("graph");
        var node = manager.Layout.Descendents().OfType<LayoutContent>().Single(n => n.ContentId == "overview");
        var pane = node.Parent;
        var root = manager.Layout;
        var old = node.Content;
        host.ReplaceWindow(Page("overview", DockSide.Right), "HistoryJanus");
        host.ReplaceWindow(Page("graph", DockSide.Bottom), "HistoryJanus");
        Assert.Same(root, manager.Layout);
        Assert.Same(pane, node.Parent);
        Assert.NotSame(old, node.Content);
        Assert.False(host.ListWindows().Single(w => w.Id == "graph").IsVisible);
        Assert.Equal(DockSide.Center, host.ListWindows().Single(w => w.Id == "overview").Side);
    });

    [Fact]
    public void RegistrationIsHiddenBeforeAnyDeferredSceneCallback() => UiTestHost.RunSta(() =>
    {
        var host = new DockingHost(new DockingManager(), [], new Store(), new TestLog());
        host.Initialize();
        host.SetRegistrationScene("HistoryMinerva");
        host.RegisterWindow(Page("mapping", DockSide.Center), "HistoryMinerva");
        host.RegisterWindow(Page("overview", DockSide.Right), "HistoryJanus");
        Assert.False(host.ListWindows().Single(w => w.Id == "overview").IsVisible);
        Assert.True(host.ListWindows().Single(w => w.Id == "mapping").IsVisible);
        Assert.Throws<InvalidOperationException>(() => host.ReplaceWindow(Page("mapping", DockSide.Left), "HistoryJanus"));
    });

    [Fact]
    public void RestartAndResetKeepTheUsersSceneBaseline() => UiTestHost.RunSta(() =>
    {
        var store = new Store();
        var host = new DockingHost(new DockingManager(), [], store, new TestLog());
        host.Initialize();
        host.SetRegistrationScene("HistoryJanus");
        host.RegisterWindow(Page("overview", DockSide.Center), "HistoryJanus");
        host.RegisterWindow(Page("graph", DockSide.Left), "HistoryJanus");
        host.Dock("graph", DockSide.Bottom, 0.32);
        host.Hide("graph");
        host.SaveLayout("HistoryJanus");
        var original = store.ReadNamed("HistoryJanus");
        store.WriteCurrent(original!);

        var restarted = new DockingHost(new DockingManager(), [], store, new TestLog());
        restarted.Initialize();
        restarted.SetRegistrationScene("HistoryJanus");
        restarted.RegisterWindow(Page("overview", DockSide.Right), "HistoryJanus");
        restarted.RegisterWindow(Page("graph", DockSide.Left), "HistoryJanus");
        restarted.ApplyScene("HistoryJanus", ["overview", "graph"], false);
        Assert.False(restarted.ListWindows().Single(w => w.Id == "graph").IsVisible);
        Assert.Equal(DockSide.Center, restarted.ListWindows().Single(w => w.Id == "overview").Side);
        restarted.Show("graph");
        restarted.SaveLayout("HistoryJanus");
        restarted.ApplyScene("HistoryJanus", ["overview", "graph"], true);
        Assert.False(restarted.ListWindows().Single(w => w.Id == "graph").IsVisible);
        Assert.Equal(original, store.ReadNamed("HistoryJanus.defaults"));
    });

    private static ToolWindowDescriptor Page(string id, DockSide side) => new()
    {
        Id = id, Title = id, DefaultSide = side, ContentFactory = () => new TextBlock { Text = id },
    };

    private sealed class Store : ILayoutStore
    {
        private string? _current;
        private readonly Dictionary<string, string> _named = new();
        public string? ReadCurrent() => _current;
        public void WriteCurrent(string payload) => _current = payload;
        public void DeleteCurrent() => _current = null;
        public string? ReadNamed(string name) => _named.GetValueOrDefault(name);
        public void WriteNamed(string name, string payload) => _named[name] = payload;
        public IReadOnlyList<string> ListNamed() => _named.Keys.ToList();
    }

    private sealed class TestLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
