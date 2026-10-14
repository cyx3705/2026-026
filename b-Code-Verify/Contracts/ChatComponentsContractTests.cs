using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Pages;
using HistoryAurora.Shell.Components.Selection;
using HistoryAurora.Shell.Components.Widgets;
using HistoryAurora.Shell.Neutral.Commands;
using HistoryAurora.Shell.Neutral.Logging;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>REQ-UI-143～146：聊天刷新不能丢掉目标、显示迟到的别人的消息，或者把回车误当作发送。</summary>
[Collection(TestCollections.Ui)]
public sealed class ChatComponentsContractTests
{
    /// <summary>REQ-UI-151：图片加载必须守住桥快照、只读边界和离开会话后的取消；预览不能另开窗口。</summary>
    [Fact]
    public void ImagesLoadPreviewRetryAndCancelWithoutCrossingConversations()
    {
        UiTestHost.RunSta(() =>
        {
            var bytes = ImageBytes(900, 450);
            var calls = new List<string>();
            var cancelled = new TaskCompletionSource();
            var registry = new CommandTable();
            registry.Register(new CommandDescriptor
            {
                Name = "demo.ui.image", Domain = "demo", CommandClass = "ui", Summary = "图片", Readonly = true,
                AllowUnspecifiedParameters = true, Handler = async c =>
                {
                    var path = c.RequireString("path");
                    lock (calls) calls.Add(c.RequireString("bridge") + ":" + path);
                    if (path == "slow")
                    {
                        using var registration = c.Cancellation.Register(() => cancelled.TrySetResult());
                        await Task.Delay(30000, c.Cancellation);
                    }
                    return CommandResult.Ok("图片", new Dictionary<string, string> { ["base64"] = Convert.ToBase64String(bytes) });
                },
            });
            registry.Register(new CommandDescriptor
            {
                Name = "demo.ui.data", Domain = "demo", CommandClass = "ui", Summary = "消息", Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("消息", new[] { ImageMessage("1", "first") })),
            });
            var channels = new SelectionChannels();
            channels.TryDeclare("demo.conversation", "HistoryDemo", "contacts", out _);
            channels.Publish("demo.conversation", new Dictionary<string, string>(Row("a", "Alice")) { ["bridge"] = "original" });
            var context = Context(channels, registry);
            var timeline = Assert.IsType<AuroraChatTimeline>(PageRenderer.Render(Page("images", new PageNode
            {
                Type = "chatTimeline", Id = "images", Source = "{selection.demo.conversation.title}",
                DataSource = new() { Command = "demo.ui.data" },
                ImageSource = new() { Command = "demo.ui.image", Args = new Dictionary<string, string>
                { ["bridge"] = "{selection.demo.conversation.bridge}", ["path"] = "{image.path}" } },
            }), context).Root);
            var host = new Window { Width = 720, Height = 540, Content = timeline, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            try
            {
                host.Show();
                var refresh = context.Refresher!.RefreshAsync(null, "images");
                Assert.True(UiTestHost.PumpUntil(() => refresh.IsCompleted));
                host.UpdateLayout(); UiTestHost.Pump();
                ChatImageCard Card() => ((ChatMessageCard)((ItemsControl)timeline.FindName("Messages")).Items[0]).Images[0];
                Assert.True(UiTestHost.PumpUntil(() => Card().Source != null));
                Assert.InRange(Card().Source!.PixelWidth, 1, 360);
                var button = Descendants<Button>(timeline).Single(b => b.DataContext == Card());
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, ((Border)timeline.FindName("ImageOverlay")).Visibility);
                Assert.True(UiTestHost.PumpUntil(() => ((Image)timeline.FindName("PreviewImage")).Source is BitmapSource bitmap && bitmap.PixelWidth == 900));
                ((Button)timeline.FindName("CloseImage")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, ((Border)timeline.FindName("ImageOverlay")).Visibility);
                Assert.Null(((Image)timeline.FindName("PreviewImage")).Source);
                var count = calls.Count;
                timeline.SetRows([ImageMessage("1", "first")]); host.UpdateLayout(); UiTestHost.Pump();
                Assert.True(UiTestHost.PumpUntil(() => Card().Source != null));
                Assert.Equal(count, calls.Count); // 普通消息刷新复用缩略图。
                timeline.SetRows([ImageMessage("2", "slow")]); host.UpdateLayout(); UiTestHost.Pump();
                Assert.True(UiTestHost.PumpUntil(() => { lock (calls) return calls.Contains("original:slow"); }));
                channels.Publish("demo.conversation", new Dictionary<string, string>(Row("b", "Bob")) { ["bridge"] = "other" });
                Assert.True(UiTestHost.PumpUntil(() => cancelled.Task.IsCompleted));
                Assert.Empty(((ItemsControl)timeline.FindName("Messages")).Items);
                Assert.DoesNotContain("other:slow", calls);
            }
            finally { host.Close(); }
        });
    }

    /// <summary>REQ-UI-151：纵图按长边缩放，损坏和超限数据不能进入位图；未保存原件时仍有可读提示。</summary>
    [Fact]
    public void ImageDecoderBoundsMemoryAndMissingOriginalsRemainExplicit()
    {
        UiTestHost.RunSta(() =>
        {
            var portrait = AuroraChatTimeline.DecodeImage(ImageBytes(300, 1200), 360);
            Assert.Equal(360, portrait.PixelHeight);
            Assert.Equal(90, portrait.PixelWidth);
            Assert.ThrowsAny<Exception>(() => AuroraChatTimeline.DecodeImage([1, 2, 3], 360));
            Assert.Throws<InvalidDataException>(() => AuroraChatTimeline.DecodeImage(new byte[8 * 1024 * 1024 + 1], 360));
            var unavailable = ChatImageCard.Parse("[{\"name\":\"图片\",\"path\":\"\"}]").Single();
            Assert.Equal("图片原件暂不可用", unavailable.Status);
            Assert.Single(ChatImageCard.Parse("broken"));
        });
    }

    private static Dictionary<string, string> ImageMessage(string id, string path)
        => new(Message(id, "查看图片", "in")) { ["images"] = System.Text.Json.JsonSerializer.Serialize(new[] { new { name = "示例图片.png", path } }) };

    private static byte[] ImageBytes(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 190; pixels[i + 1] = (byte)(100 + i / 4 / width % 100); pixels[i + 2] = 50; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    /// <summary>REQ-UI-150：只有可见且已经读到最新的消息才能确认本地已读；隐藏、失败和阅读历史均不能吞未读。</summary>
    [Fact]
    public void ReadActionRequiresVisibleLatestMessagesAndUsesSelectedIdentity()
    {
        UiTestHost.RunSta(() =>
        {
            var reads = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var registry = new CommandTable();
            registry.Register(new CommandDescriptor
            {
                Name = "demo.conv.read", Domain = "demo", CommandClass = "conv", Summary = "本地已读",
                AllowUnspecifiedParameters = true,
                Handler = CommandDescriptor.Sync(c => { reads.Enqueue(c.RequireString("conversation") + ":" + c.RequireString("through")); return CommandResult.Ok("ok"); }),
            });
            var channels = new SelectionChannels();
            channels.TryDeclare("demo.conversation", "HistoryDemo", "contacts", out _);
            channels.Publish("demo.conversation", Row("a", "Alice"));
            var context = Context(channels, registry);
            context.Actions!.DeclareLocal("HistoryDemo", [new ActionDeclaration
            {
                Id = "read", Title = "已读", Command = "demo.conv.read",
                Args = new Dictionary<string, string> { ["conversation"] = "{selection.demo.conversation.id}", ["through"] = "{readThrough}" },
            }]);
            var timeline = Assert.IsType<AuroraChatTimeline>(PageRenderer.Render(Page("messages", new PageNode
            {
                Type = "chatTimeline", Source = "{selection.demo.conversation.title}", ReadAction = "read",
            }), context).Root);
            var host = new Window { Content = timeline, Width = 400, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            var rows = Enumerable.Range(1, 40).Select(i => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(Message(i.ToString(), "消息", "in")) { ["ts_ms"] = i.ToString() }).ToList();
            try
            {
                timeline.SetRows(rows); UiTestHost.Pump();
                Assert.Empty(reads);
                host.Show(); host.UpdateLayout(); UiTestHost.Pump();
                Assert.True(UiTestHost.PumpUntil(() => reads.Count == 1));
                Assert.Equal("a:40", reads.Single());
                var viewport = (ScrollViewer)timeline.FindName("Viewport");
                viewport.ScrollToVerticalOffset(100); UiTestHost.Pump();
                rows.Add(new Dictionary<string, string>(Message("41", "新消息", "in")) { ["ts_ms"] = "41" });
                timeline.SetRows(rows); host.UpdateLayout(); UiTestHost.Pump();
                Assert.Single(reads);
                viewport.ScrollToEnd(); UiTestHost.Pump();
                Assert.True(UiTestHost.PumpUntil(() => reads.Count == 2));
                Assert.Equal("a:41", reads.Last());
                channels.Publish("demo.conversation", Row("b", "Bob"));
                timeline.SetRows([], "读取失败"); UiTestHost.Pump();
                Assert.Equal(2, reads.Count);
                timeline.SetRows([new Dictionary<string, string>(Message("b1", "Bob 内容", "in")) { ["ts_ms"] = "50" }]);
                host.UpdateLayout(); UiTestHost.Pump();
                Assert.True(UiTestHost.PumpUntil(() => reads.Count == 3));
                Assert.Equal("b:50", reads.Last());
            }
            finally { host.Close(); }
        });
    }

    /// <summary>REQ-UI-149：新消息使会话升到首位时，列表必须露出它，不能因为保留选中而仍停在旧位置。</summary>
    [Fact]
    public void PromotedConversationBecomesVisibleWithoutChangingSelection()
    {
        UiTestHost.RunSta(() =>
        {
            var list = new AuroraConversationList();
            var rows = Enumerable.Range(0, 30).Select(i => (IReadOnlyDictionary<string, string>)Row(i.ToString(), $"会话 {i}")).ToList();
            void Layout()
            {
                list.Measure(new Size(300, 300)); list.Arrange(new Rect(0, 0, 300, 300)); list.UpdateLayout();
                UiTestHost.Pump(); list.UpdateLayout();
            }
            list.SetRows(rows); Layout();
            var selector = (ListBox)list.FindName("Conversations");
            selector.SelectedIndex = 15;
            var viewport = Descendants<ScrollViewer>(list).Single();
            viewport.ScrollToVerticalOffset(10); Layout();
            Assert.True(viewport.VerticalOffset > 0);
            list.SetRows(rows); Layout();
            Assert.Equal(10, viewport.VerticalOffset);
            var promoted = rows[^1]; rows.RemoveAt(rows.Count - 1); rows.Insert(0, promoted);
            list.SetRows(rows); Layout();
            Assert.Equal("15", list.SelectedRow!["id"]);
            Assert.Equal("29", Assert.IsType<ConversationCard>(selector.Items[0]).Row["id"]);
            Assert.InRange(viewport.VerticalOffset, 0, 0.1);
        });
    }

    [Fact]
    public void RefreshPreservesSelectedIdentityAndPublishesTheFreshRow()
    {
        UiTestHost.RunSta(() =>
        {
            var channels = new SelectionChannels();
            var context = Context(channels);
            var list = Assert.IsType<AuroraConversationList>(PageRenderer.Render(Page("contacts",
                new PageNode { Type = "conversationList", Id = "contacts", Channel = "demo.conversation" }), context).Root);
            list.SetRows([Row("a", "Alice"), Row("b", "Bob")]);
            var selector = (ListBox)list.FindName("Conversations");
            selector.SelectedIndex = 1;
            Assert.Equal("b", channels.Value("demo.conversation", "id"));
            list.SetRows([Row("b", "Bob 最新"), Row("a", "Alice")]);
            Assert.Equal("b", list.SelectedRow!["id"]);
            Assert.Equal("Bob 最新", channels.Value("demo.conversation", "title"));
            list.SetRows([Row("a", "Alice")]);
            Assert.Null(channels.Current("demo.conversation"));
        });
    }

    [Fact]
    public void SwitchingConversationRejectsLateResultsAndClearsOnFailure()
    {
        UiTestHost.RunSta(() =>
        {
            var first = new TaskCompletionSource<CommandResult>();
            var second = new TaskCompletionSource<CommandResult>();
            var calls = new List<string>();
            var registry = new CommandTable();
            var offline = false;
            registry.Register(new CommandDescriptor
            {
                Name = "demo.ui.data", Domain = "demo", CommandClass = "ui", Summary = "消息", Readonly = true,
                AllowUnspecifiedParameters = true,
                Handler = c =>
                {
                    var id = c.RequireString("conversation");
                    lock (calls) calls.Add(id);
                    return offline ? Task.FromResult(CommandResult.Fail("离线")) : id == "a" ? first.Task : second.Task;
                },
            });
            var channels = new SelectionChannels();
            channels.TryDeclare("demo.conversation", "HistoryDemo", "contacts", out _);
            var context = Context(channels, registry);
            var timeline = Assert.IsType<AuroraChatTimeline>(PageRenderer.Render(Page("messages", new PageNode
            {
                Type = "chatTimeline", Id = "messages", Source = "{selection.demo.conversation.title}",
                DataSource = new PageDataSource
                {
                    Command = "demo.ui.data", Args = new Dictionary<string, string> { ["conversation"] = "{selection.demo.conversation.id}" },
                },
            }), context).Root);
            timeline.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            channels.Publish("demo.conversation", Row("a", "Alice"));
            var reload = context.Refresher!.RefreshAsync(null, "messages");
            Assert.True(UiTestHost.PumpUntil(() => { lock (calls) return calls.Contains("a"); }));
            channels.Publish("demo.conversation", Row("b", "Bob"));
            Assert.Empty(((ItemsControl)timeline.FindName("Messages")).Items);
            context.Refresher.Refresh(null, "messages");
            first.SetResult(CommandResult.Ok("ok", new[] { Message("a-msg", "旧会话内容", "in") }));
            Assert.True(UiTestHost.PumpUntil(() => { lock (calls) return calls.Contains("b"); }));
            Assert.Empty(((ItemsControl)timeline.FindName("Messages")).Items);
            second.SetResult(CommandResult.Ok("ok", new[] { Message("b-msg", "当前消息\n第二行", "out") }));
            Assert.True(UiTestHost.PumpUntil(() => reload.IsCompleted));
            var card = Assert.IsType<ChatMessageCard>(((ItemsControl)timeline.FindName("Messages")).Items[0]);
            Assert.True(card.Outgoing);
            Assert.Equal("当前消息\n第二行", card.Text);
            Assert.Equal("Bob", ((TextBlock)timeline.FindName("Heading")).Text);
            offline = true;
            var failed = context.Refresher.RefreshAsync(null, "messages");
            Assert.True(UiTestHost.PumpUntil(() => failed.IsCompleted));
            Assert.Empty(((ItemsControl)timeline.FindName("Messages")).Items);
            Assert.Contains("读取失败", ((TextBlock)timeline.FindName("Empty")).Text);
            timeline.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        });
    }

    [Fact]
    public void MultilineEnterAndLostFocusDoNotCommit()
    {
        UiTestHost.RunSta(() =>
        {
            var commits = 0;
            var registry = new CommandTable();
            registry.Register(new CommandDescriptor
            {
                Name = "demo.msg.send", Domain = "demo", CommandClass = "msg", Summary = "发送",
                Handler = CommandDescriptor.Sync(_ => { commits++; return CommandResult.Ok("ok"); }),
            });
            var context = Context(new SelectionChannels(), registry);
            context.Actions!.DeclareLocal("HistoryDemo", [new ActionDeclaration { Id = "send", Title = "发送", Command = "demo.msg.send" }]);
            var panel = PageRenderer.Render(Page("composer", new PageNode
            {
                Type = "panel", Id = "composer", Rows = [new()
                {
                    Widgets = [new() { Kind = "textbox", Id = "text", Label = "", Multiline = true, CommitAction = "send" }],
                }],
            }), context).Root;
            panel.Measure(new Size(600, 200)); panel.Arrange(new Rect(0, 0, 600, 200)); panel.UpdateLayout();
            var box = Descendants<TextBox>(panel).Single();
            Assert.True(box.AcceptsReturn);
            box.Text = "第一行\n第二行";
            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new FakePresentationSource(), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            UiTestHost.Pump();
            Assert.Equal(0, commits);
        });
    }

    [Fact]
    public void NewMessagesPreserveHistoryScrollAndFollowWhenAtTheBottom()
    {
        UiTestHost.RunSta(() =>
        {
            var timeline = new AuroraChatTimeline();
            var messages = Enumerable.Range(0, 40).Select(i => (IReadOnlyDictionary<string, string>)Message(i.ToString(), "消息正文", "in")).ToList();
            void Layout()
            {
                timeline.Measure(new Size(800, 400)); timeline.Arrange(new Rect(0, 0, 800, 400)); timeline.UpdateLayout();
                UiTestHost.Pump(); timeline.UpdateLayout();
            }
            timeline.SetRows(messages); Layout();
            var viewport = (ScrollViewer)timeline.FindName("Viewport");
            Assert.True(viewport.ScrollableHeight > 400);
            viewport.ScrollToVerticalOffset(120); UiTestHost.Pump();
            var offset = viewport.VerticalOffset;
            messages.Add(Message("new-1", "最新消息", "in"));
            timeline.SetRows(messages); Layout();
            Assert.InRange(Math.Abs(viewport.VerticalOffset - offset), 0, 1);
            viewport.ScrollToEnd(); UiTestHost.Pump();
            messages.Add(Message("new-2", "再来一条", "out"));
            timeline.SetRows(messages); Layout();
            Assert.InRange(viewport.ScrollableHeight - viewport.VerticalOffset, 0, 1);
        });
    }

    [Theory]
    [InlineData(false, 1180)]
    [InlineData(true, 1180)]
    [InlineData(false, 760)]
    public void ChatLayoutFitsViewportAndSupportsBothThemes(bool dark, int width)
    {
        UiTestHost.RunSta(() =>
        {
            var root = new Grid { Width = width, Height = 720, Margin = new Thickness(0) };
            root.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(dark ? "/HistoryAurora;component/Themes/AuroraTokens.Dark.xaml" : "/HistoryAurora;component/Themes/AuroraTokens.xaml", UriKind.Relative),
            });
            root.SetResourceReference(Panel.BackgroundProperty, "Aurora.Brush.Canvas");
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width == 760 ? 260 : 320) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var previewRegistry = new CommandTable();
            previewRegistry.Register(new CommandDescriptor
            {
                Name = "demo.preview.echo", Domain = "demo", CommandClass = "preview", Summary = "只读界面预览",
                Readonly = true, AllowUnspecifiedParameters = true, Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("预览")),
            });
            var context = Context(new SelectionChannels(), previewRegistry);
            context.Actions!.DeclareLocal("HistoryDemo", new[] { "demo.refresh", "demo.auto", "demo.send" }
                .Select(id => new ActionDeclaration { Id = id, Title = id, Command = "demo.preview.echo" }));
            var contacts = PageRenderer.Render(Page("contacts", new PageNode { Type = "stack", Children =
            [
                new() { Type = "panel", Id = "receive", Text = "消息中心", Rows =
                [
                    new() { Widgets = [new() { Kind = "switch", Id = "auto", Label = "自动接收", Value = "true", CommitAction = "demo.auto" }, new() { Kind = "button", Action = "demo.refresh", Text = "刷新", Icon = "refresh-cw" }] },
                    new() { Widgets = [new() { Kind = "textbox", Id = "search", Label = "搜索" }] },
                ] },
                new() { Type = "conversationList", Id = "contacts", Channel = "demo.conversation" },
            ] }), context).Root;
            var chat = PageRenderer.Render(Page("chat", new PageNode { Type = "stack", Children =
            [
                new() { Type = "chatTimeline", Id = "messages", Source = "{selection.demo.conversation.title}" },
                new() { Type = "panel", Id = "send", Text = "写消息", Rows =
                [
                    new() { Widgets = [new() { Kind = "textbox", Id = "text", Label = "", Multiline = true, Flex = true }] },
                    new() { Widgets = [new() { Kind = "text", Text = "Enter 换行 · 发送前确认收件人", Flex = true }, new() { Kind = "button", Action = "demo.send", Text = "发送", EnabledWhen = new() { Selected = "demo.conversation" } }] },
                ] },
            ] }), context).Root;
            contacts.Margin = new Thickness(12, 12, 6, 12); chat.Margin = new Thickness(6, 12, 12, 12);
            root.Children.Add(contacts); Grid.SetColumn(chat, 1); root.Children.Add(chat);
            root.Measure(new Size(width, 720)); root.Arrange(new Rect(0, 0, width, 720)); root.UpdateLayout();
            var list = Descendants<AuroraConversationList>(root).Single();
            list.SetRows([
                Conversation("team", "产品讨论群", "李明：界面这样调整可以吗？", "09:42", "6", "QQ · 群聊"),
                Conversation("lin", "林晓", "好，下午见", "09:38", "2", "QQ · 单聊"),
                Conversation("work", "项目协作", "文档已经更新", "昨天", "", "钉钉 · 群聊"),
                Conversation("family", "家人", "记得吃午饭 ☀", "昨天", "", "QQ · 群聊"),
            ]);
            ((ListBox)list.FindName("Conversations")).SelectedIndex = 0;
            var timeline = Descendants<AuroraChatTimeline>(root).Single();
            timeline.SetConversation("team", "产品讨论群", "QQ · 群聊 · 12 人");
            timeline.SetRows([
                Message("1", "早上好！新的消息界面大家看一下。", "in", "李明", "09:40"),
                Message("2", "会话列表可以直接看到摘要和未读数了。", "in", "林晓", "09:41"),
                Message("3", "收到，消息气泡也更清楚。\n多行内容会保留原来的换行。", "out", "我", "09:41"),
                Message("4", "长消息会自动换行，也可以选中文字复制。这样读群聊方便很多，往上看历史时新消息不会把页面拉回底部。", "in", "李明", "09:42"),
            ]);
            root.UpdateLayout(); UiTestHost.Pump(); root.UpdateLayout();
            Assert.True(timeline.ActualHeight > 300);
            Assert.True(list.ActualHeight > 400);
            Assert.All(Descendants<ScrollViewer>(root), scroll => Assert.True(scroll.ScrollableWidth < 1, "聊天界面不能出现横向溢出"));
            var directory = Environment.GetEnvironmentVariable("AURORA_CHAT_PREVIEW_DIRECTORY");
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap(width, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"fama-chat-{(dark ? "dark" : "light")}-{width}.png")); encoder.Save(output);
            }
        });
    }

    private static PageDescription Page(string id, PageNode content) => new() { Id = id, Title = id, Content = content };
    private static Dictionary<string, string> Row(string id, string title) => new() { ["id"] = id, ["title"] = title };
    private static Dictionary<string, string> Conversation(string id, string title, string last, string when, string unread, string source)
        => new() { ["id"] = id, ["title"] = title, ["last"] = last, ["when"] = when, ["unread"] = unread, ["source"] = source };
    private static Dictionary<string, string> Message(string id, string text, string direction, string who = "我", string time = "09:42")
        => new() { ["id"] = id, ["text"] = text, ["direction"] = direction, ["who"] = who, ["time"] = time };
    private static PageRenderContext Context(SelectionChannels channels, CommandTable? registry = null)
    {
        var bus = TestShell.Bus(registry ?? new CommandTable()); var log = new MemoryLog();
        return new() { Owner = "HistoryDemo", Bus = bus, Log = log, Channels = channels, Refresher = new(channels), Actions = new(bus, log) };
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private sealed class MemoryLog : IShellLog
    {
        public event EventHandler<ShellLogEntry>? EntryAdded;
        public void Log(ShellLogLevel level, string category, string message) => EntryAdded?.Invoke(this, new(DateTime.Now, level, category, message));
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
    private sealed class FakePresentationSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
