using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace HistoryAurora.Shell;

/// <summary>
/// Aurora 自持的主题化弹窗。
///
/// 独立 <see cref="Window"/> 拿不到 <c>ShellWindow.Resources</c>。前端切到宿主进程后
/// <c>Application.Current</c> 经常为 null，令牌也不会挂到应用级字典——
/// 模块自己 <c>new Window</c> 再写 <c>DynamicResource Aurora.*</c> 会静默退化成系统外观。
/// 本窗口在构造时合并与主窗体同一套令牌/控件/弹窗字典，不依赖应用级资源。
/// </summary>
public sealed class AuroraDialogWindow : Window
{
    private static readonly Uri LightTokensUri =
        new("/HistoryAurora;component/Themes/AuroraTokens.xaml", UriKind.Relative);

    private static readonly Uri DarkTokensUri =
        new("/HistoryAurora;component/Themes/AuroraTokens.Dark.xaml", UriKind.Relative);

    private static readonly Uri ControlsUri =
        new("/HistoryAurora;component/Themes/AuroraControls.xaml", UriKind.Relative);

    private static readonly Uri DialogUri =
        new("/HistoryAurora;component/Themes/AuroraDialog.xaml", UriKind.Relative);

    private static readonly Uri ChromeUri =
        new("/HistoryAurora;component/Themes/AuroraChrome.xaml", UriKind.Relative);

    private readonly AuroraDialogRequest _request;
    private bool _accepted;
    private bool _timedOut;
    private DispatcherTimer? _timer;
    private int _remaining;

    public AuroraDialogWindow(AuroraDialogRequest request, Window? owner, bool dark)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;

        Title = string.IsNullOrWhiteSpace(request.Title) ? "HistoryAurora" : request.Title;
        Owner = owner;
        WindowStartupLocation = owner == null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;

        MergeTheme(dark);
        SetResourceReference(ForegroundProperty, "Aurora.Brush.TextPrimary");
        SetResourceReference(BackgroundProperty, "Aurora.Brush.Canvas");

        Content = BuildContent();
        ApplySize();
        ApplyChrome();

        SourceInitialized += (_, _) =>
        {
            if (TryFindResource("Aurora.Brush.Hairline") is Brush hairline)
                FloatingWindowTheme.ApplyNativeBorder(this, hairline);
        };
        Closed += (_, _) => _timer?.Stop();
    }

    internal TextBlock? CaptionTitle { get; private set; }

    internal TextBlock? BodyText { get; private set; }

    internal TextBox? PromptBox { get; private set; }

    internal TextBox? ContentBox { get; private set; }

    internal ListBox? ChoiceBox { get; private set; }

    internal TextBlock? CountdownText { get; private set; }

    internal Button PrimaryButton { get; private set; } = null!;

    internal Button? CancelButton { get; private set; }

    internal string? SelectedChoiceValue
        => (ChoiceBox?.SelectedItem as AuroraDialogChoice)?.Value;

    /// <summary>按当前浅色/深色令牌构造，不显示。测试走这条，避免 <c>ShowDialog</c> 挂死 STA。</summary>
    public static AuroraDialogWindow Create(AuroraDialogRequest request, Window? owner = null, bool dark = false)
        => new(request, owner, dark);

    /// <summary>模态显示。编组到 owner 的 UI 线程；无 owner 时要求已在 UI 线程。</summary>
    public static AuroraDialogResult Show(AuroraDialogRequest request, Window? owner, bool dark)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (owner != null && !owner.Dispatcher.CheckAccess())
            return owner.Dispatcher.Invoke(() => ShowCore(request, owner, dark));
        return ShowCore(request, owner, dark);
    }

    private static AuroraDialogResult ShowCore(AuroraDialogRequest request, Window? owner, bool dark)
    {
        var window = new AuroraDialogWindow(request, owner, dark);
        window.ShowDialog();
        var value = request.Kind == AuroraDialogKind.Choice
            ? window.SelectedChoiceValue
            : window.PromptBox?.Text;
        return new AuroraDialogResult(window._accepted, window._timedOut, value);
    }

    private void MergeTheme(bool dark)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = dark ? DarkTokensUri : LightTokensUri });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ControlsUri });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ChromeUri });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DialogUri });
    }

    private void ApplyChrome()
    {
        var caption = TryFindResource("Aurora.Size.Tab") is double tab ? tab : 32;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = caption,
            ResizeBorderThickness = ResizeMode == ResizeMode.CanResize
                ? new Thickness(6)
                : new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
    }

    private void ApplySize()
    {
        switch (_request.Kind)
        {
            case AuroraDialogKind.Content:
                Width = 860;
                Height = 640;
                MinWidth = 560;
                MinHeight = 360;
                ResizeMode = ResizeMode.CanResize;
                SizeToContent = SizeToContent.Manual;
                break;
            case AuroraDialogKind.Prompt:
                Width = 520;
                MinWidth = 420;
                ResizeMode = ResizeMode.NoResize;
                SizeToContent = SizeToContent.Height;
                break;
            case AuroraDialogKind.Choice:
                Width = 520;
                Height = 460;
                MinWidth = 420;
                MinHeight = 300;
                ResizeMode = ResizeMode.CanResize;
                SizeToContent = SizeToContent.Manual;
                break;
            default:
                MinWidth = 360;
                MaxWidth = 560;
                ResizeMode = ResizeMode.NoResize;
                SizeToContent = SizeToContent.WidthAndHeight;
                break;
        }
    }

    private FrameworkElement BuildContent()
    {
        var chrome = new Border();
        chrome.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Chrome");

        var root = new DockPanel();
        var caption = BuildCaption();
        DockPanel.SetDock(caption, Dock.Top);
        root.Children.Add(caption);

        var inner = new DockPanel();
        var pad = new Border { Child = inner };
        pad.SetResourceReference(Border.PaddingProperty, "Aurora.Space.Pad");

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
            PromptBox.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Prompt");
            DockPanel.SetDock(PromptBox, Dock.Bottom);
            inner.Children.Add(PromptBox);
        }

        if (_request.Kind == AuroraDialogKind.Choice)
        {
            if (!string.IsNullOrWhiteSpace(_request.Body))
            {
                BodyText = new TextBlock { Text = _request.Body, MaxWidth = 460 };
                BodyText.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Body");
                DockPanel.SetDock(BodyText, Dock.Top);
                inner.Children.Add(BodyText);
            }

            ChoiceBox = new ListBox
            {
                ItemsSource = _request.Choices,
                DisplayMemberPath = nameof(AuroraDialogChoice.Label),
                SelectedIndex = _request.Choices.Count > 0 ? 0 : -1,
                Margin = new Thickness(0, 0, 0, 12),
            };
            ScrollViewer.SetVerticalScrollBarVisibility(ChoiceBox, ScrollBarVisibility.Auto);
            ChoiceBox.MouseDoubleClick += (_, _) =>
            {
                if (ChoiceBox.SelectedItem == null)
                    return;
                _accepted = true;
                DialogResult = true;
            };
            inner.Children.Add(ChoiceBox);
            PrimaryButton.IsEnabled = _request.Choices.Count > 0;
        }

        if (_request.Kind == AuroraDialogKind.Content)
        {
            if (!string.IsNullOrWhiteSpace(_request.Body))
            {
                BodyText = new TextBlock { Text = _request.Body };
                BodyText.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Title");
                DockPanel.SetDock(BodyText, Dock.Top);
                inner.Children.Add(BodyText);
            }

            ContentBox = new TextBox { Text = _request.Content ?? "" };
            ContentBox.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Content");
            inner.Children.Add(ContentBox);
        }
        else if (_request.Kind != AuroraDialogKind.Choice && !string.IsNullOrWhiteSpace(_request.Body))
        {
            BodyText = new TextBlock { Text = _request.Body, MaxWidth = 460 };
            BodyText.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Body");
            inner.Children.Add(BodyText);
        }

        root.Children.Add(pad);
        chrome.Child = root;
        return chrome;
    }

    private FrameworkElement BuildCaption()
    {
        var bar = new Grid();
        bar.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Caption");
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        CaptionTitle = new TextBlock { Text = Title, VerticalAlignment = VerticalAlignment.Center };
        CaptionTitle.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.CaptionTitle");
        Grid.SetColumn(CaptionTitle, 0);

        var close = new Button { Focusable = false, ToolTip = "关闭" };
        close.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.WindowButton.Close");
        close.Click += (_, _) =>
        {
            _accepted = false;
            try { DialogResult = false; }
            catch (InvalidOperationException) { Close(); }
        };
        var icon = new Path
        {
            Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"),
            StrokeThickness = 1.2,
            Stretch = Stretch.Uniform,
            Width = 10,
            Height = 10,
        };
        icon.SetBinding(Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = close });
        close.Content = icon;
        Grid.SetColumn(close, 1);

        bar.Children.Add(CaptionTitle);
        bar.Children.Add(close);

        var wrap = new DockPanel { LastChildFill = true };
        var hairline = new Border { Height = 1 };
        hairline.SetResourceReference(Border.BackgroundProperty, "Aurora.Brush.Hairline");
        DockPanel.SetDock(hairline, Dock.Bottom);
        wrap.Children.Add(hairline);
        wrap.Children.Add(bar);
        return wrap;
    }

    private FrameworkElement BuildFooter()
    {
        var footer = new StackPanel();
        footer.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Footer");

        var closeOnly = _request.Kind is AuroraDialogKind.Message or AuroraDialogKind.Content;
        if (!closeOnly)
        {
            CancelButton = new Button
            {
                Content = string.IsNullOrWhiteSpace(_request.CancelText) ? "取消" : _request.CancelText,
                MinWidth = 88,
                IsCancel = true,
                IsDefault = _request.DefaultCancel,
                Margin = new Thickness(0, 0, 8, 0),
            };
            CancelButton.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Button.Ghost");
            CancelButton.Click += (_, _) =>
            {
                _accepted = false;
                DialogResult = false;
            };
            footer.Children.Add(CancelButton);
        }

        PrimaryButton = new Button
        {
            Content = PrimaryLabel(closeOnly),
            MinWidth = 88,
            IsDefault = !_request.DefaultCancel,
            IsCancel = closeOnly,
        };
        PrimaryButton.SetResourceReference(
            FrameworkElement.StyleProperty,
            _request.Danger ? "Aurora.Button.Danger" : closeOnly ? "Aurora.Button.Base" : "Aurora.Button.Accent");
        PrimaryButton.Click += (_, _) =>
        {
            _accepted = true;
            DialogResult = true;
        };
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
        CountdownText.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Countdown");
        CountdownText.Text = $"{_remaining} 秒内未操作将自动拒绝";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                _timer.Stop();
                _timedOut = true;
                _accepted = false;
                try { DialogResult = false; }
                catch (InvalidOperationException) { Close(); }
                return;
            }

            CountdownText.Text = $"{_remaining} 秒内未操作将自动拒绝";
        };
        Loaded += (_, _) => _timer.Start();
        return CountdownText;
    }
}
