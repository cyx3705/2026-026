using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HistoryAurora.Shell.Widgets;

/// <summary>
/// Aurora 通用来源输入控件。它只负责输入与选择手势，业务命令由声明方注入。
/// </summary>
internal sealed class AuroraSourcePicker : TextBox
{
    public Func<Task<string?>>? SelectAsync { get; set; }

    public Func<string, Task>? CommitAsync { get; set; }

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
        _ = CommitAsync?.Invoke(Text);
    }

    private void OnLostFocus(object sender, RoutedEventArgs e)
        => _ = CommitAsync?.Invoke(Text);

    private async void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var selected = await (SelectAsync?.Invoke() ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(selected))
            return;

        Text = selected;
        await (CommitAsync?.Invoke(Text) ?? Task.CompletedTask);
    }
}
