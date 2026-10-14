using System.Windows;
using System.Globalization;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryAurora.Shell.Neutral;
using HistoryAurora.Shell.Components.Actions;
using HistoryAurora.Shell.Components.Selection;
using HistoryAurora.Shell.Components.Table;
using HistoryAurora.Shell.Components.Widgets;

namespace HistoryAurora.Shell.Components.Pages;

public static partial class PageRenderer
{
    private static FrameworkElement BuildConversationList(PageNode node, RenderState state)
    {
        var list = new AuroraConversationList();
        if (node.Channel is { Length: > 0 } channel)
            state.PublishConversation(list, channel, node.Id);
        if (node.DataSource is { } source)
            state.BindChatRows(list, source, node.Id, list.SetRows);
        return list;
    }

    private static FrameworkElement BuildChatTimeline(PageNode node, RenderState state)
    {
        var timeline = new AuroraChatTimeline();
        if (node.ReadAction is { Length: > 0 } readAction)
            state.BindReadAction(timeline, readAction);
        if (node.Source is { Length: > 0 })
            state.FollowConversation(timeline, node);
        if (node.DataSource is { } source)
            state.BindChatRows(timeline, source, node.Id, (rows, empty) =>
            {
                timeline.ImageLoader = state.ImageLoader(node.ImageSource);
                timeline.SetRows(rows, empty);
            });
        return timeline;
    }

    private sealed partial class RenderState
    {
        public Func<string, CancellationToken, Task<byte[]>>? ImageLoader(PageDataSource? source)
        {
            if (source == null) return null;
            if (!Bus.Registry.TryGet(source.Command, out var descriptor) || !descriptor.Readonly)
            {
                WarnUnbound("chatTimeline.imageSource 必须指向已注册的只读指令");
                return null;
            }
            // 在行集被接受时固定选择参数；迟到的图片绝不借用后来选中的桥。
            var missing = new List<string>();
            var args = (source.Args ?? new Dictionary<string, string>()).ToDictionary(p => p.Key,
                p => ActionRegistry.ExpandPlaceholders(p.Value, name => name == "image.path" ? "{image.path}" : Channels?.Resolve(name), missing));
            if (missing.Count > 0) return null;
            return async (path, cancellation) =>
            {
                // 只替换声明里的图片占位符，选择值里出现的花括号不再当模板解析。
                var text = source.Command;
                foreach (var pair in args)
                    text += " " + pair.Key + "=" + CommandParser.QuoteArg(pair.Value.Replace("{image.path}", path, StringComparison.Ordinal));
                var result = await Bus.InvokeAsync(text, "UI", cancellation).ConfigureAwait(false);
                if (!result.Success) throw new InvalidOperationException(result.Message);
                if (!CommandResultData.TryGetJsonText(result.Data, result.Message, out var payload))
                    throw new InvalidOperationException("图片源未返回有效数据");
                using var json = JsonDocument.Parse(payload);
                var encoded = json.RootElement.GetProperty("base64").GetString() ?? "";
                if (encoded.Length > 11184812) throw new InvalidOperationException("图片超过 8 MB");
                var bytes = Convert.FromBase64String(encoded);
                if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) throw new InvalidOperationException("图片大小无效或超过 8 MB");
                return bytes;
            };
        }

        public void BindReadAction(AuroraChatTimeline timeline, string actionId)
        {
            var binding = ResolveAction(actionId);
            if (!binding.Ok)
            {
                timeline.ToolTip = binding.Error;
                WarnUnbound(binding.Error!);
                return;
            }
            timeline.ReadRequested += async through =>
            {
                // 先固定动作参数，等待总线期间切会话不会把旧消息的已读水位写给新目标。
                var command = ActionRegistry.BuildCommandText(binding.Action!,
                    SelectionChannels.Chain(Channels, name => name == "readThrough" ? through.ToString(CultureInfo.InvariantCulture) : null), out var error);
                if (command == null) { WarnUnbound(error); return; }
                try
                {
                    var result = await Task.Run(() => Bus.InvokeAsync(command, "UI"));
                    if (!result.Success) { timeline.ToolTip = result.Message; WarnUnbound(result.Message); }
                }
                catch (Exception ex) { timeline.ToolTip = ex.Message; WarnUnbound(ex.Message); }
            };
        }

        public void PublishConversation(AuroraConversationList list, string channel, string? nodeId)
        {
            if (Channels is not { } channels)
            {
                WarnUnbound("会话列表选择通道台账不可用：" + channel);
                return;
            }
            if (!channels.TryDeclare(channel, Owner, $"{Owner}/{PageId}#{nodeId ?? "(无 id)"}", out var error))
            {
                WarnUnbound(error);
                return;
            }
            list.SelectionChanged += (_, _) => channels.Publish(channel, list.SelectedRow);
            channels.Publish(channel, list.SelectedRow);
        }

        public void FollowConversation(AuroraChatTimeline timeline, PageNode node)
        {
            if (!SelectionChannels.TrySplitReference(Unwrap(node.Source), out var channel, out var column) || Channels is not { } channels)
            {
                WarnUnbound("chatTimeline.source 必须是可用的 {selection.<通道>.<列>} 引用");
                return;
            }
            channels.Reference($"{Owner}/{PageId}#{node.Id}", [channel]);
            void Apply()
            {
                var row = channels.Current(channel);
                timeline.SetConversation(row?.GetValueOrDefault("id"), channels.Value(channel, column) ?? node.Text ?? "消息",
                    row?.GetValueOrDefault("subtitle") ?? (row == null ? "选择左侧会话开始聊天" : ""));
            }
            void Changed(object? sender, SelectionChannelChangedEventArgs e)
            {
                if (e.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase)) Apply();
            }
            // 浮出与切页会反复 Loaded；只在活视图上订阅，卸载的页面不留事件引用。
            timeline.Loaded += (_, _) => { channels.Changed -= Changed; channels.Changed += Changed; Apply(); };
            timeline.Unloaded += (_, _) => channels.Changed -= Changed;
            Apply();
        }

        /// <summary>REQ-UI-145：聊天数据合并并发刷新、丢弃迟到结果；掉线清空并提示，避免旧消息冒充当前会话。</summary>
        public void BindChatRows(FrameworkElement element, PageDataSource source, string? nodeId,
            Action<IReadOnlyList<IReadOnlyDictionary<string, string>>, string> apply)
        {
            Task? active = null;
            var requested = 0;
            Task Reload()
            {
                requested++;
                return active is { IsCompleted: false } ? active : active = ReloadCore();
            }
            async Task ReloadCore()
            {
                int generation;
                do
                {
                    generation = requested;
                    var text = Compose(source, out _);
                    if (text == null)
                    {
                        apply([], "选择左侧会话开始聊天");
                        continue;
                    }
                    try
                    {
                        var payload = await Task.Run(() => FetchAsync(text));
                        if (generation != requested || text != Compose(source, out _)) continue;
                        if (payload == null) { apply([], "读取失败，请刷新重试"); continue; }
                        var update = TableUpdate.Read(payload);
                        if (update.IsDelta) throw new InvalidOperationException("聊天组件只接收完整行集或 snapshot");
                        update.ValidateKeys("id");
                        apply(update.Rows, element is AuroraConversationList ? "没有匹配的会话" : "还没有消息");
                    }
                    catch (Exception ex)
                    {
                        if (generation != requested || text != Compose(source, out _)) continue;
                        apply([], "读取失败，请刷新重试");
                        WarnUnbound("聊天数据刷新失败：" + ex.Message);
                    }
                } while (generation != requested);
            }
            Bind(element, source, nodeId, Reload);
        }
    }
}
