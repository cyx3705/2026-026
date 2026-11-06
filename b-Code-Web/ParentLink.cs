using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace HistoryAurora.Web;

/// <summary>
/// REQ-WEB-041：被 Aurora 模块拉起时，经标准输入输出与模块对话。
/// </summary>
/// <remarks>
/// 一行一条 JSON。模块 → 本进程（stdin）：
/// <c>{"id":"…","op":"invoke","command":"aurora.ui.dialog","args":{…}}</c>、
/// <c>{"op":"broadcast","command":"…","args":{…}}</c>。
/// 本进程 → 模块（stdout，带 <see cref="Prefix"/> 前缀，别的输出一律被模块忽略）：
/// <c>{"id":"…","result":{…}}</c>、<c>{"id":"…","op":"log.snapshot","text":"…"}</c>、
/// <c>{"event":"ready","url":"…"}</c>、<c>{"event":"clients","count":n}</c>、<c>{"event":"shell","path":"…"}</c>（壳里的网页连上了，path 是壳的路径）。
/// 控制台拉取走 <c>log.snapshot</c>：模块用安静调用读日志，再把结果写回 stdin。
/// 不能经会回显的指令通道读，否则每一轮回执都会变成下一轮匹配到的新日志。
/// stdin 关闭就是模块没了：本进程随之退出，不留孤儿服务占着端口。
/// </remarks>
public static class ParentLink
{
    /// <summary>协议行前缀；模块只解析带它的行。</summary>
    public const string Prefix = "@aurora ";

    private static readonly object WriteGate = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> Pending = new(StringComparer.Ordinal);
    private static TextWriter? _out;

    /// <summary>命令行带 --parent-stdio 时启用。</summary>
    public static bool Requested(string[] args)
        => args.Any(arg => arg.Equals("--parent-stdio", StringComparison.OrdinalIgnoreCase));

    /// <summary>接管 stdout 并开始读 stdin；返回读循环任务。</summary>
    public static Task Start(FrontendChannel channel, IHostApplicationLifetime lifetime, string url)
    {
        _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        channel.ClientsChanged += count => Send(new { @event = "clients", count });
        channel.ShellSeen += path => Send(new { @event = "shell", path });
        lifetime.ApplicationStarted.Register(() => Send(new { @event = "ready", url }));
        return Task.Run(async () =>
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (TryComplete(line))
                    continue;
                _ = HandleAsync(channel, line, lifetime.ApplicationStopping);
            }
            lifetime.StopApplication();
        });
    }

    /// <summary>这条文本是控制台在读自己的日志，不能再走会回显的指令通道。</summary>
    public static bool IsLogSnapshot(string text)
    {
        var trimmed = text.TrimStart();
        const string name = "aurora.log.snapshot";
        return trimmed.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == name.Length || char.IsWhiteSpace(trimmed[name.Length]));
    }

    /// <summary>
    /// 请模块安静读取控制台。返回值是指令结果 JSON（success / message / data），不经过会写日志的执行通道。
    /// </summary>
    public static async Task<JsonElement> ReadLogSnapshotAsync(string text, CancellationToken cancellation)
    {
        var id = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Pending.TryAdd(id, waiter))
            return Failure("日志读取标识冲突");
        try
        {
            if (!Send(new { id, op = "log.snapshot", text }))
                return Failure("Aurora 模块未连接，无法读取控制台日志");
            using var registration = cancellation.Register(() => waiter.TrySetCanceled(cancellation));
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            Pending.TryRemove(id, out _);
        }
    }

    private static bool TryComplete(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                return false;
            if (root.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } key
                && Pending.TryRemove(key, out var waiter))
                waiter.TrySetResult(result.Clone());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement Failure(string message)
        => JsonSerializer.SerializeToElement(new { success = false, message });

    private static async Task HandleAsync(FrontendChannel channel, string line, CancellationToken stopping)
    {
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            var op = root.GetProperty("op").GetString();
            var command = root.GetProperty("command").GetString() ?? "";
            var args = root.TryGetProperty("args", out var argsValue) && argsValue.ValueKind == JsonValueKind.Object
                ? argsValue.Clone()
                : JsonSerializer.SerializeToElement(new { });
            if (op == "broadcast")
            {
                var count = channel.Broadcast(command, args);
                if (id != null)
                    Send(new { id, result = new { success = true, message = $"已通知 {count} 个网页" } });
                return;
            }
            var result = await channel.InvokeAsync(command, args, stopping).ConfigureAwait(false);
            if (id != null)
                Send(new { id, result });
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (id != null)
                Send(new { id, result = new { success = false, message = "Aurora Web 收到无效请求：" + ex.Message } });
        }
    }

    private static bool Send(object message)
    {
        var writer = _out;
        if (writer == null)
            return false;
        var text = Prefix + JsonSerializer.Serialize(message);
        lock (WriteGate)
        {
            try
            {
                writer.WriteLine(text);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
