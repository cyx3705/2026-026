using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HistoryAurora.Shell;
using Xunit;

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
            content.Close();
        });
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

    private sealed class MemoryLayoutStore : HistoryVulcan.Core.Storage.ILayoutStore
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
