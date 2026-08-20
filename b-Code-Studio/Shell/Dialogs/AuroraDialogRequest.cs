namespace HistoryAurora.Shell;

/// <summary>
/// 弹窗种类。覆盖前端独立后坏掉的两类模块对话框，以及 Aurora 自己的确认/关于框：
/// <list type="bullet">
///   <item><see cref="Message"/>：一段说明 + 关闭（关于）</item>
///   <item><see cref="Confirm"/>：确认/取消，可倒计时、可标危险</item>
///   <item><see cref="Prompt"/>：带输入的确认（如填写恢复提交说明）</item>
///   <item><see cref="Content"/>：大段只读等宽正文（如历史预览）</item>
/// </list>
/// 模块本轮不要自己 <c>new Window</c>；下一轮把现有对话框改走
/// <c>aurora.ui.dialog</c> 或本类型。
/// </summary>
public enum AuroraDialogKind
{
    Message,
    Confirm,
    Prompt,
    Content,
}

/// <summary>一次弹窗的数据。不含任何样式键或颜色——外观由 Aurora 决定（DEC-005）。</summary>
public sealed class AuroraDialogRequest
{
    public AuroraDialogKind Kind { get; init; } = AuroraDialogKind.Message;

    public string Title { get; init; } = "";

    /// <summary>主文案。confirm / message / prompt 的说明，content 的摘要。</summary>
    public string Body { get; init; } = "";

    /// <summary>content 种类的大段正文；其它种类忽略。</summary>
    public string? Content { get; init; }

    /// <summary>prompt 种类的输入初值；其它种类忽略。</summary>
    public string? Value { get; init; }

    public string PrimaryText { get; init; } = "确定";

    public string CancelText { get; init; } = "取消";

    /// <summary>主按钮用危险档（删除、允许远程执行等）。</summary>
    public bool Danger { get; init; }

    /// <summary>Enter 落到取消而不是主按钮。危险确认应当如此。</summary>
    public bool DefaultCancel { get; init; }

    /// <summary>
    /// 秒。大于 0 时显示倒计时，到期视为拒绝。
    /// 只对 <see cref="AuroraDialogKind.Confirm"/> 生效。
    /// </summary>
    public int TimeoutSeconds { get; init; }
}

/// <summary>一次弹窗的结果。超时与点取消都是未接受，用 <see cref="TimedOut"/> 区分。</summary>
public readonly record struct AuroraDialogResult(bool Accepted, bool TimedOut, string? Input)
{
    public static AuroraDialogResult Rejected { get; } = new(false, false, null);
}
