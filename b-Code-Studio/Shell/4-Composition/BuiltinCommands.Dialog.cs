using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Base.Dialogs;

namespace HistoryAurora.Shell.Composition;

/// <summary>
/// 页面注册协议之外的弹窗命令。弹窗不是停靠页，不能进 <c>PageRenderer</c> 组件集；
/// 模块下一轮用本命令替换自己的 <c>Window</c>，本轮只把能力做出来。
/// </summary>
internal static partial class BuiltinCommands
{
    private static void RegisterDialog(CommandRegistry r, ShellCommandServices s)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "aurora.ui.dialog",
            Domain = "aurora",
            CommandClass = "ui",
            Summary = "显示 Aurora 主题化弹窗（message / confirm / prompt / choice / content）",
            Example = "aurora.ui.dialog kind=confirm title=确认 body=\"覆盖现有文件？\" danger=true",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "kind",
                    Description = "message / confirm / prompt / choice / content",
                    Position = 0,
                    AllowedValues = ["message", "confirm", "prompt", "choice", "content"],
                    Default = "message",
                },
                new ParameterSpec { Name = "title", Description = "标题" },
                new ParameterSpec { Name = "body", Description = "说明或摘要" },
                new ParameterSpec { Name = "content", Description = "content 种类的大段正文" },
                new ParameterSpec { Name = "value", Description = "prompt 种类的输入初值" },
                new ParameterSpec { Name = "options", Description = "choice 种类的 {label,value} JSON 列表" },
                new ParameterSpec { Name = "primary", Description = "主按钮文案" },
                new ParameterSpec { Name = "cancel", Description = "取消按钮文案" },
                new ParameterSpec { Name = "danger", Description = "主按钮用危险档", Type = ParamType.Bool, Default = "false" },
                new ParameterSpec { Name = "defaultcancel", Description = "Enter 落到取消", Type = ParamType.Bool, Default = "false" },
                new ParameterSpec { Name = "timeout", Description = "confirm 倒计时秒数，到期拒绝", Type = ParamType.Int },
            ],
            Handler = CommandDescriptor.Sync(context =>
            {
                var kindText = context.GetString("kind") ?? "message";
                if (!TryParseKind(kindText, out var kind))
                    return CommandResult.Fail($"不支持的 kind: {kindText}（message / confirm / prompt / choice / content）");

                IReadOnlyList<AuroraDialogChoice> choices = [];
                if (kind == AuroraDialogKind.Choice
                    && !AuroraDialogChoiceReader.TryRead(context.GetString("options"), out choices, out var choiceError))
                    return CommandResult.Fail(choiceError);

                var request = new AuroraDialogRequest
                {
                    Kind = kind,
                    Title = context.GetString("title") ?? "",
                    Body = context.GetString("body") ?? "",
                    Content = context.GetString("content"),
                    Value = context.GetString("value"),
                    Choices = choices,
                    PrimaryText = context.GetString("primary") ?? "确定",
                    CancelText = context.GetString("cancel") ?? "取消",
                    Danger = context.GetBool("danger"),
                    DefaultCancel = context.GetBool("defaultcancel"),
                    TimeoutSeconds = context.GetInt("timeout"),
                };

                var result = AuroraDialogWindow.Show(request, s.Window, s.Window.IsDarkTheme);
                if (result.TimedOut)
                    return CommandResult.Fail("已超时，按拒绝处理");
                if (!result.Accepted)
                    return CommandResult.Fail("已取消");
                return kind is AuroraDialogKind.Prompt or AuroraDialogKind.Choice
                    ? CommandResult.Ok(result.Input ?? "", result.Input)
                    : CommandResult.Ok("已确认");
            }),
        });
    }

    private static bool TryParseKind(string text, out AuroraDialogKind kind)
    {
        if (text.Equals("message", StringComparison.OrdinalIgnoreCase))
        {
            kind = AuroraDialogKind.Message;
            return true;
        }

        if (text.Equals("confirm", StringComparison.OrdinalIgnoreCase))
        {
            kind = AuroraDialogKind.Confirm;
            return true;
        }

        if (text.Equals("prompt", StringComparison.OrdinalIgnoreCase))
        {
            kind = AuroraDialogKind.Prompt;
            return true;
        }

        if (text.Equals("choice", StringComparison.OrdinalIgnoreCase))
        {
            kind = AuroraDialogKind.Choice;
            return true;
        }

        if (text.Equals("content", StringComparison.OrdinalIgnoreCase))
        {
            kind = AuroraDialogKind.Content;
            return true;
        }

        kind = default;
        return false;
    }
}
