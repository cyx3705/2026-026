namespace HistoryAurora.Shell.Panels;

/// <summary>
/// 控制面板声明（REQ-UI-008，协议 V2）。
///
/// V2 把小组件收成**三种**：文字、文本框、按钮。V1 的 combo / check / slider /
/// file / dir / number 一并删除，不留兼容分支——那批控件里真正被用到的只有输入与选择两类，
/// 其余各自带着一套校验规则，却从来没有第二个使用者。
/// 更要紧的是 V1 的按钮直接写指令模板：模块一改指令名，按钮就静默变哑。V2 的按钮只认
/// 动作 id，指令名归模块自己的声明管（见 <see cref="HistoryAurora.Shell.Actions.ActionRegistry"/>）。
/// </summary>
public sealed class PanelDefinition
{
    public required string Id { get; set; }

    public required string Title { get; set; }

    public bool Visible { get; set; } = true;

    public string Side { get; set; } = "right";

    public double Ratio { get; set; } = 0.22;

    /// <summary>小组件布局方向：vertical（默认）或 horizontal。</summary>
    public string Orientation { get; set; } = "vertical";

    /// <summary>面板内的小组件，按声明顺序排列。</summary>
    public List<PanelWidget> Widgets { get; set; } = new();
}

/// <summary>面板小组件的种类。这三种就是全部，不再有第四种。</summary>
public enum PanelWidgetKind
{
    /// <summary>一段说明文字，不参与取值。</summary>
    Text,

    /// <summary>文本框：<see cref="PanelWidget.Mode"/> 决定是输入还是选择。</summary>
    TextBox,

    /// <summary>按钮：绑一个由模块声明的动作。</summary>
    Button,
}

/// <summary>文本框的两种形态。</summary>
public enum PanelTextBoxMode
{
    /// <summary>自由输入。</summary>
    Input,

    /// <summary>在 <see cref="PanelWidget.Options"/> 里选一个。</summary>
    Select,
}

/// <summary>一个小组件。字段按种类取用，不适用的字段被忽略。</summary>
public sealed class PanelWidget
{
    /// <summary>种类：text / textbox / button。大小写不敏感；无法识别时整个面板作废。</summary>
    public required string Kind { get; set; }

    /// <summary>取值标识。文本框必须有，动作参数里的 <c>{id}</c> 就是指它。</summary>
    public string? Id { get; set; }

    /// <summary>左侧标签。文本框用；为空时退回 <see cref="Id"/>。</summary>
    public string? Label { get; set; }

    /// <summary>text 的正文，或 button 的按钮文字（不写时用动作声明的标题）。</summary>
    public string? Text { get; set; }

    /// <summary>textbox 专用：input（缺省）或 select。</summary>
    public string? Mode { get; set; }

    /// <summary>textbox 且 mode=select 时的候选项。</summary>
    public List<string>? Options { get; set; }

    /// <summary>初值。select 时若不在 <see cref="Options"/> 内则退回第一项。</summary>
    public string? Value { get; set; }

    /// <summary>必填。按钮执行前校验，空值直接拒绝并报出是哪一项。</summary>
    public bool Required { get; set; }

    /// <summary>
    /// button 专用：动作 id。**这里不接受指令名**——写指令名正是 V1 会静默失效的原因。
    /// </summary>
    public string? Action { get; set; }

    /// <summary>解析后的种类；无法识别时为 null。</summary>
    public PanelWidgetKind? ResolvedKind => (Kind ?? "").ToLowerInvariant() switch
    {
        "text" => PanelWidgetKind.Text,
        "textbox" => PanelWidgetKind.TextBox,
        "button" => PanelWidgetKind.Button,
        _ => null,
    };

    /// <summary>解析后的文本框形态；非 textbox 时无意义。</summary>
    public PanelTextBoxMode ResolvedMode
        => string.Equals(Mode, "select", StringComparison.OrdinalIgnoreCase)
            ? PanelTextBoxMode.Select
            : PanelTextBoxMode.Input;
}

/// <summary>面板声明的校验结果。</summary>
public readonly record struct PanelDefinitionParse(PanelDefinition? Value, string? Error)
{
    public bool Ok => Value != null;

    public static PanelDefinitionParse Fail(string reason) => new(null, reason);
}

public static class PanelDefinitionValidator
{
    /// <summary>
    /// 校验一份面板声明。任何一处不合规整份作废：面板是一组互相取值的控件，
    /// 少掉其中一个的话按钮拿到的参数就是错的——半个面板比没有面板更危险。
    /// </summary>
    public static PanelDefinitionParse Validate(PanelDefinition? definition)
    {
        if (definition == null)
            return PanelDefinitionParse.Fail("面板声明为 null");
        if (string.IsNullOrWhiteSpace(definition.Id))
            return PanelDefinitionParse.Fail("面板缺少 id");
        if (string.IsNullOrWhiteSpace(definition.Title))
            return PanelDefinitionParse.Fail($"面板 {definition.Id} 缺少标题");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var widget in definition.Widgets)
        {
            if (widget.ResolvedKind is not { } kind)
                return PanelDefinitionParse.Fail(
                    $"面板 {definition.Id} 含无法识别的小组件 {widget.Kind}；只支持 text / textbox / button");

            switch (kind)
            {
                case PanelWidgetKind.TextBox:
                    if (string.IsNullOrWhiteSpace(widget.Id))
                        return PanelDefinitionParse.Fail($"面板 {definition.Id} 的文本框缺少 id");
                    if (!ids.Add(widget.Id!))
                        return PanelDefinitionParse.Fail($"面板 {definition.Id} 的控件 id 重复: {widget.Id}");
                    if (widget.ResolvedMode == PanelTextBoxMode.Select
                        && (widget.Options == null || widget.Options.Count == 0))
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的选择框 {widget.Id} 未提供 options");
                    break;

                case PanelWidgetKind.Button:
                    if (string.IsNullOrWhiteSpace(widget.Action))
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的按钮未声明 action；按钮不接受指令名，只能绑模块声明的动作");
                    break;

                case PanelWidgetKind.Text:
                default:
                    break;
            }
        }

        return new PanelDefinitionParse(definition, null);
    }
}
