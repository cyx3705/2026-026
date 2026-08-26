using HistoryAurora.Shell.Docking;
using HistoryAurora.Shell.Views;

namespace HistoryAurora.Shell;

internal partial class ShellWindow
{
    private void RegisterComponentGallery()
    {
        // 指令与动作先于页面登记。页面渲染时就要按 id 解析动作、Loaded 之后立刻取数,
        // 顺序反了的症状是「按钮全是未声明的动作」加一条「未知指令」,而不是报错。
        Views.ComponentGalleryCommands.Register(_bus.Registry);
        _actions.DeclareLocal(
            Views.ComponentGalleryCommands.Owner,
            Views.ComponentGalleryCommands.Actions);

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
                    _catalog.CompleteAsync,
                    _channels,
                    _dataRefresher),
            },
            "HistoryAurora");
        _docking.Show(StandardWindowIds.Components);
    }
}
