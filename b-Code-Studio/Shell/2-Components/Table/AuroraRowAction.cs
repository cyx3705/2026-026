namespace HistoryAurora.Shell.Components.Table;

/// <summary>
/// 表格的一条行操作（REQ-UI-011）。
///
/// 一条声明同时供两个出口使用：行内按钮与右键菜单。这不是两个组件，
/// 而是同一件事的两种到达方式——把它们拆成两份声明，参数与可用条件迟早只在其中一份里被改。
///
/// 与页面里的按钮一样，落点是**动作 id**，不是指令名：指令改名由模块自己的
/// <c>&lt;域&gt;.ui.actions</c> 吸收，表格这边一个字不动（REQ-UI-009）。
/// </summary>
/// <param name="Id">动作 id；点击时由 <c>ActionRegistry</c> 现取声明。</param>
/// <param name="Title">按钮与菜单项上的文字。</param>
/// <param name="Danger">危险动作：行内按钮转危险色，菜单项同样。</param>
/// <param name="Inline">
/// 是否在行内也放一个按钮。false 表示只进右键菜单——行操作多于两三条时，
/// 全塞进行内会把数据列挤没，那正是本组件要解决的问题的反面。
/// </param>
/// <param name="Summary">悬停说明；为空时按 id 提示。</param>
public sealed record AuroraRowAction(
    string Id,
    string Title,
    bool Danger = false,
    bool Inline = true,
    string? Summary = null);

/// <summary>行操作被触发：带上被操作的那一行。</summary>
public sealed class AuroraRowActionEventArgs(AuroraRowAction action, IReadOnlyDictionary<string, string> row)
    : EventArgs
{
    public AuroraRowAction Action { get; } = action;

    /// <summary>被操作的行。**总是**触发时那一行，与"当前选中行"无关——
    /// 行内按钮点的是它所在的行，右键菜单点的是指针底下那一行。</summary>
    public IReadOnlyDictionary<string, string> Row { get; } = row;
}
