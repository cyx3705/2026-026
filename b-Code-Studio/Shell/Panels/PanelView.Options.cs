using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using HistoryAurora.Shell.Selection;
using HistoryAurora.Shell.Widgets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Panels;

/// <summary>
/// 选择框的**动态候选**（REQ-UI-059）。
///
/// 为什么要有它：命令集的「域」「类」是两级联动下拉——域选了才能选类，
/// 类的取值范围随域收敛（DEC-021）。1.8.18 改描述式时这两个下拉整个消失了，
/// 记在案的理由是「联动下拉的候选是动态的，而控制面板的选项框只收静态候选」。
/// 那是一句实话：<c>options</c> 是写死在描述里的常量数组，表达不了「候选从一条指令来」，
/// 更表达不了「上一级变了这一级跟着换一批」。
///
/// 于是这一版把缺的那一块补上，而不是继续绕开它。补的形状与表格取数是**同一套**：
/// 一条只读指令加固定参数，参数值里可以写 <c>{selection.&lt;通道&gt;.&lt;列&gt;}</c>，
/// 通道一变就重取。两级联动因此不是一个专为命令集写的特例，
/// 而是「选择框的候选跟着某个通道走」这条通用能力的一次使用。
///
/// **它没有把控制面板变成脚本**：面板仍然只会「取一次候选」，不会按顺序做两件事，
/// 也不会决定拿到候选之后干什么（DEC-005）。
/// </summary>
public sealed partial class PanelView
{
    /// <summary>候选项行集里那一列的列名。与选择通道发布的列名同一个字，不接受声明。</summary>
    private const string OptionColumn = "value";

    /// <summary>取数参数里的通道引用。与表格取数用的是同一种写法。</summary>
    [GeneratedRegex(@"\{(selection\.[^{}\s]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex SelectionPlaceholder();

    /// <summary>一个候选项来自取数的选择框。</summary>
    private sealed class OptionFeed(
        string controlId,
        AuroraOptionBox box,
        ObservableCollection<string> options,
        PanelOptionsSource source,
        IReadOnlyList<string> channels,
        string? declaredValue)
    {
        public string ControlId { get; } = controlId;

        public AuroraOptionBox Box { get; } = box;

        /// <summary>
        /// 候选集合本身。**是同一个实例，不换新的**：
        /// 换实例的话，建控件时闭进 getter/setter 的那一份就成了旧的，
        /// 表现为「程序回写的值明明在候选里，却选不中」。
        /// </summary>
        public ObservableCollection<string> Options { get; } = options;

        public PanelOptionsSource Source { get; } = source;

        /// <summary>取数参数引用了哪几个通道。它们一变就要重取。</summary>
        public IReadOnlyList<string> Channels { get; } = channels;

        /// <summary>声明里写的初值；重取之后按它兜底。</summary>
        public string? DeclaredValue { get; } = declaredValue;
    }

    /// <summary>这条候选取数引用了哪几个选择通道。空表示它的候选与选中无关，只取一次。</summary>
    private static IReadOnlyList<string> ChannelsOf(PanelOptionsSource source)
        => (source.Args ?? [])
            .Values
            .SelectMany(value => SelectionPlaceholder().Matches(value ?? "")
                .Select(match => match.Groups[1].Value))
            .Select(reference => SelectionChannels.TrySplitReference(reference, out var channel, out _)
                ? channel
                : "")
            .Where(channel => channel.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 把重取排到下一拍。
    ///
    /// **不能同步取**。两处都会咬到：建面板时控件还没挂进可视树，
    /// 同步跑完的取数会先把候选填好，随后建控件那一步的初值又把选中项覆盖回去；
    /// 通道变化时这里正在 <c>Changed</c> 的处理器里，重取会再发一次 Publish，
    /// 于是一次选择在事件链上转两圈。
    /// </summary>
    private void QueueReload(OptionFeed feed)
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = ReloadAsync(feed)));

    private async Task ReloadAsync(OptionFeed feed)
    {
        var text = Compose(feed.Source);
        if (text == null)
        {
            // 引用的通道还没有值。**保持原样，不清空**：清空会让下拉在一瞬间变成空的，
            // 而「还没轮到我」和「这一级真的没有候选」在界面上必须看得出区别。
            return;
        }

        var values = await LoadAsync(text).ConfigureAwait(true);
        if (values == null)
            return;

        Apply(feed, values);
    }

    /// <summary>
    /// 组装取数指令。取不到通道值时返回 null——发一条参数为空的指令，
    /// 拿回来的要么是错误、要么是别的域的候选，两种都比「候选没变」糟。
    /// </summary>
    private string? Compose(PanelOptionsSource source)
    {
        var text = source.Command;
        var unresolved = false;

        foreach (var pair in source.Args ?? [])
        {
            var value = SelectionPlaceholder().Replace(pair.Value ?? "", match =>
            {
                // 与表格取数同一条口径：**判 null，不判空**。
                // 通道里那一格是空串，说明它就是个空值，照常取数。
                var resolved = _channels?.Resolve(match.Groups[1].Value);
                if (resolved != null)
                    return resolved;
                unresolved = true;
                return match.Value;
            });

            text += " " + pair.Key + "=" + CommandParser.QuoteArg(value);
        }

        return unresolved ? null : text;
    }

    private async Task<List<string>?> LoadAsync(string text)
    {
        CommandResult result;
        try
        {
            // 与表格取数同一条口径：候选是页面自己要的，不是用户下的指令，
            // 走安静通道不回显（见 PageRenderer.FetchAsync）。
            result = await _bus.InvokeAsync(text, "UI").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Log(ShellLogLevel.Warn, "panel", $"面板 {_definition.Id}: 取候选失败 {text}: {ex.Message}");
            return null;
        }

        if (!result.Success)
        {
            _log.Log(ShellLogLevel.Warn, "panel", $"面板 {_definition.Id}: 取候选失败 {text}: {result.Message}");
            return null;
        }

        if (!CommandResultData.TryGetJsonText(result.Data, result.Message, out var payload))
        {
            _log.Log(ShellLogLevel.Warn, "panel", $"面板 {_definition.Id}: 取候选没有合法 JSON 载荷: {text}");
            return null;
        }

        List<Dictionary<string, string>>? rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(payload, RowOptions);
        }
        catch (JsonException)
        {
            _log.Log(ShellLogLevel.Warn, "panel", $"面板 {_definition.Id}: 取候选返回的不是合法行集: {text}");
            return null;
        }

        if (rows == null)
            return null;

        return rows
            .Select(row => row.TryGetValue(OptionColumn, out var value) ? value ?? "" : "")
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static readonly JsonSerializerOptions RowOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 换一批候选，并尽量把选中项留在原处。
    ///
    /// 优先级：当前选中的那一项还在 → 留着；否则退到声明的初值；再否则第一项。
    /// **必须留**：域一变，「类」这一级会重取，而用户此时可能只是想换个域看看——
    /// 每次都把类跳回第一项，等于每换一次域就多点一次。
    /// </summary>
    private void Apply(OptionFeed feed, List<string> values)
    {
        var previous = feed.Box.SelectedItem as string;

        // 换候选是程序行为，不是用户选了什么。不压住提交动作的话，重取一次候选就等于
        // 替用户点了一下——模块那边会当成一次真实选择去改数据。
        WriteBack(() =>
        {
            feed.Options.Clear();
            foreach (var value in values)
                feed.Options.Add(value);

            if (values.Count == 0)
            {
                feed.Box.SelectedItem = null;
            }
            else
            {
                feed.Box.SelectedItem =
                    values.FirstOrDefault(value => value.Equals(previous, StringComparison.Ordinal))
                    ?? values.FirstOrDefault(value =>
                        value.Equals(feed.DeclaredValue, StringComparison.Ordinal))
                    ?? values[0];
            }
        });

        // 选中项可能因为换候选而变了，而这个框可能正是别人取数的依据（REQ-UI-045）。
        // 不补这一发，下一级会停在上一批候选算出来的那个值上。
        foreach (var (channel, controlId) in _publishers)
        {
            if (controlId.Equals(feed.ControlId, StringComparison.OrdinalIgnoreCase))
                PublishValue(channel, controlId);
        }
    }
}
