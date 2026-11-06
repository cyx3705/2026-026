using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

namespace HistoryAurora.Module;

/// <summary>
/// Aurora Web 子进程（REQ-WEB-041）：模块拉起它、经标准输入输出对话，宿主退出时它随 stdin 关闭而退出。
/// </summary>
/// <remarks>
/// 协议与 <c>b-Code-Web/ParentLink.cs</c> 一一对应：发 <c>{"id","op","command","args"}</c>，
/// 收带 <see cref="Prefix"/> 前缀的 <c>{"id","result"}</c> 与 <c>{"event":"ready"|"clients"}</c>。
/// 子进程意外退出不自动拉回：下一次需要它（推送或 vulcan.app.show）时再起，避免端口被占时反复重启。
/// </remarks>
internal sealed class WebProcess : IDisposable
{
    internal const string Prefix = "@aurora ";
    internal const int DefaultPort = 5280;

    private readonly string _executable;
    private readonly IModuleLog _log;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _waiting = new(StringComparer.Ordinal);
    private Process? _process;
    private TaskCompletionSource<bool> _ready = NewReady();
    private bool _disposed;

    public WebProcess(string executable, IModuleLog log, int port = DefaultPort)
    {
        _executable = executable;
        _log = log;
        Port = port;
        Url = $"http://127.0.0.1:{port}/";
    }

    /// <summary>网页地址。</summary>
    public string Url { get; }

    /// <summary>监听端口。</summary>
    public int Port { get; }

    /// <summary>当前连着推送通道的网页数（子进程上报）。</summary>
    public int ClientCount { get; private set; }

    /// <summary>壳里的网页连上时触发，参数是壳报来的自身路径（REQ-WEB-045）。</summary>
    public event Action<string>? ShellSeen;

    /// <summary>等到至少一个网页连上推送通道；超时返回 false。</summary>
    public async Task<bool> WaitClientAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (ClientCount == 0)
        {
            if (DateTime.UtcNow >= deadline || !Running)
                return false;
            await Task.Delay(200).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>子进程是否在跑。</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
                return _process is { HasExited: false };
        }
    }

    /// <summary>确保子进程在跑；已在跑时立即返回。</summary>
    public void EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed || _process is { HasExited: false })
                return;
            if (!File.Exists(_executable))
            {
                _log.Log(ShellLogLevel.Error, "aurora.web", $"找不到 Aurora Web：{_executable}");
                return;
            }

            _ready = NewReady();
            ClientCount = 0;
            var start = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(_executable)!,
            };
            start.ArgumentList.Add("--parent-stdio");
            start.ArgumentList.Add("--port");
            start.ArgumentList.Add(Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _log.Log(ShellLogLevel.Warn, "aurora.web", e.Data);
            };
            process.Exited += (_, _) => OnExited(process);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _log.Log(ShellLogLevel.Info, "aurora.web", $"Aurora Web 已启动（进程 {process.Id}）：{Url}");
        }
    }

    /// <summary>等子进程报告监听就绪；超时或进程退出返回 false。</summary>
    public Task<bool> WaitReadyAsync(TimeSpan timeout)
    {
        Task<bool> ready;
        lock (_gate)
            ready = _ready.Task;
        return Task.WhenAny(ready, Task.Delay(timeout))
            .ContinueWith(done => done.Result == ready && ready.Result, TaskScheduler.Default);
    }

    /// <summary>推一条界面指令给最近活跃的网页并等结果。</summary>
    public async Task<CommandResult> InvokeAsync(string command, IReadOnlyDictionary<string, string> args, CancellationToken cancellation)
    {
        EnsureStarted();
        if (!await WaitReadyAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false))
            return CommandResult.Fail("Aurora Web 未就绪：端口可能被占用，详见宿主日志 aurora.web");

        var id = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = waiter;
        try
        {
            if (!Send(new { id, op = "invoke", command, args }))
                return CommandResult.Fail("Aurora Web 已退出");
            using var registration = cancellation.Register(() => waiter.TrySetCanceled(cancellation));
            return ToResult(await waiter.Task.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail($"{command} 已取消");
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    /// <summary>不等结果、推给全部网页。子进程没在跑时什么也不做。</summary>
    public void Broadcast(string command, IReadOnlyDictionary<string, string>? args = null)
    {
        if (Running)
            Send(new { op = "broadcast", command, args = args ?? new Dictionary<string, string>() });
    }

    /// <summary>把网页回执（success/message/data）还原成宿主回执。</summary>
    internal static CommandResult ToResult(JsonElement result)
    {
        var success = result.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;
        var message = result.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? ""
            : success ? "操作完成" : "执行失败";
        if (!success)
            return CommandResult.Fail(message);
        return result.TryGetProperty("data", out var data) && data.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? CommandResult.Ok(message, data.Clone())
            : CommandResult.Ok(message);
    }

    public void Dispose()
    {
        Process? process;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            process = _process;
            _process = null;
        }

        FailAll("Aurora 模块已卸载");
        if (process == null)
            return;
        try
        {
            // 关 stdin 是正常退出信号；等不到再强杀，免得旧进程占着端口挡住下一轮装载。
            process.StandardInput.Close();
            if (!process.WaitForExit(3000))
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private bool Send(object message)
    {
        Process? process;
        lock (_gate)
            process = _process;
        if (process is not { HasExited: false })
            return false;
        try
        {
            var line = JsonSerializer.Serialize(message);
            lock (_gate)
            {
                process.StandardInput.WriteLine(line);
                process.StandardInput.Flush();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private void OnLine(string? line)
    {
        if (line == null || !line.StartsWith(Prefix, StringComparison.Ordinal))
            return;
        try
        {
            using var document = JsonDocument.Parse(line.AsMemory(Prefix.Length));
            var root = document.RootElement;
            if (root.TryGetProperty("event", out var kind))
            {
                switch (kind.GetString())
                {
                    case "ready":
                        lock (_gate)
                            _ready.TrySetResult(true);
                        break;
                    case "clients" when root.TryGetProperty("count", out var count) && count.TryGetInt32(out var value):
                        ClientCount = value;
                        break;
                    case "shell" when root.TryGetProperty("path", out var path) && path.GetString() is { Length: > 0 } shell:
                        ShellSeen?.Invoke(shell);
                        break;
                }
                return;
            }
            if (root.TryGetProperty("id", out var id) && id.GetString() is { } key
                && root.TryGetProperty("result", out var result)
                && _waiting.TryRemove(key, out var waiter))
                waiter.TrySetResult(result.Clone());
        }
        catch (JsonException ex)
        {
            _log.Log(ShellLogLevel.Warn, "aurora.web", $"无法解析 Aurora Web 回执：{ex.Message}");
        }
    }

    private void OnExited(Process process)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_process, process))
                return;
            _ready.TrySetResult(false);
            ClientCount = 0;
        }
        FailAll("Aurora Web 已退出");
        if (!_disposed)
            _log.Log(ShellLogLevel.Error, "aurora.web", $"Aurora Web 意外退出（代码 {SafeExitCode(process)}）；下次需要时会重新启动");
    }

    private void FailAll(string reason)
    {
        foreach (var key in _waiting.Keys)
            if (_waiting.TryRemove(key, out var waiter))
                waiter.TrySetResult(JsonSerializer.SerializeToElement(new { success = false, message = reason }));
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static TaskCompletionSource<bool> NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
