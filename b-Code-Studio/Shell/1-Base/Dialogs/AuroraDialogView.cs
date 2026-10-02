using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace HistoryAurora.Shell.Base.Dialogs;

/// <summary>
/// Aurora 弹窗本体（REQ-UI-136，1.30.0）：一张嵌在主窗体里的圆角卡片，与停靠区里的页面卡片同一个长相——
/// Surface 底、<c>Aurora.Radius.Page</c> 圆角、段标题接墨色条。
///
/// 1.29 及以前弹窗是一个独立的无边框 <see cref="Window"/>（<c>AuroraDialogWindow</c>）：
/// 要自己合并令牌、自绘标题栏、借 WindowChrome 拖动，长相和页面是两套。
/// 现在卡片由 <see cref="AuroraDialogHost"/> 放进主窗体的弹窗层，令牌、主题切换全跟着主窗体走；
/// 这里只管「一次弹窗里有什么」，不管它放在哪、怎么等结果。
///
/// 结果经 <see cref="Completed"/> 交出，只交一次。键盘：Enter 落到默认按钮（危险确认落到取消），Esc 取消。
/// </summary>
public sealed class AuroraDialogView : Border
{
    private static readonly Uri LightTokensUri =
        new("/HistoryAurora;component/Themes/AuroraTokens.xaml", UriKind.Relative);

    private static readonly Uri DarkTokensUri =
        new("/HistoryAurora;component/Themes/AuroraTokens.Dark.xaml", UriKind.Relative);

    private static readonly Uri ControlsUri =
        new("/HistoryAurora;component/Themes/AuroraControls.xaml", UriKind.Relative);

    internal static readonly Uri DialogUri =
        new("/HistoryAurora;component/Themes/AuroraDialog.xaml", UriKind.Relative);

    private readonly AuroraDialogRequest _request;
    private DispatcherTimer? _timer;
    private int _remaining;
    private bool _done;

    /// <summary>
    /// 构造一张卡片。<paramref name="standalone"/> 为 true 时自带整套令牌（测试、或没有主窗体可依附时）；
    /// 放进主窗体时为 false，只合并弹窗样式，令牌与主题由主窗体提供。
    /// </summary>
    public AuroraDialogView(AuroraDialogRequest request, bool standalone = false, bool dark = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;

        if (standalone)
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = dark ? DarkTokensUri : LightTokensUri });
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ControlsUri });
        }
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DialogUri });

        SetResourceReference(StyleProperty, "Aurora.Dialog.Card");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Cycle);
        PreviewKeyDown += OnPreviewKeyDown;

        Child = BuildContent();
        ApplySize();
        Unloaded += (_, _) => _timer?.Stop();
    }

    /// <summary>人做了决定（或倒计时到期）。只触发一次。</summary>
    public event EventHandler<AuroraDialogResult>? Completed;

    public AuroraDialogRequest Request => _request;

    internal TextBlock? CaptionTitle { get; private set; }

    internal TextBlock? BodyText { get; private set; }

    internal TextBox? PromptBox { get; private set; }

    internal TextBox? ContentBox { get; private set; }

    internal ListBox? ChoiceBox { get; private set; }

    internal TextBlock? CountdownText { get; private set; }

    internal Button PrimaryButton { get; private set; } = null!;

    internal Button? CancelButton { get; private set; }

    /// <summary>Enter 落到哪个按钮：危险确认（<see cref="AuroraDialogRequest.DefaultCancel"/>）落到取消。</summary>
    internal Button DefaultButton => _request.DefaultCancel && CancelButton != null ? CancelButton : PrimaryButton;

    internal string? SelectedChoiceValue
        => (ChoiceBox?.SelectedItem as AuroraDialogChoice)?.Value;

    /// <summary>当前输入：prompt 是文字，choice / 带候选的 content 是所选 value。</summary>
    internal string? CurrentInput => _request.PicksChoice ? SelectedChoiceValue : PromptBox?.Text;

    /// <summary>卡片进场后把焦点交给第一个该拿焦点的控件：输入框、候选表，否则默认按钮。</summary>
    internal void FocusInitial()
    {
        if (_timer != null && !_timer.IsEnabled)
            _timer.Start();

        Control target = PromptBox ?? (Control?)ChoiceBox ?? DefaultButton;
        target.Focus();
        Keyboard.Focus(target);
        PromptBox?.SelectAll();
    }

    /// <summary>接受（主按钮、双击候选、Enter）。</summary>
    internal void Accept() => Finish(new AuroraDialogResult(true, false, CurrentInput));

    /// <summary>拒绝（取消、Esc、宿主撤销）。</summary>
    internal void Cancel() => Finish(new AuroraDialogResult(false, false, CurrentInput));

    private void Finish(AuroraDialogResult result)
    {
        if (_done)
            return;
        _done = true;
        _timer?.Stop();
        Completed?.Invoke(this, result);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                e.Handled = true;
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                // 焦点在按钮上时 Enter 就是按那个按钮——Tab 到「取消」再回车，不能变成「确定」。
                if (e.OriginalSource is Button { IsEnabled: true } focused)
                    focused.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, focused));
                else if (DefaultButton.IsEnabled)
                    DefaultButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, DefaultButton));
                e.Handled = true;
                break;
        }
    }

    private void ApplySize()
    {
        switch (_request.Kind)
        {
            case AuroraDialogKind.Content:
                Width = 860;
                Height = 640;
                break;
            case AuroraDialogKind.Prompt:
                Width = 520;
                break;
            case AuroraDialogKind.Choice:
                Width = 520;
                Height = 460;
                break;
            default:
                MinWidth = 360;
                MaxWidth = 560;
                break;
        }
    }

    private FrameworkElement BuildContent()
    {
        var inner = new DockPanel();

        // 段标题 + 墨色条（REQ-UI-132），与页面里的段标题同一组样式；没有标题就不画这一行。
        if (!string.IsNullOrWhiteSpace(_request.Title))
        {
            var heading = BuildHeading();
            DockPanel.SetDock(heading, Dock.Top);
            inner.Children.Add(heading);
        }

        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        inner.Children.Add(footer);

        if (_request.Kind == AuroraDialogKind.Confirm && _request.TimeoutSeconds > 0)
        {
            var countdown = BuildCountdown();
            DockPanel.SetDock(countdown, Dock.Bottom);
            inner.Children.Add(countdown);
        }

        if (_request.Kind == AuroraDialogKind.Prompt)
        {
            PromptBox = new TextBox { Text = _request.Value ?? "" };
            PromptBox.SetResourceReference(StyleProperty, "Aurora.Dialog.Prompt");
            DockPanel.SetDock(PromptBox, Dock.Bottom);
            inner.Children.Add(PromptBox);
        }

        if (_request.Kind == AuroraDialogKind.Choice && !string.IsNullOrWhiteSpace(_request.Body))
        {
            BodyText = new TextBlock { Text = _request.Body };
            BodyText.SetResourceReference(StyleProperty, "Aurora.Dialog.Body");
            BodyText.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(BodyText, Dock.Top);
            inner.Children.Add(BodyText);
        }

        // 候选表在 choice 里是本体，在 content 里是正文下面那一截：两处同一个控件，
        // 差别只是 content 的正文要先占住余量，所以候选表停靠底部、正文填满剩下的。
        if (_request.PicksChoice)
        {
            ChoiceBox = new ListBox
            {
                ItemsSource = _request.Choices,
                DisplayMemberPath = nameof(AuroraDialogChoice.Label),
                SelectedIndex = _request.Choices.Count > 0 ? 0 : -1,
                Margin = new Thickness(0, 0, 0, 4),
            };
            ScrollViewer.SetVerticalScrollBarVisibility(ChoiceBox, ScrollBarVisibility.Auto);
            ChoiceBox.MouseDoubleClick += (_, _) =>
            {
                if (ChoiceBox.SelectedItem != null)
                    Accept();
            };
            if (_request.Kind == AuroraDialogKind.Content)
            {
                // 动作只有两三条，让它按内容高度停在底部；正文才是要滚的那一块。
                ChoiceBox.MaxHeight = 160;
                ChoiceBox.Margin = new Thickness(0, 8, 0, 4);
                DockPanel.SetDock(ChoiceBox, Dock.Bottom);
            }
            inner.Children.Add(ChoiceBox);
            PrimaryButton.IsEnabled = _request.Choices.Count > 0;
        }

        if (_request.Kind == AuroraDialogKind.Content)
        {
            if (!string.IsNullOrWhiteSpace(_request.Body))
            {
                BodyText = new TextBlock { Text = _request.Body };
                BodyText.SetResourceReference(StyleProperty, "Aurora.Dialog.Title");
                DockPanel.SetDock(BodyText, Dock.Top);
                inner.Children.Add(BodyText);
            }

            ContentBox = new TextBox { Text = _request.Content ?? "" };
            ContentBox.SetResourceReference(StyleProperty, "Aurora.Dialog.Content");
            inner.Children.Add(ContentBox);
        }
        else if (_request.Kind != AuroraDialogKind.Choice && !string.IsNullOrWhiteSpace(_request.Body))
        {
            BodyText = new TextBlock { Text = _request.Body };
            BodyText.SetResourceReference(StyleProperty, "Aurora.Dialog.Body");
            inner.Children.Add(BodyText);
        }

        return inner;
    }

    private FrameworkElement BuildHeading()
    {
        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        CaptionTitle = new TextBlock { Text = _request.Title };
        CaptionTitle.SetResourceReference(StyleProperty, "Aurora.Heading.Title");
        DockPanel.SetDock(CaptionTitle, Dock.Left);
        heading.Children.Add(CaptionTitle);

        var bar = new Border();
        bar.SetResourceReference(StyleProperty, "Aurora.Heading.Bar");
        heading.Children.Add(bar);
        return heading;
    }

    private FrameworkElement BuildFooter()
    {
        var footer = new StackPanel();
        footer.SetResourceReference(StyleProperty, "Aurora.Dialog.Footer");

        // 只有一个关闭键的前提是「没有要做的决定」。content 带上动作候选之后就有了，
        // 于是它和 confirm 一样要一对按钮——否则人没法在看完正文后说「不做」。
        var closeOnly = _request.Kind == AuroraDialogKind.Message
                        || (_request.Kind == AuroraDialogKind.Content && !_request.PicksChoice);
        if (!closeOnly)
        {
            CancelButton = new Button
            {
                Content = string.IsNullOrWhiteSpace(_request.CancelText) ? "取消" : _request.CancelText,
                MinWidth = 88,
                Margin = new Thickness(0, 0, 8, 0),
            };
            CancelButton.SetResourceReference(StyleProperty, "Aurora.Button.Ghost");
            CancelButton.Click += (_, _) => Cancel();
            footer.Children.Add(CancelButton);
        }

        PrimaryButton = new Button
        {
            Content = PrimaryLabel(closeOnly),
            MinWidth = 88,
        };
        PrimaryButton.SetResourceReference(
            StyleProperty,
            _request.Danger ? "Aurora.Button.Danger" : closeOnly ? "Aurora.Button.Base" : "Aurora.Button.Accent");
        // 只有关闭键时「接受」与「关掉」是同一件事，结果仍按旧弹窗记作接受。
        PrimaryButton.Click += (_, _) => Accept();
        footer.Children.Add(PrimaryButton);
        return footer;
    }

    private string PrimaryLabel(bool closeOnly)
    {
        if (!string.IsNullOrWhiteSpace(_request.PrimaryText) && _request.PrimaryText != "确定")
            return _request.PrimaryText;
        return closeOnly ? "关闭" : "确定";
    }

    private TextBlock BuildCountdown()
    {
        _remaining = Math.Max(5, _request.TimeoutSeconds);
        CountdownText = new TextBlock();
        CountdownText.SetResourceReference(StyleProperty, "Aurora.Dialog.Countdown");
        CountdownText.Text = $"{_remaining} 秒内未操作将自动拒绝";

        // 进场（FocusInitial）才开始倒数：排队等在别的弹窗后面的那张不该先把时间耗掉。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                Finish(new AuroraDialogResult(false, true, CurrentInput));
                return;
            }

            CountdownText.Text = $"{_remaining} 秒内未操作将自动拒绝";
        };
        return CountdownText;
    }
}
