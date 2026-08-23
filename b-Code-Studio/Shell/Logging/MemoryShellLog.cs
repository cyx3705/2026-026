using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Logging;

/// <summary>
/// 界面进程内日志。不写宿主日志文件，避免与宿主 ShellLog 抢同一滚动文件。
/// 控制台窗口订阅本实例；指令回显由总线 Executed 转发进来。
/// </summary>
public sealed class MemoryShellLog : IShellLog
{
    private const int BufferLimit = 20_000;
    private readonly object _gate = new();
    private readonly Queue<ShellLogEntry> _buffer = new();

    public event EventHandler<ShellLogEntry>? EntryAdded;

    public void Log(ShellLogLevel level, string category, string message)
    {
        var entry = new ShellLogEntry(DateTime.Now, level, category, message);
        lock (_gate)
        {
            _buffer.Enqueue(entry);
            while (_buffer.Count > BufferLimit)
                _buffer.Dequeue();
        }

        EntryAdded?.Invoke(this, entry);
    }

    public IReadOnlyList<ShellLogEntry> Snapshot()
    {
        lock (_gate)
            return _buffer.ToList();
    }
}
