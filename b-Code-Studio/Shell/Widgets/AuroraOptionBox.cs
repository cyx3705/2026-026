using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using HistoryAurora.Shell.Themes;

namespace HistoryAurora.Shell.Widgets;

/// <summary>
/// 单值轮换选择器。左键/键盘切换下一项，右键打开完整选项菜单；不显示独立下拉按钮。
/// </summary>
internal sealed class AuroraOptionBox : Selector
{
    public AuroraOptionBox()
    {
        AuroraComponentResources.Ensure(this);
        SetResourceReference(StyleProperty, "Aurora.OptionBox");
        Focusable = true;
    }

    public static readonly DependencyProperty CornerRadiusProperty =
        Border.CornerRadiusProperty.AddOwner(typeof(AuroraOptionBox));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        EnsureSelection();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsEnabled)
        {
            Focus();
            MoveSelection(1);
            e.Handled = true;
        }

        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (IsEnabled)
        {
            Focus();
            OpenOptionsMenu();
            e.Handled = true;
        }

        base.OnPreviewMouseRightButtonDown(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsEnabled)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
            case Key.Enter:
            case Key.Down:
            case Key.Right:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Left:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Home:
                SetSelectedIndex(0);
                e.Handled = true;
                break;
            case Key.End:
                SetSelectedIndex(Items.Count - 1);
                e.Handled = true;
                break;
            case Key.F10 when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                OpenOptionsMenu();
                e.Handled = true;
                break;
        }

        base.OnPreviewKeyDown(e);
    }

    private void EnsureSelection()
    {
        if (Items.Count == 0)
        {
            if (SelectedIndex != -1)
                SetSelectedIndex(-1);
            return;
        }

        if (SelectedIndex < 0 || SelectedIndex >= Items.Count)
            SetSelectedIndex(0);
    }

    private void MoveSelection(int delta)
    {
        if (Items.Count == 0)
            return;

        var current = SelectedIndex < 0 ? 0 : SelectedIndex;
        var next = (current + delta) % Items.Count;
        if (next < 0)
            next += Items.Count;
        SetSelectedIndex(next);
    }

    private void SetSelectedIndex(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            SelectedIndex = -1;
            return;
        }

        SelectedIndex = index;
    }

    private void OpenOptionsMenu()
    {
        if (Items.Count == 0)
            return;

        var menu = new ContextMenu
        {
            PlacementTarget = this,
            StaysOpen = false,
        };

        for (var index = 0; index < Items.Count; index++)
        {
            var itemIndex = index;
            var item = Items[index];
            var entry = new MenuItem
            {
                Header = item,
                IsCheckable = true,
                IsChecked = index == SelectedIndex,
            };
            entry.Click += (_, _) => SetSelectedIndex(itemIndex);
            menu.Items.Add(entry);
        }

        ContextMenu = menu;
        menu.IsOpen = true;
    }
}
