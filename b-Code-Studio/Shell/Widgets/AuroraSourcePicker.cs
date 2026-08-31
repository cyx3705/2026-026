using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace HistoryAurora.Shell.Widgets;

/// <summary>
/// Aurora 通用来源输入控件。它只负责输入与选择手势，业务命令由声明方注入。
/// </summary>
internal sealed class AuroraSourcePicker : TextBox
{
    public Func<Task<string?>>? SelectAsync { get; set; }

    public Func<string, Task>? CommitAsync { get; set; }

    private bool _selecting;
    private string? _committed;

    public AuroraSourcePicker()
    {
        AcceptsReturn = false;
        PreviewKeyDown += OnPreviewKeyDown;
        LostFocus += OnLostFocus;
        MouseDoubleClick += OnMouseDoubleClick;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        Commit(Text);
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        // 打开文件对话框时本框会失焦。若把当时的空值提交出去，模块命令失败，
        // 宿主还去抢控制台焦点——对话框一关，整窗就像死了。
        if (_selecting)
            return;
        Commit(Text);
    }

    private async void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _selecting = true;
        try
        {
            var selected = await (SelectAsync?.Invoke() ?? Task.FromResult<string?>(null));
            if (string.IsNullOrWhiteSpace(selected))
                return;

            // 文件对话框是嵌套消息泵。同一帧里改文字、提交、刷新表格会触发列宽空转。
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
            if (string.IsNullOrWhiteSpace(selected))
                return;

            Text = selected;
            Commit(Text);
        }
        finally
        {
            _selecting = false;
        }
    }

    private void Commit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (string.Equals(value, _committed, StringComparison.Ordinal))
            return;
        _committed = value;
        _ = CommitAsync?.Invoke(value);
    }
}
