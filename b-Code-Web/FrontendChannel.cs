using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace HistoryAurora.Web;

/// <summary>
/// REQ-WEB-040：宿主 → 浏览器的推送通道。
/// </summary>
/// <remarks>
/// 宿主里的模块（Janus 弹窗确认、Strenua 刷新表格……）经总线调 aurora.* 界面指令，
/// Aurora 模块把它转到本服务，本服务经 SSE 推给一个打开着的网页，网页执行后把结果 POST 回来。
/// 目标取「最近活跃」的网页：最后一次获得焦点或变为可见的那个；都不可见时取最近连上的。
/// 没有网页连着时直接回失败，不排队——宿主那边的调用方要的是现在的界面，不是将来的。
/// </remarks>
public sealed class FrontendChannel
{
    private sealed class Client(string id, string kind)
    {
        public string Id { get; } = id;
        public string Kind { get; } = kind;
        public Channel<string> Outbox { get; } = Channel.CreateUnbounded<string>();
        public long ActiveStamp;
        public bool Visible = true;
        public readonly ConcurrentDictionary<string, byte> Pending = new();
    }

    private readonly ConcurrentDictionary<string, Client> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _waiting = new(StringComparer.Ordinal);
    private long _clock;

    /// <summary>当前连着的网页数。</summary>
    public int ClientCount => _clients.Count;

    /// <summary>有网页连上或断开时触发；参数为当前连接数。</summary>
    public event Action<int>? ClientsChanged;

    /// <summary>SSE 长连接：登记网页，持续写出推送，直到网页断开。</summary>
    public async Task StreamAsync(HttpContext context, string id, string kind)
    {
        var client = new Client(id, kind) { ActiveStamp = Interlocked.Increment(ref _clock) };
        if (_clients.TryRemove(id, out var previous))
            Drop(previous, "同一网页重新连接");
        _clients[id] = client;
        ClientsChanged?.Invoke(_clients.Count);

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(20));
            var beat = heartbeat.WaitForNextTickAsync(context.RequestAborted).AsTask();
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var read = client.Outbox.Reader.WaitToReadAsync(context.RequestAborted).AsTask();
                var done = await Task.WhenAny(read, beat);
                if (done == beat)
                {
                    await beat;
                    await context.Response.WriteAsync(": ping\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    beat = heartbeat.WaitForNextTickAsync(context.RequestAborted).AsTask();
                    continue;
                }
                if (!await read)
                    break;
                while (client.Outbox.Reader.TryRead(out var frame))
                    await context.Response.WriteAsync(frame, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            if (_clients.TryGetValue(id, out var current) && ReferenceEquals(current, client))
                _clients.TryRemove(id, out _);
            Drop(client, "网页已关闭");
            ClientsChanged?.Invoke(_clients.Count);
        }
    }

    /// <summary>网页获得焦点或可见性变化时上报，用来挑推送目标。</summary>
    public bool MarkActive(string id, bool visible)
    {
        if (!_clients.TryGetValue(id, out var client))
            return false;
        client.Visible = visible;
        if (visible)
            client.ActiveStamp = Interlocked.Increment(ref _clock);
        return true;
    }

    /// <summary>网页回传一条推送的执行结果。</summary>
    public bool Complete(string requestId, JsonElement result)
        => _waiting.TryRemove(requestId, out var waiter) && waiter.TrySetResult(result.Clone());

    /// <summary>
    /// 推一条界面指令给最近活跃的网页并等结果。
    /// </summary>
    /// <param name="command">完整指令名，例如 aurora.ui.dialog。</param>
    /// <param name="args">参数对象（名 → 字符串值）。</param>
    /// <param name="cancellation">宿主那边的取消；取消时网页上未完成的界面不撤回，结果丢弃。</param>
    public async Task<JsonElement> InvokeAsync(string command, JsonElement args, CancellationToken cancellation)
    {
        var target = PickTarget();
        if (target == null)
            return Failure("没有打开的 Aurora 网页：请先在浏览器打开 Aurora（vulcan.app.show）");

        var requestId = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[requestId] = waiter;
        target.Pending[requestId] = 0;
        var payload = JsonSerializer.Serialize(new { id = requestId, command, args });
        target.Outbox.Writer.TryWrite($"event: invoke\ndata: {payload}\n\n");
        try
        {
            using var registration = cancellation.Register(() => waiter.TrySetCanceled(cancellation));
            return await waiter.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure($"{command} 已取消");
        }
        finally
        {
            _waiting.TryRemove(requestId, out _);
            target.Pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>不等结果、推给全部网页（例如宿主装载完成后整轮重拉页面）。</summary>
    public int Broadcast(string command, JsonElement args)
    {
        var payload = JsonSerializer.Serialize(new { id = "", command, args });
        var frame = $"event: invoke\ndata: {payload}\n\n";
        var count = 0;
        foreach (var client in _clients.Values)
            if (client.Outbox.Writer.TryWrite(frame))
                count++;
        return count;
    }

    /// <summary>失败回执的统一形状，与宿主 CommandResult 序列化一致。</summary>
    public static JsonElement Failure(string message)
        => JsonSerializer.SerializeToElement(new { success = false, message });

    private Client? PickTarget()
        => _clients.Values
            .OrderByDescending(client => client.Visible)
            .ThenByDescending(client => client.Kind == "desktop")
            .ThenByDescending(client => client.ActiveStamp)
            .FirstOrDefault();

    private void Drop(Client client, string reason)
    {
        client.Outbox.Writer.TryComplete();
        foreach (var requestId in client.Pending.Keys)
            if (_waiting.TryRemove(requestId, out var waiter))
                waiter.TrySetResult(Failure(reason + "，界面请求未完成"));
    }
}
