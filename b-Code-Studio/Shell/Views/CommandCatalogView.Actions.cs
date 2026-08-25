using System.Windows;
using HistoryAurora.Shell.Table;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Views;

/// <summary>命令集的行操作（REQ-UI-011）：一份声明，行内按钮与右键菜单两个出口。</summary>
internal sealed partial class CommandCatalogView
{
    private const string DetailAction = "detail";
    private const string PrefillAction = "prefill";
    private const string CopyAction = "copy";
    private const string RunAction = "run";

    /// <summary>行里第一列是指令名，第三列是只读标记（列键由 FromItems 按顺序生成）。</summary>
    private const string NameKey = "c0";

    private const string ReadonlyKey = "c2";

    private void ConfigureRowActions()
    {
        _table.SetRowActions([
            new AuroraRowAction(DetailAction, "详情", Summary: "在指令详情页展开这条指令"),
            new AuroraRowAction(PrefillAction, "填入", Summary: "把指令名填进控制台输入框，不执行"),
            new AuroraRowAction(CopyAction, "复制指令名", Inline: false),
            new AuroraRowAction(RunAction, "运行（仅只读指令）", Inline: false),
        ]);
        _table.RowActionInvoked += OnRowAction;
        _table.SelectionChanged += (_, _) => Publish(_table.SelectedRow);
    }

    private void Publish(IReadOnlyDictionary<string, string>? row)
    {
        if (row == null || !row.TryGetValue(NameKey, out var name) || string.IsNullOrWhiteSpace(name))
            return;
        _catalog.Select(name);
        _selection.CurrentCommandName = name;
    }

    private void OnRowAction(object? sender, AuroraRowActionEventArgs e)
    {
        if (!e.Row.TryGetValue(NameKey, out var name) || string.IsNullOrWhiteSpace(name))
            return;

        _catalog.Select(name);
        _selection.CurrentCommandName = name;

        switch (e.Action.Id)
        {
            case DetailAction:
                _ = _bus.ExecuteAsync("aurora.ui.show name=commanddetail", "UI");
                return;

            case PrefillAction:
                Prefill(name);
                return;

            case CopyAction:
                TryCopy(name);
                return;

            case RunAction:
                Run(name, e.Row);
                return;

            default:
                return;
        }
    }

    private void Prefill(string name)
        => _ = _bus.ExecuteAsync("aurora.log.prefill text=" + CommandParser.QuoteArg(name), "UI");

    /// <summary>
    /// 只读指令直接跑；其余一律只填进输入框。
    /// 在目录里一键执行一条会改东西的指令——而且往往是在"我只是想看看它干什么"的时候——
    /// 是这一页最容易出事故的地方。
    /// </summary>
    private void Run(string name, IReadOnlyDictionary<string, string> row)
    {
        if (row.TryGetValue(ReadonlyKey, out var flag) && flag == "是")
        {
            _ = _bus.ExecuteAsync(name, "UI");
            return;
        }

        Prefill(name);
        _log.Log(ShellLogLevel.Info, "catalog", name + " 不是只读指令，已填入控制台待确认，未执行");
    }

    private void TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // 剪贴板被别的进程占用是常态，不该让整页崩掉。
            _log.Log(ShellLogLevel.Warn, "catalog", "复制失败: " + ex.Message);
        }
    }
}
