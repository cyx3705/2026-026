using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        MergeTheme(dark);
        SetResourceReference(ForegroundProperty, "Aurora.Brush.TextPrimary");
        SetResourceReference(BackgroundProperty, "Aurora.Brush.Surface");

        Content = BuildContent();
        ApplySize();

        SourceInitialized += (_, _) =>
        {
            if (TryFindResource("Aurora.Brush.Hairline") is Brush hairline)
                FloatingWindowTheme.ApplyNativeBorder(this, hairline);
        };
        Closed += (_, _) => _timer?.Stop();
    }

    internal TextBlock? BodyText { get; private set; }

    internal TextBox? PromptBox { get; private set; }

    internal TextBox? ContentBox { get; private set; }

    internal TextBlock? CountdownText { get; private set; }

    internal Button PrimaryButton { get; private set; } = null!;

    internal Button? CancelButton { get; private set; }

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
        return new AuroraDialogResult(window._accepted, window._timedOut, window.PromptBox?.Text);
    }

    private void MergeTheme(bool dark)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = dark ? DarkTokensUri : LightTokensUri });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ControlsUri });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DialogUri });
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
        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        if (_request.Kind == AuroraDialogKind.Confirm && _request.TimeoutSeconds > 0)
        {
            var countdown = BuildCountdown();
            DockPanel.SetDock(countdown, Dock.Bottom);
            root.Children.Add(countdown);
        }

        if (_request.Kind == AuroraDialogKind.Prompt)
        {
            PromptBox = new TextBox { Text = _request.Value ?? "" };
            PromptBox.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Prompt");
            DockPanel.SetDock(PromptBox, Dock.Bottom);
            root.Children.Add(PromptBox);
        }

        if (_request.Kind == AuroraDialogKind.Content)
        {
            if (!string.IsNullOrWhiteSpace(_request.Body))
            {
                BodyText = new TextBlock { Text = _request.Body };
                BodyText.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Title");
                DockPanel.SetDock(BodyText, Dock.Top);
                root.Children.Add(BodyText);
            }

            ContentBox = new TextBox { Text = _request.Content ?? "" };
            ContentBox.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Content");
            root.Children.Add(ContentBox);
        }
        else if (!string.IsNullOrWhiteSpace(_request.Body))
        {
            BodyText = new TextBlock { Text = _request.Body, MaxWidth = 460 };
            BodyText.SetResourceReference(FrameworkElement.StyleProperty, "Aurora.Dialog.Body");
            root.Children.Add(BodyText);
        }

        chrome.Child = root;
        return chrome;
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
