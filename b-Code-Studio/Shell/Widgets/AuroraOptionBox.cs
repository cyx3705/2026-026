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

    /// <summary>
    /// 右键打开完整选项菜单。
    ///
    /// **这里刻意不调 <c>Focus()</c>，而且把开菜单排到下一拍**。原来两件事都做了，
    /// 症状是：刚导入零件之后第一次右键没反应，先左键点一下、之后右键才有效。
    /// 原因是这一拍里连着发生了三件事——控件拿焦点、菜单打开、菜单把键盘焦点收走。
    /// 菜单是 <c>StaysOpen = false</c> 的，控件那次迟到的焦点变更一落地它就判自己失焦、
    /// 当场关掉；等控件已经有焦点了（左键点过一次），<c>Focus()</c> 成了空操作，
    /// 菜单就活下来了，于是看起来像「先左键才能右键」。
    ///
    /// 右键因此不再抢焦点：选项菜单自己会拿键盘焦点，控件不需要在它前面插一脚。
    /// 左键那条路仍然要 <c>Focus()</c>——那里没有菜单，键盘接着用得上。
    /// </summary>
    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (IsEnabled)
        {
            OpenOptionsMenu();
            e.Handled = true;
        }

        base.OnPreviewMouseRightButtonDown(e);
    }

    /// <summary>
    /// 右键抬起也吃掉。不吃的话 WPF 的 <c>PopupControlService</c> 会在抬起时按
    /// <see cref="FrameworkElement.ContextMenu"/> 再走一遍开菜单流程——而本控件的菜单
    /// 是自己开的，两条路径撞在一起就是「菜单闪一下就没了」。
    /// </summary>
    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (IsEnabled)
            e.Handled = true;
        base.OnPreviewMouseRightButtonUp(e);
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

        // 菜单**不挂到 ContextMenu 属性上**：挂上去等于同时存在两条开菜单的路径
        // （我们这条，加上 PopupControlService 按属性走的那条）。独立实例、手动打开，
        // 只有一条路径，也就没有「谁把谁关掉」这种问题。
        var menu = new ContextMenu
        {
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
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

        OptionsMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 最近一次右键打开的选项菜单。只给门禁看——它要能验「三项都在、选中那一项打着勾」。
    /// 不是 <see cref="FrameworkElement.ContextMenu"/>：挂上去就多一条开菜单的路径。
    /// </summary>
    internal ContextMenu? OptionsMenu { get; private set; }
}
