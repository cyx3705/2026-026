using HistoryAurora.Shell.Neutral.Storage;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Composition;
using Xunit;
using HistoryAurora.Shell.Base.Dialogs;
using HistoryAurora.Shell.Neutral.Logging;

namespace HistoryAurora.Verify;

/// <summary>
/// 弹窗组件的契约（REQ-UI-004 / 136）。
///
/// 1.30.0 起弹窗是嵌在主窗体里的圆角卡片 <see cref="AuroraDialogView"/>，由 <see cref="AuroraDialogHost"/>
/// 放进主窗体的弹窗层；独立的 <c>AuroraDialogWindow</c> 删除。单独构造的卡片（这里的大部分用例）
/// 仍要自带主题字典、不依赖 Application.Current——那是 1.2.0 起守的老问题。
/// </summary>
[Collection(TestCollections.Ui)]
public sealed class AuroraDialogContractTests
{
    [Fact]
    public void DialogResolvesTokensWithoutApplicationResources()
    {
        UiTestHost.RunSta(() =>
        {
            var previous = Application.Current?.Resources;
            ResourceDictionary? backup = null;
            if (previous != null)
            {
                backup = previous;
                Application.Current!.Resources = new ResourceDictionary();
            }

            try
            {
                var dialog = Card(new AuroraDialogRequest
                {
                    Kind = AuroraDialogKind.Message,
                    Title = "关于",
                    Body = "hello",
                });

                var surface = Assert.IsType<SolidColorBrush>(dialog.TryFindResource("Aurora.Brush.Surface"));
                Assert.True(surface.Color.R > 0xE0 && surface.Color.G > 0xE0 && surface.Color.B > 0xE0,
                    $"dialog should resolve the light surface, got {surface.Color}");
                Assert.NotNull(dialog.TryFindResource("Aurora.Button.Accent"));
                Assert.NotNull(dialog.TryFindResource("Aurora.Dialog.Card"));
                Assert.Equal(new CornerRadius(20), dialog.CornerRadius);
                Assert.Equal("关于", dialog.CaptionTitle?.Text);
                Assert.Equal("关闭", dialog.PrimaryButton.Content);
                Assert.Null(dialog.CancelButton);
            }
            finally
            {
                if (previous != null && backup != null)
                    Application.Current!.Resources = backup;
            }
        });
    }

    [Fact]
    public void DarkDialogUsesDarkSurfaceToken()
    {
        UiTestHost.RunSta(() =>
        {
            var dialog = new AuroraDialogView(
                new AuroraDialogRequest { Kind = AuroraDialogKind.Message, Body = "dark" },
                standalone: true,
                dark: true);

            var surface = Assert.IsType<SolidColorBrush>(dialog.TryFindResource("Aurora.Brush.Surface"));
            Assert.Equal((Color)ColorConverter.ConvertFromString("#1D201F")!, surface.Color);
        });
    }

    [Fact]
    public void ConfirmPromptAndContentBuildTheExpectedChrome()
    {
        UiTestHost.RunSta(() =>
        {
            var confirm = Card(new AuroraDialogRequest
            {
                Kind = AuroraDialogKind.Confirm,
                Title = "需要确认",
                Body = "覆盖？",
                Danger = true,
                DefaultCancel = true,
                TimeoutSeconds = 12,
            });
            Assert.Equal("覆盖？", confirm.BodyText?.Text);
            Assert.NotNull(confirm.CancelButton);
            Assert.Same(confirm.CancelButton, confirm.DefaultButton);
            Assert.Contains("秒内未操作", confirm.CountdownText?.Text);

            var prompt = Card(new AuroraDialogRequest
            {
                Kind = AuroraDialogKind.Prompt,
                Title = "生成恢复提交",
                Body = "恢复提交说明",
                Value = "revert abc",
            });
            Assert.Equal("revert abc", prompt.PromptBox?.Text);
            Assert.Equal("确定", prompt.PrimaryButton.Content);

            var content = Card(new AuroraDialogRequest
            {
                Kind = AuroraDialogKind.Content,
                Title = "预览",
                Body = "abc123 提交",
                Content = "diff --git a/file",
            });
            Assert.Equal("abc123 提交", content.BodyText?.Text);
            Assert.Equal("diff --git a/file", content.ContentBox?.Text);
            Assert.IsType<TextBox>(content.ContentBox);
            Assert.Equal("预览", content.CaptionTitle?.Text);
            Assert.NotNull(content.TryFindResource("Aurora.Heading.Bar"));
        });
    }

    [Fact]
    public void ChoiceBuildsScrollableKeyboardListAndReturnsValue()
    {
        UiTestHost.RunSta(() =>
        {
            var choices = Enumerable.Range(1, 40)
                .Select(index => new AuroraDialogChoice
                {
                    Label = $"目录 {index}",
                    Value = $"z-{index}",
                })
                .ToList();
            var dialog = Card(new AuroraDialogRequest
            {
                Kind = AuroraDialogKind.Choice,
                Title = "选择 z 级文件夹",
                Body = "请选择要打开的目录",
                Choices = choices,
            });

            Assert.NotNull(dialog.ChoiceBox);
            Assert.Equal(40, dialog.ChoiceBox!.Items.Count);
            Assert.True(dialog.ChoiceBox.Focusable);
            Assert.Equal(ScrollBarVisibility.Auto,
                ScrollViewer.GetVerticalScrollBarVisibility(dialog.ChoiceBox));
            Assert.Equal("z-1", dialog.SelectedChoiceValue);
            dialog.ChoiceBox.SelectedIndex = 18;
            Assert.Equal("z-19", dialog.SelectedChoiceValue);
            Assert.NotNull(dialog.CancelButton);
            Assert.Same(dialog.PrimaryButton, dialog.DefaultButton);
        });
    }

    // 1.24.0（REQ-UI-123）：content 带 options 时是「看清正文再决定」的同一个窗，
    // 因此正文、候选表和一对按钮必须同时在。少了取消键，人看完就只剩「做」这一条路。
    [Fact]
    public void ContentWithOptionsShowsBothTheBodyAndTheActionList()
    {
        UiTestHost.RunSta(() =>
        {
            var dialog = Card(new AuroraDialogRequest
            {
                Kind = AuroraDialogKind.Content,
                Title = "提交",
                Body = "3 个文件变更",
                Content = "diff --git a/file",
                Choices =
                [
                    new AuroraDialogChoice { Label = "提交", Value = "commit" },
                    new AuroraDialogChoice { Label = "删除本次脏工作树", Value = "discard" },
                ],
                PrimaryText = "执行",
            });

            Assert.Equal("diff --git a/file", dialog.ContentBox?.Text);
            Assert.Equal("3 个文件变更", dialog.BodyText?.Text);
            Assert.NotNull(dialog.ChoiceBox);
            Assert.Equal(2, dialog.ChoiceBox!.Items.Count);
            Assert.Equal("commit", dialog.SelectedChoiceValue);
            dialog.ChoiceBox.SelectedIndex = 1;
            Assert.Equal("discard", dialog.SelectedChoiceValue);
            Assert.NotNull(dialog.CancelButton);
            Assert.Equal("执行", dialog.PrimaryButton.Content);
            Assert.True(dialog.PrimaryButton.IsEnabled);
        });
    }

    // 不给 options 的 content 一个字都不能变：它仍是只读预览，只有一个关闭键。
    [Fact]
    public void ContentWithoutOptionsStaysACloseOnlyPreview()
    {
        UiTestHost.RunSta(() =>
        {
            var dialog = Card(new AuroraDialogRequest
            {
                Kind = AuroraDialogKind.Content,
                Title = "预览",
                Content = "diff --git a/file",
            });

            Assert.Null(dialog.ChoiceBox);
            Assert.Null(dialog.CancelButton);
            Assert.Equal("关闭", dialog.PrimaryButton.Content);
        });
    }

    [Fact]
    public void ChoiceReaderRejectsMalformedEmptyAndIncompleteOptions()
    {
        Assert.False(AuroraDialogChoiceReader.TryRead("[]", out _, out var empty));
        Assert.Contains("不能为空", empty);

        Assert.False(AuroraDialogChoiceReader.TryRead("not-json", out _, out var malformed));
        Assert.Contains("合法", malformed);

        Assert.False(AuroraDialogChoiceReader.TryRead(
            """[{"label":"目录"}]""", out _, out var incomplete));
        Assert.Contains("label 和 value", incomplete);

        Assert.True(AuroraDialogChoiceReader.TryRead(
            """[{"label":"目录 A","value":"z-A"}]""", out var choices, out var error), error);
        Assert.Equal("z-A", Assert.Single(choices).Value);
    }

    [Fact]
    public void InProcessShellClaimsHostConfirmationChannel()
    {
        var module = Path.Combine(RepositoryRoot(), "b-Code-Studio", "Module");
        var frontend = File.ReadAllText(Path.Combine(module, "AuroraFrontend.cs"));
        Assert.Contains("new DialogConfirmation(window.Dialogs)", frontend, StringComparison.Ordinal);

        // 宿主 5.4：确认经唯一前端登记交出，不得再改写宿主总线的确认开关。
        var composition = File.ReadAllText(Path.Combine(RepositoryRoot(), "b-Code-Studio", "AuroraBusinessComposition.cs"));
        Assert.Contains("RegisterFrontend(new AuroraFrontend(window))", composition, StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(module, "*.cs").Append(Path.Combine(RepositoryRoot(), "b-Code-Studio", "AuroraBusinessComposition.cs")))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("Bus.ConfirmationRouter", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Bus.Confirmation =", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Bus.FrontendExecutor", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Bus.UiContext =", source, StringComparison.Ordinal);
        }
    }

    private static AuroraDialogView Card(AuroraDialogRequest request) => new(request, standalone: true);

    /// <summary>
    /// REQ-UI-136：弹窗嵌在主窗体里——卡片进弹窗层、遮罩盖住后面的页面、不多开任何窗口；
    /// Esc 拒绝、主按钮接受，结果经 ShowAsync 交回，弹窗层随最后一张收起。
    /// </summary>
    [Fact]
    public void DialogsAreEmbeddedCardsInTheShellWindow()
    {
        UiTestHost.RunSta(() =>
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryAurora-dialog-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            var window = new ShellWindow(
                new ShellConfig { AppName = "Dialog Test", AppVersion = "1.1.0" },
                new MemoryLayoutStore(),
                new NullLog(),
                new MemorySettings(),
                dataDirectory, TestShell.NewBus(new NullLog(), out var shellCommands), shellCommands)
            {
                Width = 800,
                Height = 600,
                ShowInTaskbar = false,
            };

            try
            {
                window.Show();
                UiTestHost.Pump();
                var windowsBefore = Application.Current?.Windows.Count ?? 0;

                var first = window.Dialogs.ShowAsync(new AuroraDialogRequest
                {
                    Kind = AuroraDialogKind.Confirm,
                    Title = "删除场景",
                    Body = "删除？",
                    Danger = true,
                    DefaultCancel = true,
                });
                UiTestHost.Pump();
                Assert.Equal(1, window.Dialogs.OpenCount);
                Assert.Equal(windowsBefore, Application.Current?.Windows.Count ?? 0);
                var card = window.Dialogs.Top!;
                Assert.True(card.IsVisible);
                Assert.True(card.ActualWidth > 0 && card.ActualWidth <= 800);
                Assert.Same(card.CancelButton, card.DefaultButton);

                card.RaiseEvent(new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(card)!,
                    0,
                    System.Windows.Input.Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent });
                UiTestHost.Pump();
                Assert.True(first.IsCompleted);
                Assert.False(first.Result.Accepted);
                Assert.Equal(0, window.Dialogs.OpenCount);

                var second = window.Dialogs.ShowAsync(new AuroraDialogRequest
                {
                    Kind = AuroraDialogKind.Prompt,
                    Title = "另存场景",
                    Value = "出图",
                });
                UiTestHost.Pump();
                window.Dialogs.Top!.Accept();
                UiTestHost.Pump();
                Assert.True(second.Result.Accepted);
                Assert.Equal("出图", second.Result.Input);

                // 窗体关闭时摆着的弹窗按拒绝收场，同步等确认的一方才能返回。
                var pending = window.Dialogs.ShowAsync(new AuroraDialogRequest { Kind = AuroraDialogKind.Message, Body = "x" });
                UiTestHost.Pump();
                window.Dialogs.CancelAll();
                Assert.False(pending.Result.Accepted);
            }
            finally
            {
                window.Close();
                try { Directory.Delete(dataDirectory, recursive: true); }
                catch (IOException) { }
            }
        });
    }

    /// <summary>旧的独立弹窗窗口不许回来：源码里不得再出现 AuroraDialogWindow 类型或 ShowDialog 弹 Aurora 自己的窗。</summary>
    [Fact]
    public void TheStandaloneDialogWindowIsGone()
    {
        var shell = Path.Combine(RepositoryRoot(), "b-Code-Studio");
        foreach (var file in Directory.EnumerateFiles(shell, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("class AuroraDialogWindow", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AuroraDialogWindow.Show", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBox.Show", source, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "project.manifest.json")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("未找到 HistoryAurora 仓库根目录");
    }

    [Fact]
    public void ShellRegistersTheDialogCommandButDoesNotExecuteIt()
    {
        UiTestHost.RunSta(() =>
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), $"HistoryAurora-dialog-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dataDirectory);
            var window = new ShellWindow(
                new ShellConfig { AppName = "Dialog Test", AppVersion = "1.1.0" },
                new MemoryLayoutStore(),
                new NullLog(),
                new MemorySettings(),
                dataDirectory, TestShell.NewBus(new NullLog(), out var shellCommands1), shellCommands1)
            {
                Width = 800,
                Height = 600,
                ShowInTaskbar = false,
            };

            try
            {
                window.Show();
                UiTestHost.Pump();
                Assert.Contains(
                    window.Commands.Registry.All().Select(item => item.Name),
                    name => name == "aurora.ui.dialog");
            }
            finally
            {
                window.Close();
                try { Directory.Delete(dataDirectory, recursive: true); }
                catch (IOException) { }
            }
        });
    }

    private sealed class MemoryLayoutStore : HistoryAurora.Shell.Base.Docking.ILayoutStore
    {
        private string? _current;
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

        public string? ReadCurrent() => _current;
        public void WriteCurrent(string payload) => _current = payload;
        public void DeleteCurrent() => _current = null;
        public string? ReadNamed(string name) => _named.GetValueOrDefault(name);
        public void WriteNamed(string name, string payload) => _named[name] = payload;
        public IReadOnlyList<string> ListNamed() => _named.Keys.ToList();
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(HistoryVulcan.Core.Logging.ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
