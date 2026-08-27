using HistoryAurora.Shell.Selection;

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
    /// textbox 专用：跟随某个选择通道的某一列，写法 <c>&lt;通道&gt;.&lt;列&gt;</c>
    /// （REQ-UI-041，见 <see cref="HistoryAurora.Shell.Selection.SelectionChannels"/>）。
    ///
    /// 选中行一变就整体改写本框的内容——它的定位是「显示当前选中的那一个，顺便可以改成别的」，
    /// 不是「记住用户上次输入」。要一个不被覆盖的自由输入框，就别写这一项。
    /// </summary>
    public string? Follows { get; set; }

    /// <summary>
    /// textbox 专用：把本控件的当前值发布到这个**选择通道**，列名固定为 <c>value</c>
    /// （REQ-UI-045）。页面节点因此能按 <c>{selection.&lt;通道&gt;.value}</c> 引用它——
    /// 取数可以跟着它重取，<c>switch</c> 容器可以跟着它换一批组件。
    ///
    /// 与表格的 <c>channel</c> 是同一个台账、同一套规则：同名通道只认第一个声明方，
    /// 页面撤了通道跟着撤。方向相反而已——表格发布的是「选中了哪一行」，
    /// 这里发布的是「这个框现在是什么值」。
    ///
    /// **列名不接受声明**。允许自定义的话，引用侧的 <c>{selection.x.y}</c> 写错一个字
    /// 就是永远取不到值，而症状与「还没选中」完全一样。固定成 <c>value</c>，写法只有一种。
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>
    /// button 专用：动作 id。**这里不接受指令名**——写指令名正是 V1 会静默失效的原因。
    /// </summary>
    public string? Action { get; set; }

    /// <summary>
    /// button 专用：启用条件。目前只有 <c>{ "selected": "&lt;通道&gt;" }</c>——
    /// 该通道有选中行时按钮才可用（REQ-UI-041）。
    /// </summary>
    public PanelEnabledWhen? EnabledWhen { get; set; }

    /// <summary>
    /// button 专用：与**前一个控件同行**，而不是自己独占一行（REQ-UI-043）。
    /// 一行因此是「左标签 / 中控件 / 右按钮」三段。多个连续的同行按钮并排放在右侧。
    ///
    /// 缺省 false，与表格 <c>rowActions</c> 的 <c>inline</c> 相反。理由是已有面板：
    /// 默认改成 true 会让每一份既有声明的版面当场变样，而它们并没有要求过这件事。
    /// </summary>
    public bool Inline { get; set; }

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

/// <summary>
/// 按钮的启用条件（REQ-UI-041）。页面按钮的 <c>enabledWhen</c> 在 1.8.14 随 <c>button</c>
/// 节点一同退役，「选中一行 → 按钮变可用」这条链路因此断了一版；它在面板这一层回来，
/// 落点从**页内节点 id** 换成**界面级通道**，于是顺带能跨页。
/// </summary>
public sealed class PanelEnabledWhen
{
    /// <summary>该选择通道必须有选中行。</summary>
    public string? Selected { get; set; }
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
        var seenAnyWidget = false;
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
                    // follows 写错的症状是「框里永远空着」，与「还没选中」长得一样，
                    // 因此形状必须在收下声明时就判死，不留到运行期。
                    if (widget.Follows is { Length: > 0 } follows
                        && !SelectionChannels.TrySplitBinding(follows, out _, out _))
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的文本框 {widget.Id} 的 follows 必须写成 <通道>.<列>: {follows}");
                    // 通道名带点是常态（janus.section），因此只判空白与空格：
                    // 带空格的通道名在动作占位符 {selection.…} 里根本拆不出来。
                    if (widget.Channel is { } channel
                        && (string.IsNullOrWhiteSpace(channel) || channel.Contains(' ')))
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的文本框 {widget.Id} 的 channel 不能为空或含空格: {channel}");
                    if (widget.EnabledWhen != null)
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的文本框 {widget.Id} 不支持 enabledWhen；它只属于按钮");
                    if (widget.Inline)
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的文本框 {widget.Id} 不支持 inline；它只属于按钮");
                    break;

                case PanelWidgetKind.Button:
                    if (string.IsNullOrWhiteSpace(widget.Action))
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的按钮未声明 action；按钮不接受指令名，只能绑模块声明的动作");
                    if (widget.Follows != null)
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的按钮 {widget.Action} 不支持 follows；它只属于文本框");
                    if (widget.Channel != null)
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的按钮 {widget.Action} 不支持 channel；它只属于文本框");
                    if (widget.EnabledWhen is { } gate
                        && string.IsNullOrWhiteSpace(gate.Selected))
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的按钮 {widget.Action} 的 enabledWhen 只支持 selected=<通道>");
                    // 同行按钮要有个"同行"可跟。放在第一位的话它跟谁一行是没有答案的，
                    // 而症状会是"按钮自己占了一行"——与没写 inline 完全一样，查不出来。
                    if (widget.Inline && !seenAnyWidget)
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的按钮 {widget.Action} 声明了 inline，但它前面没有控件");
                    break;

                case PanelWidgetKind.Text:
                default:
                    if (widget.Follows != null || widget.EnabledWhen != null || widget.Channel != null)
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的说明文字不参与取值，不支持 follows / enabledWhen / channel");
                    if (widget.Inline)
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的说明文字不支持 inline；它只属于按钮");
                    break;
            }

            seenAnyWidget = true;
        }

        return new PanelDefinitionParse(definition, null);
    }
}
