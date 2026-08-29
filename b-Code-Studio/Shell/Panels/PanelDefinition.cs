using HistoryAurora.Shell.Selection;

namespace HistoryAurora.Shell.Panels;

/// <summary>
/// 控制面板声明（REQ-UI-008，协议 V3）。
///
/// V2 把小组件收成三种——文字、文本框、按钮——这一条 V3 原样保留。
/// V3 改的是**版面**：小组件从一个平铺数组换成 <see cref="Rows"/>，行成为一等结构。
///
/// 为什么必须是一等结构（REQ-UI-060）。V2 有两套互不相干的排版，由 <c>orientation</c>
/// 二选一：竖排是一个三列共享的 Grid，每个控件独占一行、行行对齐得像张表；
/// 横排靠统一列宽自动换行。两套都表达不了「这三个是一行、那一个自己一行」——
/// 竖排里行是「一个控件加几个 inline 按钮」的副产品，横排里行是宽度算出来的结果。
/// 于是「第几行是什么样」这件事，声明侧根本没有地方写。
///
/// V3 只有一套排版：行由声明给出，行内元素按最窄宽度排；一行放得下就不换行，
/// 放不下才折成多个视觉行——**而折出来的仍然是同一行**，行与行之间那条横线不会因此多一条。
///
/// 一并退役的两个字段，各有各的账：
/// <list type="bullet">
///   <item><c>orientation</c>：只有一套排版之后它没有第二个取值可选；</item>
///   <item><c>required</c>：校验是**全局**的——任何一个必填框为空，面板上**每个**按钮
///         都拒绝执行。Janus 因此不敢用它（注释在案），Mercury 三个数字框全写了 required
///         却共用同一批按钮，等于把整块面板锁在「三个都填了」上。
///         参数缺失交给各条指令自己的 <c>Required</c> 去报，报出来的还是那条指令的话。</item>
/// </list>
/// </summary>
public sealed class PanelDefinition
{
    public required string Id { get; set; }

    public required string Title { get; set; }

    /// <summary>
    /// 以下三项只在面板作为**独立可停靠窗口**时有效（JSON 配置与 <c>ShellConfig.Panels</c>）。
    /// 页面里内嵌的 <c>panel</c> 节点不读它们——那一层的位置由页面描述的 placement 决定。
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <inheritdoc cref="Visible"/>
    public string Side { get; set; } = "right";

    /// <inheritdoc cref="Visible"/>
    public double Ratio { get; set; } = 0.22;

    /// <summary>面板的行，按声明顺序自上而下。</summary>
    public List<PanelRow> Rows { get; set; } = new();

    /// <summary>全部小组件，按行内顺序摊平。取值与遍历都用它。</summary>
    public IEnumerable<PanelWidget> AllWidgets => Rows.SelectMany(row => row.Widgets);
}

/// <summary>一行里的余量怎么分。</summary>
public enum PanelRowMode
{
    /// <summary>
    /// 可变宽度（缺省）：除一个可变元素外，其余各自停在最窄宽度，余量全给那一个。
    /// 可变元素由 <see cref="PanelWidget.Flex"/> 指定；没有谁声明时是**最右边**那一个。
    /// </summary>
    Flex,

    /// <summary>均布：余量按各元素的最窄宽度**等比放大**，宽的还是宽、窄的还是窄。</summary>
    Even,
}

/// <summary>面板里的一行。</summary>
public sealed class PanelRow
{
    /// <summary>flex（缺省）或 even。大小写不敏感；无法识别时整个面板作废。</summary>
    public string? Mode { get; set; }

    public List<PanelWidget> Widgets { get; set; } = new();

    /// <summary>解析后的分配方式；无法识别时为 null。</summary>
    public PanelRowMode? ResolvedMode => (Mode ?? "").ToLowerInvariant() switch
    {
        "" or "flex" => PanelRowMode.Flex,
        "even" => PanelRowMode.Even,
        _ => null,
    };
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

    /// <summary>在候选里选一个。</summary>
    Select,
}

/// <summary>候选项取数（REQ-UI-059）：候选是活的，由一条只读指令给。</summary>
public sealed class PanelOptionsSource
{
    /// <summary>只读指令名。返回的行集里，列名 <c>value</c> 那一格就是一个候选项。</summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// 固定参数。值里可以写 <c>{selection.&lt;通道&gt;.&lt;列&gt;}</c>：
    /// 通道一变就重取一次候选，两级联动下拉（域 → 类）因此成立。
    /// </summary>
    public Dictionary<string, string>? Args { get; set; }
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

    /// <summary>textbox 且 mode=select 时的候选项。与 <see cref="OptionsSource"/> 二选一。</summary>
    public List<string>? Options { get; set; }

    /// <summary>
    /// textbox 且 mode=select 时的**动态**候选项（REQ-UI-059）。
    ///
    /// 与 <see cref="Options"/> 二选一：两个都写的话，界面上看到的那一份取决于取数回来的时机，
    /// 而两次打开可能不一样——因此校验直接拒绝，不去定义谁盖过谁。
    /// </summary>
    public PanelOptionsSource? OptionsSource { get; set; }

    /// <summary>初值。select 时若不在候选内则退回第一项。</summary>
    public string? Value { get; set; }

    /// <summary>
    /// 本元素的**最窄宽度**（像素，REQ-UI-060）。
    ///
    /// 不写时由组件按内容量：按钮和说明文字的字宽就是它们的最窄宽度，不必注册；
    /// 文本框量不出来——空框的内容宽度是 0——因此不写时按
    /// <see cref="HistoryAurora.Shell.Widgets.AuroraPanelBoard.DefaultInputMinWidth"/> 算。
    /// 写了就以写的为准。
    /// </summary>
    public double? MinWidth { get; set; }

    /// <summary>
    /// 本行的可变宽度元素（REQ-UI-060）。一行最多一个：
    /// 写了两个只认第一个，后面的按最窄宽度处理——拒绝整份声明太重，
    /// 而「两个都变宽」没有一种分法说得出道理。
    /// 一个都没写时，可变的是这一行**最右边**那个元素。
    /// <see cref="PanelRowMode.Even"/> 的行忽略本项。
    /// </summary>
    public bool Flex { get; set; }

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
        foreach (var row in definition.Rows)
        {
            if (row == null)
                return PanelDefinitionParse.Fail($"面板 {definition.Id} 含空行");
            if (row.ResolvedMode is null)
                return PanelDefinitionParse.Fail(
                    $"面板 {definition.Id} 的行声明了无法识别的 mode={row.Mode}；只支持 flex / even");
            if (row.Widgets.Count == 0)
                return PanelDefinitionParse.Fail($"面板 {definition.Id} 含没有小组件的空行");

            foreach (var widget in row.Widgets)
            {
                if (Validate(definition, widget, ids) is { } failure)
                    return failure;
            }
        }

        return new PanelDefinitionParse(definition, null);
    }

    private static PanelDefinitionParse? Validate(
        PanelDefinition definition,
        PanelWidget widget,
        HashSet<string> ids)
    {
        if (widget.ResolvedKind is not { } kind)
            return PanelDefinitionParse.Fail(
                $"面板 {definition.Id} 含无法识别的小组件 {widget.Kind}；只支持 text / textbox / button");

        // 最窄宽度是像素，负数与 0 都排不出东西来；这类值写错的症状是「那一格没了」。
        if (widget.MinWidth is { } min && (double.IsNaN(min) || min <= 0))
            return PanelDefinitionParse.Fail(
                $"面板 {definition.Id} 的小组件 {widget.Id ?? widget.Kind} 的 minWidth 必须为正数: {min}");

        switch (kind)
        {
            case PanelWidgetKind.TextBox:
                if (string.IsNullOrWhiteSpace(widget.Id))
                    return PanelDefinitionParse.Fail($"面板 {definition.Id} 的文本框缺少 id");
                if (!ids.Add(widget.Id!))
                    return PanelDefinitionParse.Fail($"面板 {definition.Id} 的控件 id 重复: {widget.Id}");
                if (widget.ResolvedMode == PanelTextBoxMode.Select)
                {
                    var hasStatic = widget.Options is { Count: > 0 };
                    var hasSource = !string.IsNullOrWhiteSpace(widget.OptionsSource?.Command);
                    if (!hasStatic && !hasSource)
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的选择框 {widget.Id} 未提供 options 或 optionsSource");
                    if (hasStatic && hasSource)
                        return PanelDefinitionParse.Fail(
                            $"面板 {definition.Id} 的选择框 {widget.Id} 同时写了 options 与 optionsSource；只能二选一");
                }
                else if (widget.OptionsSource != null || widget.Options != null)
                {
                    return PanelDefinitionParse.Fail(
                        $"面板 {definition.Id} 的输入框 {widget.Id} 不支持 options / optionsSource；它们只属于 mode=select");
                }

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
                if (widget.Options != null || widget.OptionsSource != null)
                    return PanelDefinitionParse.Fail(
                        $"面板 {definition.Id} 的按钮 {widget.Action} 不支持 options / optionsSource；它们只属于文本框");
                if (widget.EnabledWhen is { } gate && string.IsNullOrWhiteSpace(gate.Selected))
                    return PanelDefinitionParse.Fail(
                        $"面板 {definition.Id} 的按钮 {widget.Action} 的 enabledWhen 只支持 selected=<通道>");
                break;

            case PanelWidgetKind.Text:
            default:
                if (widget.Follows != null || widget.EnabledWhen != null || widget.Channel != null)
                    return PanelDefinitionParse.Fail(
                        $"面板 {definition.Id} 的说明文字不参与取值，不支持 follows / enabledWhen / channel");
                break;
        }

        return null;
    }
}
