using HistoryAurora.Shell.Docking;
using HistoryAurora.Shell.Views;

namespace HistoryAurora.Shell;

internal partial class ShellWindow
{
    private void RegisterComponentGallery()
    {
        if (_docking.ListWindows().Any(window =>
                window.Id.Equals(StandardWindowIds.Components, StringComparison.OrdinalIgnoreCase)))
        {
            _docking.Show(StandardWindowIds.Components);
            return;
        }

        _docking.RegisterWindow(
            new ToolWindowDescriptor
            {
                Id = StandardWindowIds.Components,
                Title = "组件测试",
                DefaultSide = DockSide.Center,
                DefaultRatio = 0.8,
                DefaultVisible = true,
                IsSingleton = true,
                ContentFactory = () => new ComponentGalleryView(
                    _bus,
                    _log,
                    _actions,
                    _catalog.CompleteAsync),
            },
            "HistoryAurora");
        _docking.Show(StandardWindowIds.Components);
    }
}
