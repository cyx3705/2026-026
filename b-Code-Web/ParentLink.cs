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
/// <c>{"id":"…","result":{…}}</c>、<c>{"event":"ready","url":"…"}</c>、<c>{"event":"clients","count":n}</c>、<c>{"event":"shell","path":"…"}</c>（壳里的网页连上了，path 是壳的路径）。
/// stdin 关闭就是模块没了：本进程随之退出，不留孤儿服务占着端口。
/// </remarks>
public static class ParentLink
{
    /// <summary>协议行前缀；模块只解析带它的行。</summary>
    public const string Prefix = "@aurora ";

    private static readonly object WriteGate = new();
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
                _ = HandleAsync(channel, line, lifetime.ApplicationStopping);
            }
            lifetime.StopApplication();
        });
    }

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

    private static void Send(object message)
    {
        var writer = _out;
        if (writer == null)
            return;
        var text = Prefix + JsonSerializer.Serialize(message);
        lock (WriteGate)
        {
            try
            {
                writer.WriteLine(text);
            }
            catch (IOException)
            {
            }
        }
    }
}
