using AvalonDock.Layout;

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

    /// <summary>
    /// 只用于页面声明（<c>side=tab tabTarget=…</c>）：与目标页同一个位置。
    /// 1.20.2 起没有标签组、一格一页（REQ-UI-100），<c>aurora.ui.dock</c> 也不再接受 <c>pos=tab</c>。
    /// </summary>
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

/// <summary>
/// 中央区的文档窗格。与 <see cref="AvalonDock.Layout.LayoutDocumentPane"/> 的唯一区别，
/// 是它**认得工具页的下标**（REQ-UI-083）。
///
/// 中央区里的页全是 <c>LayoutAnchorable</c>（1.20.2 起命令集也不再是 <c>LayoutDocument</c>）。而
/// <c>LayoutDocumentPane</c> 的 <c>ILayoutContentSelector.IndexOf</c> 只认前者，
/// 对后者一律返回 -1。这一个 -1 顺着 AvalonDock 的实现扩散成两个用户可见的故障：
///
///   * **换不了页**：<c>LayoutContent.IsSelected</c> 的 setter 回写
///     <c>Parent.SelectedContentIndex = Parent.IndexOf(this)</c>，写进去的是 -1，
///     窗格于是「什么都不选」——页签照画、内容整片空白，而 <c>aurora.ui.show</c>
///     还会报成功；
///   * **拖不动**：AvalonDock 自己那条页签拖拽同样按这个下标记住来处
///     （<c>PreviousContainerIndex</c>），-1 让整条拖拽起不来。
///     真机症状是「Janus 的项目总览拖不动，旁边的命令集拖得动」（2026-09-07）。
///
/// 派生类重新实现该接口，接口映射就指向这里——一处改对，换页、拖动、快照恢复
/// 三条路径同时正确，不必在手势层拦截，也不必把中央区拆成两块。
/// **Aurora 建的每一个中央文档窗格都必须是这个类型。**
/// </summary>
internal sealed class CenterDocumentPane : LayoutDocumentPane, ILayoutContentSelector
{
    public CenterDocumentPane()
    {
    }

    public CenterDocumentPane(LayoutContent firstChild)
        : base(firstChild)
    {
    }

    int ILayoutContentSelector.IndexOf(LayoutContent content) => Children.IndexOf(content);
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
