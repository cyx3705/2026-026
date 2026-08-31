using System.Text.Json;

namespace HistoryAurora.Shell;

/// <summary>
/// 弹窗种类。覆盖前端独立后坏掉的两类模块对话框，以及 Aurora 自己的确认/关于框：
/// <list type="bullet">
///   <item><see cref="Message"/>：一段说明 + 关闭（关于）</item>
///   <item><see cref="Confirm"/>：确认/取消，可倒计时、可标危险</item>
///   <item><see cref="Prompt"/>：带输入的确认（如填写恢复提交说明）</item>
///   <item><see cref="Choice"/>：从一组 label/value 候选中选择一项</item>
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
    Choice,
    Content,
}

/// <summary>选择弹窗中的一项；界面显示 <see cref="Label"/>，结果返回 <see cref="Value"/>。</summary>
public sealed class AuroraDialogChoice
{
    public string Label { get; init; } = "";

    public string Value { get; init; } = "";
}

/// <summary>选择项 JSON 的统一解析入口，命令与合同测试共用同一套严格校验。</summary>
public static class AuroraDialogChoiceReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static bool TryRead(string? json, out IReadOnlyList<AuroraDialogChoice> choices, out string error)
    {
        choices = [];
        error = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "choice 弹窗必须提供 options";
            return false;
        }

        List<AuroraDialogChoice>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<AuroraDialogChoice>>(json, Options);
        }
        catch (JsonException ex)
        {
            error = "options 不是合法的 {label,value} JSON 列表: " + ex.Message;
            return false;
        }

        if (parsed is not { Count: > 0 })
        {
            error = "choice 弹窗的 options 不能为空";
            return false;
        }

        var invalid = parsed.FindIndex(item =>
            item == null || string.IsNullOrWhiteSpace(item.Label) || string.IsNullOrWhiteSpace(item.Value));
        if (invalid >= 0)
        {
            error = $"options[{invalid}] 必须同时提供非空 label 和 value";
            return false;
        }

        choices = parsed;
        return true;
    }
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

    /// <summary>choice 种类的候选项；其它种类忽略。</summary>
    public IReadOnlyList<AuroraDialogChoice> Choices { get; init; } = [];

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

/// <summary>
/// 一次弹窗的结果。<see cref="Input"/> 对 prompt 是输入文字，对 choice 是所选 value。
/// 超时与点取消都是未接受，用 <see cref="TimedOut"/> 区分。
/// </summary>
public readonly record struct AuroraDialogResult(bool Accepted, bool TimedOut, string? Input)
{
    public static AuroraDialogResult Rejected { get; } = new(false, false, null);
}
