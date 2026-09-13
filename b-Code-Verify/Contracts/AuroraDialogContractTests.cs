using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell.Composition;
using Xunit;
using HistoryAurora.Shell.Base.Dialogs;

namespace HistoryAurora.Verify;

/// <summary>
/// 弹窗组件的契约：独立 Window 必须自带主题字典，不得依赖 Application.Current。
///
/// 前端切出后模块自己的对话框全坏，根因就是这条——<c>DynamicResource Aurora.*</c>
/// 在没有合并令牌的顶层窗口里静默退化成系统外观。本轮只交付 Aurora 侧组件，
/// 模块适配下一轮。
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
                var dialog = AuroraDialogWindow.Create(new AuroraDialogRequest
                {
                    Kind = AuroraDialogKind.Message,
                    Title = "关于",
                    Body = "hello",
                });

                var surface = Assert.IsType<SolidColorBrush>(dialog.TryFindResource("Aurora.Brush.Surface"));
                Assert.Equal(Colors.White, surface.Color);
                Assert.NotNull(dialog.TryFindResource("Aurora.Button.Accent"));
                Assert.NotNull(dialog.TryFindResource("Aurora.Dialog.Chrome"));
                Assert.Equal(WindowStyle.None, dialog.WindowStyle);
                Assert.Equal("关于", dialog.CaptionTitle?.Text);
                Assert.Equal("关闭", dialog.PrimaryButton.Content);
                Assert.Null(dialog.CancelButton);
                dialog.Close();
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
            var dialog = AuroraDialogWindow.Create(
                new AuroraDialogRequest { Kind = AuroraDialogKind.Message, Body = "dark" },
                dark: true);

            var surface = Assert.IsType<SolidColorBrush>(dialog.TryFindResource("Aurora.Brush.Surface"));
            Assert.Equal((Color)ColorConverter.ConvertFromString("#1D201F")!, surface.Color);
            dialog.Close();
        });
    }

    [Fact]
    public void ConfirmPromptAndContentBuildTheExpectedChrome()
    {
        UiTestHost.RunSta(() =>
        {
            var confirm = AuroraDialogWindow.Create(new AuroraDialogRequest
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
            Assert.True(confirm.CancelButton!.IsDefault);
            Assert.Contains("秒内未操作", confirm.CountdownText?.Text);
            confirm.Close();

            var prompt = AuroraDialogWindow.Create(new AuroraDialogRequest
            {
                Kind = AuroraDialogKind.Prompt,
                Title = "生成恢复提交",
                Body = "恢复提交说明",
                Value = "revert abc",
            });
            Assert.Equal("revert abc", prompt.PromptBox?.Text);
            Assert.Equal("确定", prompt.PrimaryButton.Content);
            prompt.Close();

            var content = AuroraDialogWindow.Create(new AuroraDialogRequest
            {
                Kind = AuroraDialogKind.Content,
                Title = "预览",
                Body = "abc123 提交",
                Content = "diff --git a/file",
            });
            Assert.Equal("abc123 提交", content.BodyText?.Text);
            Assert.Equal("diff --git a/file", content.ContentBox?.Text);
            Assert.IsType<TextBox>(content.ContentBox);
            Assert.Equal(WindowStyle.None, content.WindowStyle);
            Assert.Equal("预览", content.CaptionTitle?.Text);
            Assert.NotNull(content.TryFindResource("Aurora.WindowButton.Close"));
            content.Close();
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
            var dialog = AuroraDialogWindow.Create(new AuroraDialogRequest
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
            Assert.True(dialog.CancelButton!.IsCancel);
            dialog.Close();
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
        Assert.Contains("new MessageBoxConfirmation(window)", frontend, StringComparison.Ordinal);

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
                dataDirectory)
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

    private sealed class MemorySettings : HistoryVulcan.Core.Storage.ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class NullLog : HistoryVulcan.Core.Logging.IShellLog
    {
        public void Log(HistoryVulcan.Core.Logging.ShellLogLevel level, string category, string message) { }
        public event EventHandler<HistoryVulcan.Core.Logging.ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<HistoryVulcan.Core.Logging.ShellLogEntry> Snapshot() => [];
    }
}
