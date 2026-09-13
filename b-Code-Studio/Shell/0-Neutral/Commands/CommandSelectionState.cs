namespace HistoryAurora.Shell.Neutral.Commands;

/// <summary>进程内共享的命令集选择，只传递稳定的指令名。</summary>
/// <remarks>宿主 5.4 起不再携带界面状态类型，本类迁回 Aurora。</remarks>
public sealed class CommandSelectionState
{
    private string? _currentCommandName;

    /// <summary>当前选中的指令名；写入时去空白，空白视为清除选择，值不变时不触发 <see cref="Changed"/>。</summary>
    public string? CurrentCommandName
    {
        get => _currentCommandName;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_currentCommandName, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _currentCommandName = normalized;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>选中指令名实际变化后触发；在写入线程上引发。</summary>
    public event EventHandler? Changed;
}
