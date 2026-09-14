using System.Windows.Controls;
using HistoryAurora.Shell.Base.Docking;
using HistoryAurora.Shell.Components.Modules;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

[Collection(TestCollections.Ui)]
public sealed class UiAnnotationClaimerTests
{
    [Fact]
    public void Claim_DocksLiveObjectFromAnnotatedCommand()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            var pane = new TextBlock { Text = "live" };
            registry.Register(new CommandDescriptor
            {
                Name = "demo.ui.pane",
                Domain = "demo",
                CommandClass = "ui",
                Summary = "演示窗格",
                Readonly = true,
                Annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [UiAnnotationKeys.Window] = "demo.pane",
                    [UiAnnotationKeys.Side] = "left",
                    [UiAnnotationKeys.Title] = "演示",
                    [UiAnnotationKeys.Ratio] = "0.3",
                },
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok", pane)),
            }, "module:HistoryDemo");

            var log = new MemoryLog();
            var docking = new RecordingDocking();
            var claimer = new UiAnnotationClaimer(new CommandBus(registry, log), docking, log);
            var count = claimer.ClaimAsync().GetAwaiter().GetResult();

            Assert.Equal(1, count);
            var registered = Assert.Single(docking.Registered);
            Assert.Equal("demo.pane", registered.Descriptor.Id);
            Assert.Equal("演示", registered.Descriptor.Title);
            Assert.Equal(DockSide.Left, registered.Descriptor.DefaultSide);
            Assert.Equal(0.3, registered.Descriptor.DefaultRatio);
            Assert.Equal(UiAnnotationClaimer.OwnerPrefix + "HistoryDemo", registered.Owner);
            Assert.Same(pane, registered.Descriptor.ContentFactory?.Invoke());
        });
    }

    [Fact]
    public void Claim_ReclaimDropsPreviousOwnerWindows()
    {
        UiTestHost.RunSta(() =>
        {
            var registry = new CommandRegistry();
            registry.Register(new CommandDescriptor
            {
                Name = "demo.ui.pane",
                Domain = "demo",
                CommandClass = "ui",
                Summary = "演示窗格",
                Readonly = true,
                Annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [UiAnnotationKeys.Window] = "demo.pane",
                },
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok", new TextBlock())),
            }, "module:HistoryDemo");

            var log = new MemoryLog();
            var docking = new RecordingDocking();
            var claimer = new UiAnnotationClaimer(new CommandBus(registry, log), docking, log);
            claimer.ClaimAsync().GetAwaiter().GetResult();
            claimer.ClaimAsync().GetAwaiter().GetResult();

            Assert.Contains(UiAnnotationClaimer.OwnerPrefix + "HistoryDemo", docking.Dropped);
            Assert.Equal(2, docking.Registered.Count);
        });
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
        public void Log(ShellLogLevel level, string category, string message) { }

        public event EventHandler<ShellLogEntry>? EntryAdded
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
