using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using HistoryAurora.Shell.Components.Widgets;
using Xunit;

namespace HistoryAurora.Verify;

[Collection(TestCollections.Ui)]
public sealed class AuroraOptionBoxContractTests
{
    [Fact]
    public void LeftClickCyclesAndWraps()
    {
        UiTestHost.RunSta(() =>
        {
            var box = new AuroraOptionBox { ItemsSource = new[] { "浅色", "深色", "跟随系统" } };
            Assert.Equal("浅色", box.SelectedItem);

            RaiseMouse(box, MouseButton.Left);
            Assert.Equal("深色", box.SelectedItem);
            RaiseMouse(box, MouseButton.Left);
            RaiseMouse(box, MouseButton.Left);
            Assert.Equal("浅色", box.SelectedItem);
        });
    }

    [Fact]
    public void KeyboardMovesInBothDirectionsAndJumpsToEnds()
    {
        UiTestHost.RunSta(() =>
        {
            var box = new AuroraOptionBox { ItemsSource = new[] { "一", "二", "三" } };
            var window = new Window
            {
                Content = box,
                Width = 1,
                Height = 1,
                Opacity = 0,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow,
            };
            window.Show();
            UiTestHost.Pump();

            try
            {
                RaiseKey(box, Key.End);
                Assert.Equal("三", box.SelectedItem);
                RaiseKey(box, Key.Left);
                Assert.Equal("二", box.SelectedItem);
                RaiseKey(box, Key.Home);
                Assert.Equal("一", box.SelectedItem);
                RaiseKey(box, Key.Enter);
                Assert.Equal("二", box.SelectedItem);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RightClickBuildsACompleteCheckedMenu()
    {
        UiTestHost.RunSta(() =>
        {
            var box = new AuroraOptionBox { ItemsSource = new[] { "一", "二", "三" } };
            box.SelectedIndex = 1;

            RaiseMouse(box, MouseButton.Right);

            // 菜单是独立实例，不挂在 ContextMenu 属性上：挂上去会和 WPF 自己那条
            // 按属性开菜单的路径撞车，表现为刚导入后第一次右键菜单闪一下就没。
            Assert.Null(box.ContextMenu);
            Assert.NotNull(box.OptionsMenu);
            Assert.Equal(3, box.OptionsMenu!.Items.Count);
            Assert.True(((System.Windows.Controls.MenuItem)box.OptionsMenu.Items[1]).IsChecked);
        });
    }

    [Fact]
    public void RefreshKeepsTheCurrentValueAndFallsBackToTheFirstValue()
    {
        UiTestHost.RunSta(() =>
        {
            var values = new System.Collections.ObjectModel.ObservableCollection<string>(["一", "二"]);
            var box = new AuroraOptionBox { ItemsSource = values };
            box.SelectedItem = "二";

            values.Add("三");
            Assert.Equal("二", box.SelectedItem);

            values.Clear();
            values.Add("新一");
            Assert.Equal("新一", box.SelectedItem);

            var empty = new AuroraOptionBox { ItemsSource = Array.Empty<string>() };
            Assert.Equal(-1, empty.SelectedIndex);
        });
    }

    private static void RaiseMouse(AuroraOptionBox box, MouseButton button)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
        {
            RoutedEvent = button == MouseButton.Left
                ? UIElement.PreviewMouseLeftButtonDownEvent
                : UIElement.PreviewMouseRightButtonDownEvent,
        };
        box.RaiseEvent(args);
    }

    private static void RaiseKey(AuroraOptionBox box, Key key)
    {
        var source = PresentationSource.FromVisual(box)
            ?? throw new InvalidOperationException("测试选项框尚未连接到 WPF 输入源");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        box.RaiseEvent(args);
    }
}
