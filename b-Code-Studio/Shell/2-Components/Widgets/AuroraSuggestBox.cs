using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using HistoryAurora.Shell.Neutral.CommandSurface;
using HistoryAurora.Shell.Components.Themes;

namespace HistoryAurora.Shell.Components.Widgets;

/// <summary>
/// 带候选的输入框（REQ-UI-013）。
///
/// 为什么要有它：加一条常驻指令时，指令全名只能手打，打错要等到执行时才知道
/// （见 b-Office/history/前端组件缺失需求书 C4）。候选来自与控制台补全**同一个**会话，
/// 因此"控制台里补得出来、面板里补不出来"这种不一致不会发生。
///
/// 组件只管两件事：什么时候弹、弹出来长什么样。取候选的口径由调用方注入。
/// </summary>
internal sealed class AuroraSuggestBox : UserControl
{
    private readonly TextBox _input = new();
    private readonly ListBox _list = new();
    private readonly Popup _popup;
    private readonly AuroraCompletionProvider _completions;

    private ConsoleCompletionResult _result = ConsoleCompletionResult.Empty;
    private CancellationTokenSource? _pending;
    private bool _suppress;

    public AuroraSuggestBox(AuroraCompletionProvider completions)
    {
        _completions = completions;
        AuroraComponentResources.Ensure(this);

        _input.SetResourceReference(StyleProperty, "Aurora.Segment.TextBox");
        _input.VerticalContentAlignment = VerticalAlignment.Center;
        _input.TextChanged += (_, _) => Refresh();
        _input.PreviewKeyDown += OnInputKeyDown;
        _input.LostKeyboardFocus += (_, _) => Close();

        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "Aurora.Suggest.Item");
        _list.ItemTemplate = CandidateTemplate();
        _list.MaxHeight = 240;
        _list.BorderThickness = new Thickness(0);
        _list.Background = System.Windows.Media.Brushes.Transparent;
        _list.PreviewMouseLeftButtonUp += (_, _) => Accept();

        var surface = new Border { Child = _list };
        surface.SetResourceReference(StyleProperty, "Aurora.Suggest.Surface");
        AuroraComponentResources.Ensure(surface);

        _popup = new Popup
        {
            PlacementTarget = _input,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = surface,
        };

        Content = new Grid { Children = { _input, _popup } };
    }

    /// <summary>输入框里的文字。程序回写时不触发候选弹出——那会在填值的一瞬间挡住页面。</summary>
    public string Text
    {
        get => _input.Text;
        set
        {
            _suppress = true;
            try
            {
                _input.Text = value ?? "";
                _input.CaretIndex = _input.Text.Length;
            }
            finally
            {
                _suppress = false;
            }
        }
    }

    public string? Placeholder
    {
        get => _input.ToolTip as string;
        set => _input.ToolTip = value;
    }

    /// <summary>文字变化（含候选被接受）。</summary>
    public event EventHandler? TextChanged;

    /// <summary>候选未展开时按下回车。</summary>
    public event EventHandler? Submitted;

    public void FocusInput() => _input.Focus();

    private void Refresh()
    {
        TextChanged?.Invoke(this, EventArgs.Empty);
        if (_suppress)
            return;

        _pending?.Cancel();
        _pending?.Dispose();
        _pending = new CancellationTokenSource();
        _ = RefreshAsync(_input.Text, _input.CaretIndex, _pending.Token);
    }

    private async Task RefreshAsync(string text, int caret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Close();
            return;
        }

        ConsoleCompletionResult result;
        try
        {
            result = await _completions(text, caret, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // 取不到候选就是没有候选：补全不该让输入本身出错。
            Close();
            return;
        }

        // 期间又敲了键：这一批候选已经对不上当前文本，丢掉而不是覆盖上去。
        if (cancellationToken.IsCancellationRequested
            || !string.Equals(_input.Text, text, StringComparison.Ordinal))
            return;

        _result = result;
        _list.ItemsSource = result.Candidates;
        _list.SelectedIndex = result.HasCandidates ? 0 : -1;
        _popup.Width = Math.Clamp(_input.ActualWidth, 1, 720);
        _popup.IsOpen = result.HasCandidates;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen)
        {
            if (e.Key == Key.Enter)
            {
                Submitted?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Tab:
            case Key.Enter:
                Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Move(int direction)
    {
        if (_list.Items.Count == 0)
            return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + direction, 0, _list.Items.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private void Accept()
    {
        if (_list.SelectedItem is not ConsoleCompletionCandidate candidate)
            return;

        var text = _input.Text;
        var start = Math.Clamp(_result.ReplaceStart, 0, text.Length);
        var length = Math.Clamp(_result.ReplaceLength, 0, text.Length - start);
        var replaced = text[..start] + candidate.InsertText + text[(start + length)..];

        _suppress = true;
        try
        {
            _input.Text = replaced;
            _input.CaretIndex = start + candidate.InsertText.Length;
        }
        finally
        {
            _suppress = false;
        }

        Close();
        _input.Focus();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Close()
    {
        _popup.IsOpen = false;
        _result = ConsoleCompletionResult.Empty;
    }

    /// <summary>候选行：左边是要插入的文本，右边是说明。说明用次级色，不与正文抢注意力。</summary>
    private static DataTemplate CandidateTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

        var display = new FrameworkElementFactory(typeof(TextBlock));
        display.SetBinding(TextBlock.TextProperty, new Binding(nameof(ConsoleCompletionCandidate.DisplayText)));
        panel.AppendChild(display);

        var hint = new FrameworkElementFactory(typeof(TextBlock));
        hint.SetBinding(TextBlock.TextProperty, new Binding(nameof(ConsoleCompletionCandidate.Description)));
        hint.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Suggest.Hint");
        panel.AppendChild(hint);

        return new DataTemplate { VisualTree = panel };
    }
}
