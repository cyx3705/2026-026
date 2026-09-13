using HistoryAurora.Shell.Neutral.CommandSurface;
using HistoryAurora.Shell.Neutral.Commands;
using HistoryAurora.Shell.Base.Docking;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;

namespace HistoryAurora.Shell.Components.Modules;

/// <summary>模块向 Aurora 注册内嵌界面的门面。5.0 起由 Aurora 自持。</summary>
internal interface IShellUiRegistrar
{
    bool IsUiThread { get; }

    void Invoke(Action action);

    IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner);

    void UnregisterToolWindow(string id);

    void UnregisterOwner(string owner);
}

/// <summary>工具窗口被激活时应接收输入焦点时实现。</summary>
internal interface IActivatableToolContent
{
    void ActivateContent();
}

/// <summary>命令工作台挂载点。5.0 宿主不再转发，由 Aurora 窗口自持。</summary>
internal interface IShellCommandWorkbenchHost
{
    CommandBus Bus { get; }

    CommandSelectionState CommandSelection { get; }

    ISettingsService Settings { get; }

    IShellLog Log { get; }

    string DataDirectory { get; }

    void AttachCommandCatalogSession(ICommandCatalogSession session);

    void ConfigureCommandCompletionRouting(Func<bool> isConsoleFocused, Action showCommandCatalog);

    void RefreshCommandCompletionFocus();
}
