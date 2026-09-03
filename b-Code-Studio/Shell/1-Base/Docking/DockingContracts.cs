namespace HistoryAurora.Shell.Base.Docking;

/// <summary>某个工具窗口的当前状态快照。</summary>
internal sealed record ToolWindowInfo(
    string Id,
    string Title,
    bool IsVisible,
    bool IsFloating,
    DockSide? Side,
    double? Ratio,
    string Owner = "framework");

/// <summary>停靠系统对外门面。5.0 起由 Aurora 自持，不再来自宿主 Core。</summary>
internal interface IDockingService
{
    string? MaximizedId { get; }

    IReadOnlyList<ToolWindowInfo> ListWindows();

    void Show(string id);

    void Hide(string id);

    void Float(string id);

    void Dock(string id, DockSide side, double? ratio = null, string? targetId = null);

    void SetRatio(string id, double ratio);

    void ResetWindow(string id);

    void ResetLayout();

    void SaveLayout(string name);

    bool LoadLayout(string name);

    IReadOnlyList<string> ListLayouts();

    void RegisterWindow(ToolWindowDescriptor descriptor, string owner);

    void UnregisterWindow(string id);

    void UnregisterOwner(string owner);

    void MaximizeWindow(string id);

    void RestoreLayoutFromMaximized();

    event EventHandler<ShellCommandEventArgs>? CommandGenerated;

    event EventHandler? WindowsChanged;
}

internal enum DockSide
{
    Left = 0,
    Right = 1,
    Top = 2,
    Bottom = 3,
    Tab = 4,
    Center = 5,
}

internal sealed class ToolWindowDescriptor
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public DockSide DefaultSide { get; init; } = DockSide.Right;

    public double DefaultRatio { get; init; } = 0.25;

    public string? DefaultTabTarget { get; init; }

    public bool DefaultVisible { get; init; } = true;

    public bool IsSingleton { get; init; } = true;

    public Func<object>? ContentFactory { get; init; }
}

internal static class StandardWindowIds
{
    public const string Console = "console";
    public const string Mcp = "mcp";
    public const string CommandDetail = "commanddetail";
    public const string Modules = "modules";
    public const string Components = "components";
}

internal sealed class ShellCommandEventArgs : EventArgs
{
    public required string CommandText { get; init; }

    public required string Source { get; init; }
}

/// <summary>布局文件存取。5.0 起从宿主 Core 迁入 Aurora。</summary>
internal interface ILayoutStore
{
    string? ReadCurrent();

    void WriteCurrent(string payload);

    void DeleteCurrent();

    string? ReadNamed(string name);

    void WriteNamed(string name, string payload);

    IReadOnlyList<string> ListNamed();
}
