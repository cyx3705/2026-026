using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HistoryAurora.Shell.Components.Table;

public sealed partial class AuroraTable
{
    private DataTemplate CellEditTemplate(AuroraTableColumn column)
    {
        var editor = new FrameworkElementFactory(typeof(AuroraTableCellEditor));
        editor.SetValue(TagProperty, column);
        editor.AddHandler(LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is AuroraTableCellEditor cell)
                cell.Submit = CommitCellEditAsync;
        }));
        return new DataTemplate { VisualTree = editor };
    }

    internal static bool CanEdit(AuroraTableColumn column, IReadOnlyDictionary<string, string> row)
        => column.CellEditAction != null && (column.EditableWhen == null
            || row.TryGetValue(column.EditableWhen, out var flag) && bool.TryParse(flag, out var allowed) && allowed);

    internal async Task<bool> CommitCellEditAsync(AuroraTableColumn column,
        IReadOnlyDictionary<string, string> row, string value)
    {
        if (!CanEdit(column, row)) return false;
        var snapshot = new Dictionary<string, string>(row, StringComparer.Ordinal) { [column.Key] = value };
        var args = new AuroraCellActionEventArgs(column.CellEditAction!, column.Key, snapshot);
        CellActionInvoked?.Invoke(this, args);
        return args.Completion != null && await args.Completion;
    }
}

/// <summary>编辑态只存于当前格；程序刷新、失焦与回写均不执行保存动作。</summary>
public sealed class AuroraTableCellEditor : UserControl
{
    private readonly TextBox _input = new();
    private readonly TextBlock _text = new();
    private IReadOnlyDictionary<string, string>? _snapshot;
    private string _original = "";
    private bool _saving;
    private int _generation;

    internal Func<AuroraTableColumn, IReadOnlyDictionary<string, string>, string, Task<bool>>? Submit { get; set; }

    public AuroraTableCellEditor()
    {
        Themes.AuroraComponentResources.Ensure(this);
        Content = new Grid { Children = { _text, _input } };
        _text.SetResourceReference(StyleProperty, "Aurora.Table.Cell");
        _input.SetResourceReference(StyleProperty, "Aurora.Table.CellEditor");
        _input.ToolTip = "Enter 保存，Escape 取消";
        Loaded += (_, _) => Reset();
        DataContextChanged += (_, _) => Reset();
        _input.GotKeyboardFocus += (_, _) => Capture();
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _input.Text = _original;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SaveAsync();
            }
        };
    }

    private void Capture()
    {
        if (DataContext is IReadOnlyDictionary<string, string> row)
            _snapshot = new Dictionary<string, string>(row, StringComparer.Ordinal);
    }

    private void Reset()
    {
        _generation++;
        _snapshot = null;
        var column = Tag as AuroraTableColumn;
        var row = DataContext as IReadOnlyDictionary<string, string>;
        _original = column != null && row != null && row.TryGetValue(column.Key, out var value) ? value : "";
        _input.Text = _text.Text = _original;
        var editable = column != null && row != null && AuroraTable.CanEdit(column, row);
        _input.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        _text.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;
        if (_input.IsKeyboardFocusWithin) Capture();
    }

    internal async Task SaveAsync()
    {
        if (_saving || Submit == null || Tag is not AuroraTableColumn column
            || _snapshot == null || _input.Text == _original) return;
        var generation = _generation;
        var entered = _input.Text;
        _saving = true;
        _input.IsReadOnly = true;
        try
        {
            var success = await Submit(column, _snapshot, entered);
            if (generation != _generation) return;
            if (success) _original = entered;
            else _input.Text = _original;
        }
        finally
        {
            _saving = false;
            _input.IsReadOnly = false;
        }
    }
}
